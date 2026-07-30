using System;
using System.Runtime.InteropServices;
using System.Threading;
using ArisenKernel.Diagnostics;

namespace ArisenEngine.Rendering;

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

    private pfnTriggerCapture? m_TriggerCapture;
    private pfnLaunchReplayUI? m_LaunchReplayUI;
    private pfnStartFrameCapture? m_StartFrameCapture;
    private pfnEndFrameCapture? m_EndFrameCapture;
    private pfnIsFrameCapturing? m_IsFrameCapturing;
    private pfnGetNumCaptures? m_GetNumCaptures;
    private pfnGetCapture? m_GetCapture;
    private pfnTriggerMultiFrameCapture? m_TriggerMultiFrameCapture;
    
    private int m_CaptureRequested;
    private int m_Initialized;
    private int m_UnavailableLogged;
    private string m_AvailabilityDiagnostic =
        "RenderDoc availability has not been queried for this process.";

    public bool IsCaptureRequested => Volatile.Read(ref m_CaptureRequested) != 0;

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
            return m_Library != IntPtr.Zero;
        }
    }

    /// <summary>
    /// Requests a frame capture on the next frame.
    /// The render loop checks IsCaptureRequested and wraps the frame with Start/EndCapture.
    /// </summary>
    public void TriggerCapture()
    {
        TryTriggerCapture();
    }

    public bool TryTriggerCapture()
    {
        EnsureInitialized();
        
        if (m_ApiPtr == IntPtr.Zero)
        {
            KernelLog.Warning($"[RenderDocService] Cannot capture: {m_AvailabilityDiagnostic}");
            return false;
        }

        Volatile.Write(ref m_CaptureRequested, 1);
        KernelLog.Info("[RenderDocService] Capture requested for next frame via manual markers.");
        return true;
    }

    public void ClearCaptureRequest() => Volatile.Write(ref m_CaptureRequested, 0);

    /// <summary>
    /// Begins a frame capture. Uses NULL/NULL wildcards per RenderDoc docs:
    /// "If both are set to NULL, RenderDoc will simply choose one at random
    /// so is only recommended for the case where only one is present."
    /// This is correct for Arisen Engine which has a single Vulkan instance.
    /// </summary>
    public void StartCapture()
    {
        if (Volatile.Read(ref m_Initialized) == 0 || m_ApiPtr == IntPtr.Zero) return;
        
        // Pass NULL, NULL to wildcard match the single Vulkan device.
        // Virtual surfaces have no HWND, so we cannot pass a window handle.
        m_StartFrameCapture?.Invoke(IntPtr.Zero, IntPtr.Zero);
        
        if (IsFrameCapturing())
        {
            KernelLog.Info("[RenderDocService] RenderDoc started capturing frame.");
        }
        else
        {
            KernelLog.Warning("[RenderDocService] RenderDoc failed to start capturing. Was renderdoc.dll loaded before vkCreateInstance?");
        }
    }

    public bool IsFrameCapturing() => (m_IsFrameCapturing?.Invoke() ?? 0) != 0;

    /// <summary>
    /// Ends a frame capture and retrieves the capture file path.
    /// Uses NULL/NULL wildcards matching StartCapture.
    /// </summary>
    public void EndCapture()
    {
        if (Volatile.Read(ref m_Initialized) == 0 || m_ApiPtr == IntPtr.Zero) return;

        // Must match the device/window used in StartCapture (NULL, NULL)
        uint result = m_EndFrameCapture?.Invoke(IntPtr.Zero, IntPtr.Zero) ?? 0;
        if (result == 0)
        {
            KernelLog.Warning("[RenderDocService] EndFrameCapture returned 0 (failure).");
        }
        else 
        {
            KernelLog.Info("[RenderDocService] EndFrameCapture succeeded.");
        }

        // Path retrieval logic remains below
        uint numCaptures = m_GetNumCaptures?.Invoke() ?? 0;
        KernelLog.Info($"[RenderDocService] Total captures reported by API: {numCaptures}");
        if (numCaptures > 0)
        {
            // Get the latest capture (index is 0-based)
            uint lastIdx = numCaptures - 1;
            uint pathLen = 0;
            ulong timestamp = 0;
            
            // First call to get length (filename=NULL)
            m_GetCapture?.Invoke(lastIdx, IntPtr.Zero, out pathLen, out timestamp);
            if (pathLen > 0)
            {
                IntPtr pathPtr = Marshal.AllocHGlobal((int)pathLen);
                try 
                {
                    // Second call to get path
                    m_GetCapture?.Invoke(lastIdx, pathPtr, out pathLen, out timestamp);
                    string? path = Marshal.PtrToStringAnsi(pathPtr);
                    if (!string.IsNullOrEmpty(path))
                    {
                        KernelLog.Info($"[RenderDocService] Capture saved to: {path}. Launching UI...");
                        
                        // Try to find qrenderdoc.exe in common installation paths
                        string exePath = "C:\\Program Files\\RenderDoc\\qrenderdoc.exe";
                        if (!System.IO.File.Exists(exePath))
                        {
                            exePath = "C:\\renderdoc\\qrenderdoc.exe"; // Fallback
                        }

                        if (System.IO.File.Exists(exePath))
                        {
                            System.Diagnostics.Process.Start(exePath, $"\"{path}\"");
                        }
                        else 
                        {
                            // Fallback to API if exe not found
                            m_LaunchReplayUI?.Invoke(1, path);
                        }
                    }
                }
                finally 
                {
                    Marshal.FreeHGlobal(pathPtr);
                }
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
