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
    internal const uint EditorSharedTextureMaxOutstandingFrames = 3;

    internal List<RenderSurface> Surfaces = new();

    private readonly object m_OutputLock = new();
    private IntPtr m_Host;
    private uint m_SurfaceId;
    private IntPtr m_Handle;
    private uint m_Width;
    private uint m_Height;
    private string m_Name = "RenderSurface";
    private ulong m_LastTicket;
    private uint m_LastFrameIndex;
    private uint m_LastConsumedFrameIndex;
    private uint m_LastRenderWidth;
    private uint m_LastRenderHeight;
    private uint m_ResizeGeneration;
    private uint m_LastRenderResizeGeneration;
    private RenderOutputFramePacingState m_FramePacing;
    private RHISwapChain? m_LastRenderSwapChain;
    private Core.RHI.RHISurface? m_NativeSurface;
    private RHISwapChain? m_CachedSwapChain;

    private WindowProcessor m_Processor = null!;
    private bool m_Hosted = true;

    public IntPtr Handle => m_Handle;
    public uint SurfaceId => m_SurfaceId;
    public uint Width => m_Width;
    public uint Height => m_Height;
    public string Name => m_Name;

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
        width = Math.Max(1u, width);
        height = Math.Max(1u, height);

        if (m_Width == width && m_Height == height) return;

        lock (m_OutputLock)
        {
            m_Width = width;
            m_Height = height;
            m_ResizeGeneration++;
            m_LastTicket = 0;
            m_LastFrameIndex = 0;
            m_LastConsumedFrameIndex = 0;
            m_LastRenderWidth = 0;
            m_LastRenderHeight = 0;
            m_LastRenderSwapChain = null;
            m_CachedSwapChain = null;
            m_FramePacing.Reset();
        }

        // B101: Professional Virtual Surface Resizing.
        // We cannot call ResizeRenderSurface in HAL because that assumes a Win32 HWND exists.
        // Instead, we call the RHI-level SetResolution directly which handles swapchain recreation.
        if ((m_SurfaceId & RHISystem.VirtualSurfaceIDMask) != 0)
        {
            var nativeSurface = EnsureNativeSurface();
            if (nativeSurface == null)
            {
                return;
            }

            lock (m_OutputLock)
            {
                RHISurfaceAPI.RHISurface_SetResolution(nativeSurface.Handle, width, height);
            }

            Logger.Log(
                $"[RenderSurface] Resized virtual surface | Name: {m_Name} | Surface: 0x{m_SurfaceId:X} | Size: {width}x{height} | Generation: {m_ResizeGeneration}");
            return;
        }

        NativeHAL.RenderWindowAPI.ResizeRenderSurface(m_SurfaceId, width, height);
        Logger.Log(
            $"[RenderSurface] Resized native surface | Name: {m_Name} | Surface: 0x{m_SurfaceId:X} | Size: {width}x{height} | Generation: {m_ResizeGeneration}");
    }

    public void Dispose() => DisposeSurface();

    public void DisposeSurface()
    {
        if ((m_SurfaceId & RHISystem.VirtualSurfaceIDMask) == 0)
        {
            NativeHAL.RenderWindowAPI.RemoveRenderSurface(m_SurfaceId);
        }
        else
        {
            RHISystem.RemoveDevice(m_SurfaceId);
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

    public IntPtr GetSharedHandle(uint frameIndex)
    {
        lock (m_OutputLock)
        {
            return GetSharedHandleLocked(frameIndex);
        }
    }

    public ulong GetSharedMemorySize(uint frameIndex)
    {
        lock (m_OutputLock)
        {
            return GetSharedMemorySizeLocked(frameIndex);
        }
    }

    public IntPtr GetRenderFinishedSemaphoreHandle(uint frameIndex)
    {
        lock (m_OutputLock)
        {
            return GetRenderFinishedSemaphoreHandleLocked(frameIndex);
        }
    }

    public IntPtr CreateConsumedSemaphoreHandle(uint frameIndex)
    {
        lock (m_OutputLock)
        {
            return CreateConsumedSemaphoreHandleLocked(frameIndex);
        }
    }

    public void ReleaseConsumedSemaphoreHandle(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;

        lock (m_OutputLock)
        {
            var swapChainToUse = m_LastRenderSwapChain ?? m_CachedSwapChain;
            if (swapChainToUse.HasValue && swapChainToUse.Value.IsValid)
            {
                swapChainToUse.Value.ReleaseConsumedSemaphoreWin32Handle(handle);
            }
        }
    }

    public ulong GetLastRenderTicket()
    {
        lock (m_OutputLock)
        {
            return m_LastTicket;
        }
    }

    public uint GetLastRenderFrameIndex()
    {
        lock (m_OutputLock)
        {
            return m_LastFrameIndex;
        }
    }

    public uint GetLastRenderWidth()
    {
        lock (m_OutputLock)
        {
            return m_LastRenderWidth;
        }
    }

    public uint GetLastRenderHeight()
    {
        lock (m_OutputLock)
        {
            return m_LastRenderHeight;
        }
    }

    public async Task WaitForRenderTicketAsync(ulong ticket)
    {
        if (ticket == 0) return;

        var device = RHISystem.GetOrCreateDevice(m_SurfaceId, m_Width, m_Height);
        if (!device.IsValid) return;

        var completedBefore = device.GetCompletedTicket();
        if (completedBefore >= ticket) return;

        // GetCompletedTicket() is only a cached value. In the Vulkan backend that cache is
        // refreshed by RHIVkQueue::Update(), or by the native WaitForTicket path after waiting
        // on the timeline semaphore. A pure managed poll here can therefore spin forever even
        // while the GPU has already completed the work, leaving the editor viewport black before
        // the first ImportImage/UpdateAsync call.
        await Task.Run(() => device.WaitQueueTicket(ticket));

        var completedAfter = device.GetCompletedTicket();
        if (completedAfter < ticket)
        {
            KernelLog.Warning($"[RenderSurface] WaitForRenderTicketAsync returned before ticket completion. Surface=0x{m_SurfaceId:X}, Ticket={ticket}, CompletedBefore={completedBefore}, CompletedAfter={completedAfter}");
        }
    }

    public RenderOutputInfo GetOutputInfo()
    {
        lock (m_OutputLock)
        {
            if (m_LastTicket == 0 || m_LastRenderWidth == 0 || m_LastRenderHeight == 0)
            {
                return new RenderOutputInfo
                {
                    ResizeGeneration = m_ResizeGeneration,
                    Width = m_Width,
                    Height = m_Height
                };
            }

            return new RenderOutputInfo
            {
                Ticket = m_LastTicket,
                FrameIndex = m_LastFrameIndex,
                ResizeGeneration = m_LastRenderResizeGeneration,
                SharedHandle = GetSharedHandleLocked(m_LastFrameIndex),
                MemorySize = GetSharedMemorySizeLocked(m_LastFrameIndex),
                WaitSemaphoreHandle = GetRenderFinishedSemaphoreHandleLocked(m_LastFrameIndex),
                SignalSemaphoreHandle = CreateConsumedSemaphoreHandleLocked(m_LastFrameIndex),
                Width = m_LastRenderWidth,
                Height = m_LastRenderHeight
            };
        }
    }

    public void ReportConsumedFrameIndex(uint frameIndex)
    {
        lock (m_OutputLock)
        {
            m_FramePacing.MarkConsumed(frameIndex);
            m_LastConsumedFrameIndex = m_FramePacing.LastConsumedFrameIndex;
        }
    }

    public uint GetLastConsumedFrameIndex()
    {
        lock (m_OutputLock)
        {
            return m_LastConsumedFrameIndex;
        }
    }

    internal bool CanSubmitOutputFrame(uint frameIndex, uint maxOutstandingFrames)
    {
        lock (m_OutputLock)
        {
            return m_FramePacing.CanSubmit(frameIndex, maxOutstandingFrames);
        }
    }

    internal void SetLastRenderTicket(ulong ticket, uint frameIndex, uint width, uint height, RHISwapChain swapChain)
    {
        lock (m_OutputLock)
        {
            m_LastTicket = ticket;
            m_LastFrameIndex = frameIndex;
            m_LastRenderWidth = width;
            m_LastRenderHeight = height;
            m_LastRenderResizeGeneration = m_ResizeGeneration;
            m_LastRenderSwapChain = swapChain;
            m_FramePacing.MarkSubmitted(frameIndex);
        }
    }

    private Core.RHI.RHISurface? EnsureNativeSurface()
    {
        if (m_NativeSurface != null)
        {
            return m_NativeSurface;
        }

        var device = RHISystem.GetOrCreateDevice(m_SurfaceId, m_Width, m_Height);
        if (device.IsValid)
        {
            m_NativeSurface = device.GetSurface();
        }

        return m_NativeSurface;
    }

    private RHISwapChain? GetSwapChainForSharedOutputLocked()
    {
        var nativeSurface = EnsureNativeSurface();
        if (nativeSurface == null)
        {
            return null;
        }

        // B101: Strict Synchronization.
        // Prefer the swapchain that produced the last successful render so handles,
        // memory size, semaphores, and dimensions describe the same output frame.
        var swapChainToUse = m_LastRenderSwapChain ?? (m_CachedSwapChain ?? nativeSurface.GetSwapChain());
        m_CachedSwapChain ??= swapChainToUse;
        return swapChainToUse;
    }

    private IntPtr GetSharedHandleLocked(uint frameIndex)
    {
        var swapChainToUse = GetSwapChainForSharedOutputLocked();
        if (swapChainToUse is not { IsValid: true })
        {
            return IntPtr.Zero;
        }

        const uint imageCount = 3;
        return swapChainToUse.Value.GetSharedWin32Handle(frameIndex % imageCount);
    }

    private ulong GetSharedMemorySizeLocked(uint frameIndex)
    {
        var swapChainToUse = GetSwapChainForSharedOutputLocked();
        if (swapChainToUse is not { IsValid: true })
        {
            return 0;
        }

        const uint imageCount = 3;
        return swapChainToUse.Value.GetSharedMemorySize(frameIndex % imageCount);
    }

    private IntPtr GetRenderFinishedSemaphoreHandleLocked(uint frameIndex)
    {
        var swapChainToUse = GetSwapChainForSharedOutputLocked();
        return swapChainToUse is { IsValid: true }
            ? swapChainToUse.Value.GetRenderFinishedSemaphoreWin32Handle(frameIndex)
            : IntPtr.Zero;
    }

    private IntPtr CreateConsumedSemaphoreHandleLocked(uint frameIndex)
    {
        var swapChainToUse = GetSwapChainForSharedOutputLocked();
        return swapChainToUse is { IsValid: true }
            ? swapChainToUse.Value.CreateConsumedSemaphoreWin32Handle(frameIndex)
            : IntPtr.Zero;
    }
}

