using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;

namespace ArisenEngine.Rendering;

internal readonly record struct RenderDocCaptureArtifactExpectation(
    RenderDocCaptureLease Lease,
    uint CaptureCountBeforeStart,
    string PathTemplate)
{
    public bool IsValid =>
        Lease.IsValid &&
        !string.IsNullOrWhiteSpace(PathTemplate);
}

/// <summary>
/// Provides integration with the RenderDoc API for frame captures.
/// RenderDoc must be explicitly loaded before vkCreateInstance for captures to work.
/// This service only binds to that already-loaded module and never injects it late.
/// </summary>
public class RenderDocService : IDisposable
{
    private static RenderDocService? s_Instance;
    public static RenderDocService Instance => s_Instance ??= new RenderDocService();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int pfnRENDERDOC_GetAPI(int version, out IntPtr api);

    [StructLayout(LayoutKind.Sequential)]
    private struct RENDERDOC_API_1_6_0
    {
        public IntPtr GetVTablePointer; // We'll use delegates instead of raw VTable for simplicity if possible, but RenderDoc API is a struct of function pointers.
    }

    // Function pointer signatures for RENDERDOC_API_1.6.0
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void pfnShutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void pfnSetCaptureOptionU32(int option, uint value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void pfnSetCaptureOptionF32(int option, float value);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint pfnGetCaptureOptionU32(int option);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float pfnGetCaptureOptionF32(int option);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void pfnTriggerCapture();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint pfnIsTargetControlConnected();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void pfnLaunchReplayUI(uint connectTargetControl, [MarshalAs(UnmanagedType.LPStr)] string cmdline);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void pfnSetActiveWindow(IntPtr device, IntPtr windowHandle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void pfnStartFrameCapture(IntPtr device, IntPtr windowHandle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint pfnIsFrameCapturing();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint pfnEndFrameCapture(IntPtr device, IntPtr windowHandle);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void pfnSetCaptureFilePathTemplate(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string pathTemplate);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint pfnGetNumCaptures();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint pfnGetCapture(uint idx, IntPtr logFile, out uint pathLen, out ulong timestamp);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void pfnTriggerMultiFrameCapture(uint numFrames);

    [StructLayout(LayoutKind.Sequential)]
    private struct RenderDocVTable
    {
        public IntPtr GetAPIVersion;
        public IntPtr SetCaptureOptionU32;
        public IntPtr SetCaptureOptionF32;
        public IntPtr GetCaptureOptionU32;
        public IntPtr GetCaptureOptionF32;

        public IntPtr SetFocusToggleKeys;
        public IntPtr SetCaptureKeys;

        public IntPtr GetOverlayBits;
        public IntPtr MaskOverlayBits;

        public IntPtr Shutdown; // union with RemoveHooks
        public IntPtr UnloadCrashHandler;

        public IntPtr SetCaptureFilePathTemplate; // union with SetLogFilePathTemplate
        public IntPtr GetCaptureFilePathTemplate; // union with GetLogFilePathTemplate

        public IntPtr GetNumCaptures;
        public IntPtr GetCapture;

        public IntPtr TriggerCapture;

        public IntPtr IsTargetControlConnected; // union with IsRemoteAccessConnected

        public IntPtr LaunchReplayUI;

        public IntPtr SetActiveWindow;

        public IntPtr StartFrameCapture;
        public IntPtr IsFrameCapturing;
        public IntPtr EndFrameCapture;

        public IntPtr TriggerMultiFrameCapture;

        public IntPtr SetCaptureFileComments;
        public IntPtr DiscardFrameCapture;
        public IntPtr ShowReplayUI;
        public IntPtr SetCaptureTitle;
    }

    private IntPtr m_Library;
    private IntPtr m_ApiPtr;
    private RenderDocVTable m_VTable;
    private readonly object m_InitializationLock = new();
    private readonly RenderDocCaptureRequestState m_CaptureRequests = new();

    private pfnTriggerCapture? m_TriggerCapture;
    private pfnLaunchReplayUI? m_LaunchReplayUI;
    private pfnStartFrameCapture? m_StartFrameCapture;
    private pfnEndFrameCapture? m_EndFrameCapture;
    private pfnIsFrameCapturing? m_IsFrameCapturing;
    private pfnSetCaptureFilePathTemplate? m_SetCaptureFilePathTemplate;
    private pfnGetNumCaptures? m_GetNumCaptures;
    private pfnGetCapture? m_GetCapture;
    private pfnTriggerMultiFrameCapture? m_TriggerMultiFrameCapture;
    
    private int m_Initialized;
    private int m_UnavailableLogged;
    private RenderDocCaptureArtifactExpectation m_ArtifactExpectation;
    private CancellationTokenSource? m_ArtifactProbeCancellation;
    private AutoResetEvent? m_ArtifactProbeSignal;
    private Task<RenderDocCaptureArtifactProbeResult>? m_ArtifactProbeTask;
    private string m_AvailabilityDiagnostic =
        "RenderDoc availability has not been queried for this process.";

    public RenderDocCaptureRequestSnapshot CaptureRequest => m_CaptureRequests.Snapshot;

    public event Action<RenderDocCaptureRequestSnapshot>? CaptureStateChanged;

    public bool IsAvailable
    {
        get
        {
            EnsureInitialized();
            return m_ApiPtr != IntPtr.Zero;
        }
    }

    public string AvailabilityDiagnostic
    {
        get
        {
            EnsureInitialized();
            return m_AvailabilityDiagnostic;
        }
    }

    private RenderDocService()
    {
    }

    /// <summary>
    /// Synchronous initialization. Called when the service is first needed.
    /// By this point, an explicit process-start capture mode has already loaded
    /// renderdoc.dll before vkCreateInstance, so GetModuleHandle can bind its API.
    /// </summary>
    public void EnsureInitialized()
    {
        if (Volatile.Read(ref m_Initialized) != 0) return;

        lock (m_InitializationLock)
        {
            if (m_Initialized != 0) return;
            if (Initialize())
            {
                Volatile.Write(ref m_Initialized, 1);
            }
        }
    }

    private bool Initialize()
    {
        try 
        {
            KernelLog.Info("[RenderDocService] Initializing...");

            m_Library = GetModuleHandle("renderdoc.dll");
            if (m_Library == IntPtr.Zero)
            {
                m_AvailabilityDiagnostic =
                    "RenderDoc is not loaded; frame capture requires process-start enablement before graphics initialization.";
                if (Interlocked.Exchange(ref m_UnavailableLogged, 1) == 0)
                {
                    KernelLog.Info($"[RenderDocService] {m_AvailabilityDiagnostic}");
                }
                return false;
            }

            IntPtr getApiAddr = GetProcAddress(m_Library, "RENDERDOC_GetAPI");
            if (getApiAddr == IntPtr.Zero)
            {
                m_AvailabilityDiagnostic =
                    "The loaded RenderDoc module does not expose RENDERDOC_GetAPI.";
                KernelLog.Error($"[RenderDocService] {m_AvailabilityDiagnostic} Error Code: {Marshal.GetLastWin32Error()}");
                return true;
            }

            var getApi = Marshal.GetDelegateForFunctionPointer<pfnRENDERDOC_GetAPI>(getApiAddr);
            KernelLog.Info("[RenderDocService] Found RENDERDOC_GetAPI, requesting version 10600 (v1.6.0)...");
            
            // We request version 1.6.0
            int result = getApi(10600, out m_ApiPtr);
            if (result != 1)
            {
                m_AvailabilityDiagnostic =
                    $"The loaded RenderDoc module rejected API v1.6.0 with result {result}.";
                KernelLog.Error($"[RenderDocService] {m_AvailabilityDiagnostic}");
                m_ApiPtr = IntPtr.Zero;
                return true;
            }

            KernelLog.Info($"[RenderDocService] API pointer acquired: 0x{m_ApiPtr:X}");

            // The ApiPtr points to the struct of function pointers
            m_VTable = Marshal.PtrToStructure<RenderDocVTable>(m_ApiPtr);
            
            m_TriggerCapture = Marshal.GetDelegateForFunctionPointer<pfnTriggerCapture>(m_VTable.TriggerCapture);
            m_LaunchReplayUI = Marshal.GetDelegateForFunctionPointer<pfnLaunchReplayUI>(m_VTable.LaunchReplayUI);
            m_StartFrameCapture = Marshal.GetDelegateForFunctionPointer<pfnStartFrameCapture>(m_VTable.StartFrameCapture);
            m_IsFrameCapturing = Marshal.GetDelegateForFunctionPointer<pfnIsFrameCapturing>(m_VTable.IsFrameCapturing);
            m_EndFrameCapture = Marshal.GetDelegateForFunctionPointer<pfnEndFrameCapture>(m_VTable.EndFrameCapture);
            m_SetCaptureFilePathTemplate = Marshal.GetDelegateForFunctionPointer<pfnSetCaptureFilePathTemplate>(
                m_VTable.SetCaptureFilePathTemplate);
            m_GetNumCaptures = Marshal.GetDelegateForFunctionPointer<pfnGetNumCaptures>(m_VTable.GetNumCaptures);
            m_GetCapture = Marshal.GetDelegateForFunctionPointer<pfnGetCapture>(m_VTable.GetCapture);
            m_TriggerMultiFrameCapture = Marshal.GetDelegateForFunctionPointer<pfnTriggerMultiFrameCapture>(m_VTable.TriggerMultiFrameCapture);

            m_AvailabilityDiagnostic = "RenderDoc frame capture is available.";
            KernelLog.Info($"[RenderDocService] {m_AvailabilityDiagnostic}");
            return true;
        }
        catch (Exception ex)
        {
            m_AvailabilityDiagnostic = $"RenderDoc API initialization failed: {ex.Message}";
            KernelLog.Error($"[RenderDocService] Initialization failed: {ex.Message}");
            m_ApiPtr = IntPtr.Zero;
            return m_Library != IntPtr.Zero;
        }
    }

    /// <summary>
    /// Requests a frame capture for one exact render-surface registration.
    /// </summary>
    public void TriggerCapture(RenderSurfaceRegistration target)
    {
        TryTriggerCapture(target);
    }

    public bool TryTriggerCapture(RenderSurfaceRegistration target)
    {
        EnsureInitialized();

        if (m_ApiPtr == IntPtr.Zero)
        {
            KernelLog.Warning($"[RenderDocService] Cannot capture: {m_AvailabilityDiagnostic}");
            return false;
        }

        if (!m_CaptureRequests.TryRequest(target, out RenderDocCaptureRequestSnapshot snapshot))
        {
            KernelLog.WarningFormat(
                "[RenderDocService] Capture request rejected because request {0} is already {1} for host 0x{2:X}, generation {3}.",
                snapshot.RequestId,
                snapshot.Status,
                snapshot.Target.Host.ToInt64(),
                snapshot.Target.Generation);
            return false;
        }

        PublishCaptureState(snapshot);
        KernelLog.InfoFormat(
            "[RenderDocService] Capture request {0} targets host 0x{1:X}, generation {2}.",
            snapshot.RequestId,
            target.Host.ToInt64(),
            target.Generation);
        return true;
    }

    internal bool TryClaimCapture(
        RenderSurfaceRegistration surface,
        out RenderDocCaptureLease lease)
    {
        if (!m_CaptureRequests.TryBeginCapture(surface, out lease, out var snapshot))
        {
            return false;
        }

        PublishCaptureState(snapshot);
        return true;
    }

    internal void BeginArtifactPublication(
        RenderDocCaptureArtifactExpectation expectation,
        string diagnostic)
    {
        if (!expectation.IsValid)
        {
            throw new ArgumentException(
                "RenderDoc artifact publication requires a valid capture expectation.",
                nameof(expectation));
        }
        if (m_ArtifactExpectation.IsValid)
        {
            FailCapture(
                expectation.Lease,
                RenderDocCaptureFailureStage.ArtifactPublication,
                $"Capture request {expectation.Lease.RequestId} cannot publish while request " +
                $"{m_ArtifactExpectation.Lease.RequestId} still owns artifact publication.");
            return;
        }

        ResetArtifactReleaseProbe(waitForCompletion: true);
        m_ArtifactExpectation = expectation;
        if (!m_CaptureRequests.TryBeginArtifactPublication(
                expectation.Lease,
                diagnostic,
                out var snapshot))
        {
            m_ArtifactExpectation = default;
            return;
        }

        PublishCaptureState(snapshot);
        KernelLog.InfoFormat(
            "[RenderDocService] Capture request {0} ended and is waiting for artifact publication.",
            snapshot.RequestId);
        PollCaptureArtifactPublication();
    }

    internal void FailCapture(
        RenderDocCaptureLease lease,
        RenderDocCaptureFailureStage failureStage,
        string diagnostic)
    {
        if (m_CaptureRequests.TryFail(
                lease,
                failureStage,
                diagnostic,
                out var snapshot))
        {
            ClearArtifactExpectation(snapshot.RequestId);
            PublishCaptureFailure(snapshot);
        }
    }

    internal void ReportSurfaceUnregistered(RenderSurfaceRegistration surface)
    {
        string diagnostic =
            $"Target surface host 0x{surface.Host.ToInt64():X}, generation {surface.Generation} was unregistered before capture completion.";
        if (m_CaptureRequests.TryFailActiveForSurface(
                surface,
                RenderDocCaptureFailureStage.SurfaceUnregistered,
                diagnostic,
                out var snapshot))
        {
            ClearArtifactExpectation(snapshot.RequestId);
            PublishCaptureFailure(snapshot);
        }
    }

    internal void ReportSurfaceFrameFailure(
        RenderSurfaceRegistration surface,
        Exception exception)
    {
        string diagnostic =
            $"Target surface frame failed with {exception.GetType().Name}: {exception.Message}";
        if (m_CaptureRequests.TryFailActiveForSurface(
                surface,
                RenderDocCaptureFailureStage.SurfaceFrame,
                diagnostic,
                out var snapshot))
        {
            ClearArtifactExpectation(snapshot.RequestId);
            PublishCaptureFailure(snapshot);
        }
    }

    internal void ReportRenderSubsystemShutdown()
    {
        const string diagnostic =
            "The render subsystem shut down before the targeted surface completed its capture.";
        if (m_CaptureRequests.TryFailActive(
                RenderDocCaptureFailureStage.RenderSubsystemShutdown,
                diagnostic,
                out var snapshot))
        {
            ClearArtifactExpectation(snapshot.RequestId);
            PublishCaptureFailure(snapshot);
        }
    }

    /// <summary>
    /// Begins a frame capture against the process Vulkan device. Logical surface selection is
    /// enforced by the generation-qualified request before these NULL/NULL native markers run.
    /// </summary>
    internal bool TryStartCapture(
        RenderDocCaptureLease lease,
        out RenderDocCaptureArtifactExpectation expectation,
        out string diagnostic)
    {
        expectation = default;
        if (!lease.IsValid)
        {
            diagnostic = "RenderDoc capture start requires a valid request lease.";
            return false;
        }
        if (Volatile.Read(ref m_Initialized) == 0 || m_ApiPtr == IntPtr.Zero)
        {
            diagnostic = "RenderDoc API is unavailable when the targeted surface attempted to start capture.";
            return false;
        }
        if (m_StartFrameCapture == null ||
            m_IsFrameCapturing == null ||
            m_SetCaptureFilePathTemplate == null ||
            m_GetNumCaptures == null ||
            m_GetCapture == null)
        {
            diagnostic =
                "The loaded RenderDoc API does not expose the required capture and artifact-publication functions.";
            return false;
        }

        try
        {
            // Virtual surfaces have no HWND, so NULL/NULL selects the process Vulkan device.
            if (IsFrameCapturing())
            {
                diagnostic =
                    "RenderDoc was already capturing before the targeted request started.";
                return false;
            }

            string pathTemplate = CreateCapturePathTemplate(lease.RequestId);
            uint captureCountBeforeStart = m_GetNumCaptures();
            m_SetCaptureFilePathTemplate(pathTemplate);
            m_StartFrameCapture(IntPtr.Zero, IntPtr.Zero);
            if (!IsFrameCapturing())
            {
                diagnostic =
                    "RenderDoc did not enter frame-capturing state after StartFrameCapture.";
                return false;
            }

            expectation = new RenderDocCaptureArtifactExpectation(
                lease,
                captureCountBeforeStart,
                pathTemplate);
            diagnostic = string.Empty;
            KernelLog.InfoFormat(
                "[RenderDocService] RenderDoc started capture request {0}; inventory baseline={1}, path template='{2}'.",
                lease.RequestId,
                captureCountBeforeStart,
                pathTemplate);
            return true;
        }
        catch (Exception exception)
        {
            diagnostic =
                $"RenderDoc StartFrameCapture failed with {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    public bool IsFrameCapturing() => (m_IsFrameCapturing?.Invoke() ?? 0) != 0;

    /// <summary>
    /// Ends a frame capture. Artifact publication is observed separately because RenderDoc may
    /// finish writing and append the capture to its inventory after this call returns.
    /// </summary>
    internal bool TryEndCapture(out string diagnostic)
    {
        if (Volatile.Read(ref m_Initialized) == 0 ||
            m_ApiPtr == IntPtr.Zero ||
            m_EndFrameCapture == null)
        {
            diagnostic = "RenderDoc API became unavailable before EndFrameCapture.";
            return false;
        }

        try
        {
            // Must match the device/window used in StartFrameCapture (NULL, NULL).
            uint result = m_EndFrameCapture(IntPtr.Zero, IntPtr.Zero);
            if (result == 0)
            {
                diagnostic = "RenderDoc EndFrameCapture returned failure.";
                return false;
            }

            diagnostic = "RenderDoc EndFrameCapture succeeded.";
            KernelLog.Info("[RenderDocService] EndFrameCapture succeeded.");
            return true;
        }
        catch (Exception exception)
        {
            diagnostic =
                $"RenderDoc EndFrameCapture failed with {exception.GetType().Name}: {exception.Message}";
            return false;
        }
    }

    internal void PollCaptureArtifactPublication()
    {
        RenderDocCaptureArtifactExpectation expectation = m_ArtifactExpectation;
        if (!expectation.IsValid)
        {
            return;
        }

        RenderDocCaptureRequestSnapshot current = m_CaptureRequests.Snapshot;
        if (current.Status != RenderDocCaptureRequestStatus.PublishingArtifact ||
            current.RequestId != expectation.Lease.RequestId ||
            current.Target != expectation.Lease.Target)
        {
            ResetArtifactReleaseProbe(waitForCompletion: true);
            m_ArtifactExpectation = default;
            return;
        }

        if (m_GetNumCaptures == null || m_GetCapture == null)
        {
            FailArtifactPublication(
                expectation,
                "The RenderDoc capture inventory API became unavailable during artifact publication.");
            return;
        }

        try
        {
            if (m_ArtifactProbeTask == null)
            {
                StartArtifactReleaseProbe(expectation);
                return;
            }
            if (!m_ArtifactProbeTask.IsCompleted)
            {
                m_ArtifactProbeSignal?.Set();
                return;
            }

            RenderDocCaptureArtifactProbeResult artifactProbe =
                ConsumeArtifactReleaseProbe();
            if (artifactProbe.Status == RenderDocCaptureArtifactProbeStatus.Failed)
            {
                FailArtifactPublication(expectation, artifactProbe.Diagnostic);
                return;
            }

            if (m_CaptureRequests.TryCompleteArtifact(
                    expectation.Lease,
                    artifactProbe.CandidatePath,
                    artifactProbe.Diagnostic,
                    out var completed))
            {
                m_ArtifactExpectation = default;
                PublishCaptureState(completed);
                KernelLog.InfoFormat(
                    "[RenderDocService] Capture request {0} completed for host 0x{1:X}, generation {2}. Capture saved to: {3}",
                    completed.RequestId,
                    completed.Target.Host.ToInt64(),
                    completed.Target.Generation,
                    completed.CapturePath);
                TryOpenCapture(completed.CapturePath);
            }
        }
        catch (Exception exception)
        {
            FailArtifactPublication(
                expectation,
                $"RenderDoc artifact publication failed with {exception.GetType().Name}: {exception.Message}");
        }
    }

    private void StartArtifactReleaseProbe(
        RenderDocCaptureArtifactExpectation expectation)
    {
        if (m_ArtifactProbeTask != null ||
            m_ArtifactProbeCancellation != null ||
            m_ArtifactProbeSignal != null ||
            m_GetNumCaptures == null ||
            m_GetCapture == null)
        {
            throw new InvalidOperationException(
                "RenderDoc artifact publication probe ownership is invalid.");
        }

        var cancellation = new CancellationTokenSource();
        var signal = new AutoResetEvent(false);
        pfnGetNumCaptures readCaptureCount = m_GetNumCaptures;
        RenderDocCapturePathReader readCapturePath = TryReadCapturePath;
        m_ArtifactProbeCancellation = cancellation;
        m_ArtifactProbeSignal = signal;
        m_ArtifactProbeTask = Task.Factory.StartNew(
            () => RenderDocCaptureArtifactProbe.WaitForPublication(
                expectation.CaptureCountBeforeStart,
                expectation.PathTemplate,
                readCaptureCount.Invoke,
                readCapturePath,
                signal,
                cancellation.Token),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private RenderDocCaptureArtifactProbeResult ConsumeArtifactReleaseProbe()
    {
        Task<RenderDocCaptureArtifactProbeResult>? task = m_ArtifactProbeTask;
        CancellationTokenSource? cancellation = m_ArtifactProbeCancellation;
        AutoResetEvent? signal = m_ArtifactProbeSignal;
        if (task == null || !task.IsCompleted)
        {
            throw new InvalidOperationException(
                "RenderDoc artifact publication probe is not complete.");
        }

        m_ArtifactProbeTask = null;
        m_ArtifactProbeCancellation = null;
        m_ArtifactProbeSignal = null;
        try
        {
            return task.GetAwaiter().GetResult();
        }
        finally
        {
            signal?.Dispose();
            cancellation?.Dispose();
        }
    }

    private void ResetArtifactReleaseProbe(bool waitForCompletion)
    {
        Task<RenderDocCaptureArtifactProbeResult>? task = m_ArtifactProbeTask;
        CancellationTokenSource? cancellation = m_ArtifactProbeCancellation;
        AutoResetEvent? signal = m_ArtifactProbeSignal;
        m_ArtifactProbeTask = null;
        m_ArtifactProbeCancellation = null;
        m_ArtifactProbeSignal = null;

        cancellation?.Cancel();
        signal?.Set();
        if (waitForCompletion && task != null)
        {
            try
            {
                task.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        signal?.Dispose();
        cancellation?.Dispose();
    }

    private bool TryReadCapturePath(
        uint captureIndex,
        out string capturePath,
        out string diagnostic)
    {
        capturePath = string.Empty;
        diagnostic = string.Empty;
        if (m_GetCapture == null)
        {
            diagnostic = "The RenderDoc GetCapture function is unavailable.";
            return false;
        }

        uint pathLength = 0;
        if (m_GetCapture(
                captureIndex,
                IntPtr.Zero,
                out pathLength,
                out _) == 0)
        {
            diagnostic =
                $"RenderDoc rejected advertised capture inventory index {captureIndex}.";
            return false;
        }
        if (pathLength == 0 || pathLength >= int.MaxValue)
        {
            diagnostic =
                $"RenderDoc capture inventory index {captureIndex} reported invalid UTF-8 path length {pathLength}.";
            return false;
        }

        int allocationLength = checked((int)pathLength + 1);
        IntPtr pathBuffer = Marshal.AllocHGlobal(allocationLength);
        try
        {
            Marshal.WriteByte(pathBuffer, allocationLength - 1, 0);
            if (m_GetCapture(
                    captureIndex,
                    pathBuffer,
                    out pathLength,
                    out _) == 0)
            {
                diagnostic =
                    $"RenderDoc rejected capture inventory index {captureIndex} while reading its path.";
                return false;
            }

            capturePath = Marshal.PtrToStringUTF8(pathBuffer) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(capturePath))
            {
                diagnostic =
                    $"RenderDoc capture inventory index {captureIndex} published an empty path.";
                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(pathBuffer);
        }
    }

    private static string CreateCapturePathTemplate(ulong requestId)
    {
        string captureDirectory = Path.Combine(
            AppContext.BaseDirectory,
            "logs",
            "renderdoc");
        Directory.CreateDirectory(captureDirectory);
        return Path.Combine(
            captureDirectory,
            $"arisen-p{Environment.ProcessId}-request-{requestId:D20}-{Guid.NewGuid():N}-");
    }

    private void FailArtifactPublication(
        RenderDocCaptureArtifactExpectation expectation,
        string diagnostic)
    {
        FailCapture(
            expectation.Lease,
            RenderDocCaptureFailureStage.ArtifactPublication,
            diagnostic);
    }

    private void ClearArtifactExpectation(ulong requestId)
    {
        if (m_ArtifactExpectation.Lease.RequestId == requestId)
        {
            ResetArtifactReleaseProbe(waitForCompletion: true);
            m_ArtifactExpectation = default;
        }
    }

    private void TryOpenCapture(string capturePath)
    {
        try
        {
            if (!ShouldOpenReplayUi())
            {
                KernelLog.InfoFormat(
                    "[RenderDocService] Capture artifact ready at '{0}'. Replay UI launch is disabled.",
                    capturePath);
                return;
            }

            KernelLog.InfoFormat(
                "[RenderDocService] Capture artifact ready at '{0}'. Launching replay UI.",
                capturePath);

            string executablePath = "C:\\Program Files\\RenderDoc\\qrenderdoc.exe";
            if (!File.Exists(executablePath))
            {
                executablePath = "C:\\renderdoc\\qrenderdoc.exe";
            }

            if (File.Exists(executablePath))
            {
                System.Diagnostics.Process.Start(executablePath, $"\"{capturePath}\"");
            }
            else
            {
                m_LaunchReplayUI?.Invoke(1, capturePath);
            }
        }
        catch (Exception exception)
        {
            KernelLog.WarningFormat(
                "[RenderDocService] Capture artifact was published, but replay launch failed: {0}: {1}",
                exception.GetType().Name,
                exception.Message);
        }
    }

    private static bool ShouldOpenReplayUi()
    {
        string? setting = Environment.GetEnvironmentVariable("ARISEN_RENDERDOC_OPEN_REPLAY");
        return !string.Equals(setting, "0", StringComparison.Ordinal) &&
               !string.Equals(setting, "false", StringComparison.OrdinalIgnoreCase) &&
               !string.Equals(setting, "no", StringComparison.OrdinalIgnoreCase);
    }

    private void PublishCaptureFailure(RenderDocCaptureRequestSnapshot snapshot)
    {
        PublishCaptureState(snapshot);
        KernelLog.ErrorFormat(
            "[RenderDocService] Capture request {0} failed at {1} for host 0x{2:X}, generation {3}: {4}",
            snapshot.RequestId,
            snapshot.FailureStage,
            snapshot.Target.Host.ToInt64(),
            snapshot.Target.Generation,
            snapshot.Diagnostic);
    }

    private void PublishCaptureState(RenderDocCaptureRequestSnapshot snapshot)
    {
        Action<RenderDocCaptureRequestSnapshot>? handlers = CaptureStateChanged;
        if (handlers == null)
        {
            return;
        }

        foreach (Action<RenderDocCaptureRequestSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(snapshot);
            }
            catch (Exception exception)
            {
                KernelLog.WarningFormat(
                    "[RenderDocService] Capture-state observer failed. Request={0}, State={1}, Observer={2}.{3}, Error={4}: {5}",
                    snapshot.RequestId,
                    snapshot.Status,
                    handler.Method.DeclaringType?.FullName ?? "<unknown>",
                    handler.Method.Name,
                    exception.GetType().Name,
                    exception.Message);
            }
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    public void Dispose()
    {
        // RenderDoc usually handles its own shutdown via the API or process exit
    }
}
