using ArisenKernel.Diagnostics;
using ArisenKernel.Contracts;
using ArisenEngine.Core.ECS;
using ArisenEngine.Core.Automation;
using ArisenEngine.Rendering;
using ArisenKernel.Lifecycle;
using ArisenEngine.Core.Lifecycle;
using ArisenEngine.Core.Diagnostics;

namespace ArisenEngine.Rendering;



public class RenderSurface : IRenderSurface
{
    internal List<RenderSurface> Surfaces = new List<RenderSurface>();
    private IntPtr m_Host;
    private uint m_SurfaceId;
    private IntPtr m_Handle;
    private string m_Name = "RenderSurface";

    private WindowProcessor m_Processor;
    private bool m_Hosted = true;

    public IntPtr Handle => m_Handle;
    public uint SurfaceId => m_SurfaceId;

    public RenderSurface(IntPtr host, string name, int width = 0, int height = 0, bool hosted = true)
    {
        m_Name = name;
        m_Hosted = hosted;
        bool isFullScreen = (width == 0 || height == 0) && host == IntPtr.Zero;
        if (Initialize())
        {
            m_Host = host;

            // B101: If the host is a dummy handle (like 1001 from the Editor), 
            // we bypass native window creation and use a virtual surface ID.
            if (host == (IntPtr)1001)
            {
                m_SurfaceId = 0xFFFFFFFF;
            }
            else
            {
                m_SurfaceId = isFullScreen
                    ? NativeHAL.RenderWindowAPI.CreateFullScreenRenderSurface(host, m_Processor.ProcPtr)
                    : NativeHAL.RenderWindowAPI.CreateRenderWindow(host, m_Processor.ProcPtr, width, height);
            }

            if (m_SurfaceId != 0xFFFFFFFF)
            {
                m_Handle = NativeHAL.RenderWindowAPI.GetWindowHandle(m_SurfaceId);
                NativeHAL.RenderWindowAPI.SetWindowResizeCallback(m_SurfaceId, m_Processor.ResizeCallbackPtr);
            }
            else
            {
                // Virtual/Headless surface has no native window handle
                m_Handle = IntPtr.Zero;
            }

            // TODO: Per-surface device creation 
            // CreateLogicDevice and GetLogicalDevice
            // are pure virtual methods in C++ RHIInstance that CppSharp cannot bind.
            // Device creation is handled by Graphics.Initialize()  InitLogicDevices() instead.
            // var instance = RHIGraphics.Instance;
            // if (instance != null)
            // {
            //     instance.CreateLogicDevice(m_SurfaceId);
            //     var device = instance.GetLogicalDevice(m_SurfaceId);
            //     if (device != null)
            //         RHIGraphics.SetLogicDevice(device);
            // }

            Surfaces.Add(this);
        }
        else
        {
            throw new Exception("Render Surface init failed.");
        }
    }

    private bool Initialize()
    {
        if (EngineKernel.Instance.Services.TryGetService<IWindowProvider>(out var provider))
        {
            m_Processor = provider.CreateWindowProcessor();
            return true;
        }

        throw new System.Exception($"No IWindowProvider registered! Cannot create RenderSurface for {m_Name}");
    }

    public bool IsValid() => ((m_Hosted && m_Host != IntPtr.Zero) || !m_Hosted) && m_Handle != IntPtr.Zero;

    public void DisposeSurface()
    {
        NativeHAL.RenderWindowAPI.RemoveRenderSurface(m_SurfaceId);
        Surfaces.Remove(this);
        if (Surfaces.Count <= 0)
        {
            // ArisenEngine.Core.Lifecycle.ArisenApplication.AllSurfacesDestroyed?.Invoke();
        }
    }

    public IntPtr GetHandle() => m_Handle;

    public void OnCreate()
    {
    }

    public void OnResizing() => KernelLog.InfoFormat("RenderSurface : {0} resizing.", m_Name);

    public void OnResized()
    {
        KernelLog.InfoFormat("RenderSurface : {0} resized.", m_Name);
        Logger.Log($"RenderSurface : {m_Name} resized.");
    }

    public void OnDestroy()
    {
    }
}

