using System;
using System.Runtime.InteropServices;
using ArisenKernel.Diagnostics;

namespace ArisenEngine.Rendering;

/// <summary>
/// Provides integration with the RenderDoc API for frame captures.
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

    private pfnTriggerCapture? m_TriggerCapture;
    private pfnLaunchReplayUI? m_LaunchReplayUI;
    private pfnStartFrameCapture? m_StartFrameCapture;
    private pfnEndFrameCapture? m_EndFrameCapture;
    private pfnIsFrameCapturing? m_IsFrameCapturing;
    private pfnGetNumCaptures? m_GetNumCaptures;
    private pfnGetCapture? m_GetCapture;
    private pfnTriggerMultiFrameCapture? m_TriggerMultiFrameCapture;
    
    public bool IsCaptureRequested { get; private set; }

    public bool IsAvailable => m_ApiPtr != IntPtr.Zero;

    private readonly object m_InitLock = new object();
    private volatile bool m_Initialized;
    private System.Threading.Tasks.Task? m_InitTask;

    private RenderDocService()
    {
    }

    /// <summary>
    /// Kicks off the background initialization of the RenderDoc API.
    /// Uses a Task to avoid blocking the main thread during library search and LoadLibrary calls.
    /// </summary>
    private void EnsureInitialized()
    {
        if (m_Initialized) return;
        lock (m_InitLock)
        {
            if (m_Initialized) return;
            if (m_InitTask == null)
            {
                m_InitTask = System.Threading.Tasks.Task.Run(() => {
                    Initialize();
                    m_Initialized = true;
                });
            }
        }
    }

    private void Initialize()
    {
        try 
        {
            KernelLog.Info("[RenderDocService] Starting background initialization...");

            // RenderDoc usually injects itself. We try to find it first.
            m_Library = GetModuleHandle("renderdoc.dll");
            if (m_Library == IntPtr.Zero)
            {
                // If not injected, we try to load it from common installation paths
                string[] paths = {
                    "C:\\Program Files\\RenderDoc\\renderdoc.dll",
                    "C:\\renderdoc.dll"
                };
                foreach(var p in paths) {
                    if (System.IO.File.Exists(p)) {
                        KernelLog.Info($"[RenderDocService] Loading library from: {p}");
                        m_Library = LoadLibrary(p);
                        break;
                    }
                }
            }

            if (m_Library == IntPtr.Zero)
            {
                KernelLog.Warning("[RenderDocService] renderdoc.dll not found. RenderDoc integration disabled.");
                return;
            }

            IntPtr getApiAddr = GetProcAddress(m_Library, "RENDERDOC_GetAPI");
            if (getApiAddr == IntPtr.Zero)
            {
                KernelLog.Error("[RenderDocService] RENDERDOC_GetAPI not found in renderdoc.dll. Error Code: " + Marshal.GetLastWin32Error());
                return;
            }

            var getApi = Marshal.GetDelegateForFunctionPointer<pfnRENDERDOC_GetAPI>(getApiAddr);
            KernelLog.Info("[RenderDocService] Found RENDERDOC_GetAPI, requesting version 10600 (v1.6.0)...");
            
            // We request version 1.6.0
            int result = getApi(10600, out m_ApiPtr);
            if (result != 1)
            {
                KernelLog.Error($"[RenderDocService] Failed to get RenderDoc API v1.6.0. Result: {result}");
                m_ApiPtr = IntPtr.Zero;
                return;
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

            KernelLog.Info("[RenderDocService] RenderDoc API initialized successfully.");
        }
        catch (Exception ex)
        {
            KernelLog.Error($"[RenderDocService] Initialization failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Requests a frame capture on the next frame.
    /// Runs in a Task so it can wait for background initialization without blocking the caller (UI/Main thread).
    /// </summary>
    public void TriggerCapture()
    {
        EnsureInitialized();
        
        // We set the flag to true. The next call to StartCapture in the render loop will begin the capture.
        // This is necessary because for virtual surfaces, RenderDoc doesn't see a 'Present' call.
        IsCaptureRequested = true;
        KernelLog.Info("[RenderDocService] Capture requested for next frame via manual markers.");
    }

    public void ClearCaptureRequest() => IsCaptureRequested = false;

    public void StartCapture(IntPtr device, IntPtr window)
    {
        if (!m_Initialized || !IsAvailable) return;
        
        m_StartFrameCapture?.Invoke(device, window);
        
        if (IsFrameCapturing())
        {
            KernelLog.Info("[RenderDocService] RenderDoc started capturing frame.");
        }
        else
        {
            KernelLog.Warning("[RenderDocService] RenderDoc failed to start capturing. (Device/Window wildcard mismatch?)");
        }
    }

    public bool IsFrameCapturing() => (m_IsFrameCapturing?.Invoke() ?? 0) != 0;

    public void EndCapture(IntPtr device, IntPtr window)
    {
        if (!m_Initialized || !IsAvailable) return;

        uint result = m_EndFrameCapture?.Invoke(device, window) ?? 0;
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

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr LoadLibrary(string lpFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    public void Dispose()
    {
        // RenderDoc usually handles its own shutdown via the API or process exit
    }
}
