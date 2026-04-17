using ArisenKernel.Diagnostics;
using ArisenKernel.Contracts;
using ArisenEngine.Core.ECS;
using ArisenEngine.Core.Automation;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using System.Threading.Tasks;
using Arisen.Native.RHI;

namespace ArisenEngine.Rendering;

public class RenderSurface : IRenderSurface
{
    internal List<RenderSurface> Surfaces = new List<RenderSurface>();
    private IntPtr m_Host;
    private uint m_SurfaceId;
    private IntPtr m_Handle;
    private uint m_Width;
    private uint m_Height;
    private string m_Name = "RenderSurface";
    private ulong m_LastTicket;
    private uint m_LastFrameIndex;
    private Core.RHI.RHISurface m_NativeSurface;

    private WindowProcessor m_Processor;
    private bool m_Hosted = true;

    public IntPtr Handle => m_Handle;
    public uint SurfaceId => m_SurfaceId;
    public uint Width => m_Width;
    public uint Height => m_Height;

    public RenderSurface(IntPtr host, string name, int width = 0, int height = 0, bool hosted = true)
    {
        m_Name = name;
        m_Hosted = hosted;
        m_Width = (uint)width;
        m_Height = (uint)height;
        bool isFullScreen = (width == 0 || height == 0) && host == IntPtr.Zero;
        if (Initialize())
        {
            m_Host = host;

            // B101: If the host is in the dedicated virtual window range (e.g. from the Editor), 
            // we bypass native window creation and use a virtual surface ID.
            if (host.ToInt64() >= 1000 && host.ToInt64() <= 65535)
            {
                m_SurfaceId = RHISystem.VirtualSurfaceIDMask | (uint)host.ToInt64(); 
            }
            else
            {
                m_SurfaceId = isFullScreen
                    ? NativeHAL.RenderWindowAPI.CreateFullScreenRenderSurface(host, m_Processor.ProcPtr)
                    : NativeHAL.RenderWindowAPI.CreateRenderWindow(host, m_Processor.ProcPtr, width, height);
            }

            if ((m_SurfaceId & RHISystem.VirtualSurfaceIDMask) == 0)
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

    public bool IsValid() 
    {
        // B101: Virtual surfaces (Editor) don't have native window handles, 
        // they are valid if their virtual surface ID is correctly assigned.
        if ((m_SurfaceId & RHISystem.VirtualSurfaceIDMask) != 0)
            return true;
            
        return ((m_Hosted && m_Host != IntPtr.Zero) || !m_Hosted) && m_Handle != IntPtr.Zero;
    }

    public void Resize(uint width, uint height)
    {
        m_Width = width;
        m_Height = height;

        // B101: Professional Virtual Surface Resizing.
        // We cannot call ResizeRenderSurface in HAL because that assumes a Win32 HWND exists.
        // Instead, we call the RHI-level SetResolution directly which handles swapchain recreation.
        if ((m_SurfaceId & RHISystem.VirtualSurfaceIDMask) != 0)
        {
            if (m_NativeSurface == null)
            {
                var device = RHISystem.GetOrCreateDevice(m_SurfaceId, m_Width, m_Height);
                if (device.IsValid) m_NativeSurface = device.GetSurface();
            }

            if (m_NativeSurface != null)
            {
                RHISurfaceAPI.RHISurface_SetResolution(m_NativeSurface.Handle, width, height);
            }
            return;
        }

        NativeHAL.RenderWindowAPI.ResizeRenderSurface(m_SurfaceId, width, height);
    }

    public void Dispose() => DisposeSurface();

    public void DisposeSurface()
    {
        if ((m_SurfaceId & RHISystem.VirtualSurfaceIDMask) == 0)
        {
            NativeHAL.RenderWindowAPI.RemoveRenderSurface(m_SurfaceId);
        }
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

    private RHISwapChain? m_CachedSwapChain;

    public IntPtr GetSharedHandle(uint frameIndex)
    {
        if (m_NativeSurface == null)
        {
            var device = RHISystem.GetOrCreateDevice(m_SurfaceId, m_Width, m_Height);
            if (device.IsValid) m_NativeSurface = device.GetSurface();
        }

        if (m_NativeSurface == null) return IntPtr.Zero;

        if (m_CachedSwapChain == null || !m_CachedSwapChain.Value.IsValid)
        {
            m_CachedSwapChain = m_NativeSurface.GetSwapChain();
        }

        if (m_CachedSwapChain.Value.IsValid)
        {
            // For cross-API interop, we synchronize with the engine's frame rotation.
            // RHIVkSwapChain consistently uses (frameIndex % imageCount) for virtual swapchains.
            // Default image count for virtual surfaces is 3. 
            uint imageCount = 3; 
            return m_CachedSwapChain.Value.GetSharedWin32Handle(frameIndex % imageCount);
        }
        return IntPtr.Zero;
    }

    public ulong GetLastRenderTicket() => m_LastTicket;
    public uint GetLastRenderFrameIndex() => m_LastFrameIndex;

    public async Task WaitForRenderTicketAsync(ulong ticket)
    {
        if (ticket == 0) return;

        var device = RHISystem.GetOrCreateDevice(m_SurfaceId, m_Width, m_Height);
        if (!device.IsValid) return;

        // Poll for completion to avoid blocking the caller (e.g. Avalonia UI thread)
        // while allowing concurrent execution of the Engine and UI.
        while (device.GetCompletedTicket() < ticket)
        {
            await Task.Delay(1);
        }
    }

    internal void SetLastRenderTicket(ulong ticket, uint frameIndex) 
    { 
        m_LastTicket = ticket; 
        m_LastFrameIndex = frameIndex;
    }
}

