using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using ArisenEngine.Core.Diagnostics;
using ArisenKernel.Lifecycle;
using ArisenEngine.Core.RHI;
using ArisenEngine.Core.Memory;
using ArisenEngine.Core.ECS;
using ArisenEngine.Core.Math;
using ArisenEngine.Core.Lifecycle;
using ArisenEngine.ECS.Lifecycle;
using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;

namespace ArisenEngine.Rendering;

public class RenderSubsystem : ITickableSubsystem
{
    public RenderSubsystem()
    {
        Instance = this;
    }

    public static RenderSubsystem? Instance;
    public static Action? AllSurfacesDestroyed;
    private static ConcurrentDictionary<IntPtr, SurfaceInfo> s_GlobalSurfaces
    {
        get
        {
            var addrStr = Environment.GetEnvironmentVariable("ARISEN_SURFACE_REGISTRY_ADDR");
            if (!string.IsNullOrEmpty(addrStr) && long.TryParse(addrStr, out var addr))
            {
                var handle = GCHandle.FromIntPtr(new IntPtr(addr));
                if (handle.IsAllocated && handle.Target is ConcurrentDictionary<IntPtr, SurfaceInfo> dict)
                {
                    return dict;
                }
            }

            var newDict = new ConcurrentDictionary<IntPtr, SurfaceInfo>();
            var newHandle = GCHandle.Alloc(newDict);
            Environment.SetEnvironmentVariable("ARISEN_SURFACE_REGISTRY_ADDR", ((IntPtr)newHandle).ToInt64().ToString());
            return newDict;
        }
    }
    
    private readonly RHICommandQueue m_CommandQueue = new();

    private RenderPipeline? m_CurrentPipeline;
    private RenderPipelineAsset? m_CurrentAsset;
    private IWindowProvider? m_WindowProvider;
    private IntPtr m_RuntimeWindowHost;

    // Rendering should typically happen last in the frame
    public int Priority => 100;
    public EnginePhase InitPhase => EnginePhase.Init;

    public void Initialize()
    {
        using var _ = Profiler.Zone("RenderSubsystem.Initialize");
        Logger.Log("[RenderSubsystem] Initializing...");

#if !ARISEN_ENGINE_EDITOR
        var services = EngineKernel.Instance.Services;
        if (services.TryGetService<IWindowProvider>(out m_WindowProvider) && m_WindowProvider != null)
        {
            var windowInfo = m_WindowProvider.GetWindowInfo();
            if (windowInfo.SurfaceKind == WindowSurfaceKind.Win32 &&
                windowInfo.NativeHandle != IntPtr.Zero)
            {
                m_RuntimeWindowHost = windowInfo.NativeHandle;
                InternalRegisterExistingSurface(
                    m_RuntimeWindowHost,
                    "RuntimeMainWindow",
                    SurfaceType.Window,
                    new RuntimeWindowRenderSurface(windowInfo));

                m_WindowProvider.WindowResized += OnRuntimeWindowResized;
                KernelLog.InfoFormat(
                    "[RenderSubsystem] Registered runtime main window surface. Handle=0x{0:X}, Surface=0x{1:X}, Size={2}x{3}",
                    windowInfo.NativeHandle.ToInt64(),
                    windowInfo.NativeSurfaceId,
                    windowInfo.Width,
                    windowInfo.Height);
            }
            else
            {
                KernelLog.WarningFormat(
                    "[RenderSubsystem] Runtime main window surface not registered. SurfaceKind={0}, Handle=0x{1:X}",
                    windowInfo.SurfaceKind,
                    windowInfo.NativeHandle.ToInt64());
            }
        }
        else
        {
            KernelLog.Warning("[RenderSubsystem] Runtime rendering has no IWindowProvider; no native swapchain surface was registered.");
        }
#endif
    }

    public void Tick(float deltaTime)
    {
        using var _ = Profiler.Zone("RenderSubsystem.Tick");

        try
        {
            // Execute all pending RHI commands (resize, registration) on the Render thread
            // BEFORE starting the frame's rendering work.
            m_CommandQueue.ExecutePending(this);

            var asset = Graphics.currentRenderPipelineAsset;
        
            Logger.Log($"[RenderSubsystem] Tick [Hash:{GetHashCode()}] - Frame {EngineKernel.Instance.CurrentFrameIndex}, Pipeline Asset: {(asset == null ? "NULL" : asset.GetType().Name)}");
            if (asset == null)
            {
                return;
            }

            // 1. Manage pipeline lifecycle
            // REFACTOR: We check for reference equality AND a dirty state to handle property changes in the same asset instance.
            if (!ReferenceEquals(m_CurrentAsset, asset) || asset.IsDirty)
            {
                m_CurrentPipeline?.Dispose();
                m_CurrentAsset = asset;
                m_CurrentPipeline = asset.InternalCreatePipeline();
                asset.IsDirty = false;
                Logger.Log($"[RenderSubsystem] Pipeline recreated from asset: {asset.GetType().Name}");
            }

            if (m_CurrentPipeline == null) return;

            // 2. Prepare Context and Render per Surface
            foreach (var surfaceInfo in s_GlobalSurfaces.Values)
            {
            var surface = surfaceInfo.Surface;
            var device = RHISystem.GetOrCreateDevice(surface.SurfaceId, surface.Width, surface.Height);
            
            // Get the swapchain associated with this surface
            var swapChain = device.GetSurface().GetSwapChain();
            if (!swapChain.IsValid) continue;

            // 3. Render
            // Fetch cameras and processed draw list from ECS
            var sceneSubsystem = EngineKernel.Instance.GetSubsystem<SceneSubsystem>();
            var entityManager = sceneSubsystem?.ActiveEntityManager;
            
            var frameIndex = EngineKernel.Instance.CurrentFrameIndex;
            
            // Acquire the current swapchain image.
            // If this fails (e.g. window minimized or 0x0 size), we skip rendering for this surface.
            var acquiredImage = swapChain.BeginFrame(frameIndex);
            if (!acquiredImage.IsValid)
            {
                continue;
            }

            var outputKind = ((surface.SurfaceId & RHISystem.VirtualSurfaceIDMask) != 0)
                ? RenderOutputKind.EditorSharedTexture
                : RenderOutputKind.NativeSwapchain;

            var arena = FrameArena.Instance;
            Span<MeshDrawCommand> frameDrawList = Span<MeshDrawCommand>.Empty;

            if (sceneSubsystem != null)
            {
                var drawList = sceneSubsystem.GetCurrentDrawList();
                if (drawList.Length > 0)
                {
                    frameDrawList = arena.Alloc<MeshDrawCommand>(drawList.Length);
                    drawList.CopyTo(frameDrawList);
                }
            }

            Span<Camera> frameCameras = Span<Camera>.Empty;
            var cameraCount = 0;

            if (entityManager != null)
            {
                var cameraPool = entityManager.GetPool<CameraComponent>();
                var transformPool = entityManager.GetPool<TransformComponent>();

                var cameraComponents = cameraPool.GetRawComponentArray();
                var cameraEntities = cameraPool.GetRawEntityArray();
                int camCount = cameraPool.Count;

                if (camCount > 0)
                {
                    frameCameras = arena.Alloc<Camera>(camCount);
                    var aspectRatio = surface.Height == 0 ? 1.0f : (float)surface.Width / surface.Height;

                    for (int i = 0; i < camCount; i++)
                    {
                        Entity entity = cameraEntities[i];
                        if (transformPool.Has(entity))
                        {
                            ref var camComp = ref cameraComponents[i];
                            ref var transComp = ref transformPool.GetRef(entity);

                            ref Camera cam = ref frameCameras[cameraCount];
                            cam.FieldOfView = camComp.VerticalFov;
                            cam.NearClip = camComp.NearPlane;
                            cam.FarClip = camComp.FarPlane;
                            cam.AspectRatio = aspectRatio;
                            cam.ProjectionType = camComp.IsPerspective != 0 ? CameraProjectionType.Perspective : CameraProjectionType.Orthographic;
                            cam.Position = transComp.Position;
                            cam.Rotation = transComp.Rotation.QuaternionToEulerDegrees();
                            cameraCount++;
                        }
                    }

                    frameCameras = frameCameras.Slice(0, cameraCount);
                }
            }

            ulong ticket = 0;

            unsafe
            {
                fixed (Camera* pCameras = frameCameras)
                fixed (MeshDrawCommand* pDrawList = frameDrawList)
                {
                    var snapshot = new RenderFrameSnapshot(
                        device,
                        swapChain,
                        acquiredImage,
                        surface.SurfaceId,
                        outputKind,
                        frameIndex,
                        deltaTime,
                        surface.Width,
                        surface.Height,
                        pCameras,
                        cameraCount,
                        pDrawList,
                        frameDrawList.Length);

                    var context = new RenderContext(arena, snapshot);
                    Profiler.PlotValue("Render.DrawCount", snapshot.DrawListCount);
                    Profiler.PlotValue("Render.CameraCount", snapshot.CameraCount);
                    Profiler.PlotValue("Render.OutputWidth", snapshot.Width);
                    Profiler.PlotValue("Render.OutputHeight", snapshot.Height);

                    if (frameIndex % 60 == 0)
                    {
                        Logger.Log($"[RenderSubsystem] FrameSnapshot | Frame: {snapshot.FrameIndex} | Surface: 0x{snapshot.SurfaceId:X} | Size: {snapshot.Width}x{snapshot.Height} | Cameras: {snapshot.CameraCount} | Draws: {snapshot.DrawListCount} | Output: {snapshot.OutputKind}");
                    }

                    // B11: RenderDoc Integration
                    // If a capture was requested (e.g. from the Editor UI), we wrap the engine work
                    // with Start/End capture calls. RenderDocService uses NULL/NULL wildcards
                    // to match the single Vulkan device (virtual surfaces have no HWND).
                    var rd = ArisenKernel.Lifecycle.EngineKernel.Instance.Services.GetService<RenderDocService>();
                    bool requestCapture = rd?.IsCaptureRequested ?? false;

                    if (requestCapture)
                    {
                        rd?.StartCapture();
                    }

                    try
                    {
                        ticket = m_CurrentPipeline.InternalRender(context);
                    }
                    finally
                    {
                        if (requestCapture)
                        {
                            rd?.EndCapture();
                            rd?.ClearCaptureRequest();
                            Logger.Log("[RenderSubsystem] RenderDoc capture completed.");
                        }
                    }

                    // Phase 2 Optimization: Precision synchronization.
                    // Instead of stalling the CPU here (which slows down the simulation),
                    // we pass the ticket to the surface so the consumer (Editor Viewport)
                    // can perform a targeted asynchronous wait.
                    if (surface is RenderSurface concreteSurface)
                    {
                        var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                        var sharedHandle = surface.GetSharedHandle(context.FrameIndex);
                        if (frameIndex % 60 == 0 || ticket > 0)
                            Logger.Info($"[ArisenViewportControl] PID: {pid}, Frame {frameIndex} Status: Ticket {ticket}, Handle 0x{sharedHandle.ToInt64():X}, SubsystemHash: {GetHashCode()}, IsGlobalInstance: {this == Instance}");

                        // B11: Ticket update must be atomic for the UI thread's polling loop
                        lock (concreteSurface)
                        {
                            concreteSurface.SetLastRenderTicket(ticket, context.FrameIndex, context.Width, context.Height, swapChain);
                            if (frameIndex % 60 == 0)
                                Logger.Log($"[RenderSubsystem] SetLastRenderTicket for Host {surfaceInfo.Parent}: {ticket}");
                        }
                    }
                }
            }

                // Finalize work and signal presentation
                swapChain.EndFrame(frameIndex);
            }
        }
        finally
        {
            Profiler.FrameMarkNamed("RuntimeFrame");
        }
    }

    public void RegisterSurface(IntPtr host, string name, SurfaceType surfaceType, int width = 0, int height = 0)
    {
        m_CommandQueue.Enqueue(new RegisterSurfaceCommand(host, name, surfaceType, width, height));
    }

    internal void InternalRegisterSurface(IntPtr host, string name, SurfaceType surfaceType, int width = 0, int height = 0)
    {
        using var _ = Profiler.Zone("RenderSubsystem.InternalRegisterSurface");
        if (!s_GlobalSurfaces.ContainsKey(host))
        {
            var surface = new RenderSurface(host, name, width, height);
            s_GlobalSurfaces.TryAdd(host, new SurfaceInfo()
            {
                Name = name,
                Parent = host,
                Surface = surface,
                SurfaceType = surfaceType
            });

            return;
        }

        throw new Exception($"Same host : {host} already added");
    }

    internal void InternalRegisterExistingSurface(IntPtr host, string name, SurfaceType surfaceType, IRenderSurface surface)
    {
        using var _ = Profiler.Zone("RenderSubsystem.InternalRegisterExistingSurface");
        if (!s_GlobalSurfaces.ContainsKey(host))
        {
            s_GlobalSurfaces.TryAdd(host, new SurfaceInfo()
            {
                Name = name,
                Parent = host,
                Surface = surface,
                SurfaceType = surfaceType
            });

            return;
        }

        throw new Exception($"Same host : {host} already added");
    }

    public void ResizeSurface(IntPtr host, int width, int height)
    {
        m_CommandQueue.Enqueue(new ResizeSurfaceCommand(host, (uint)width, (uint)height));
    }

    internal void InternalResizeSurface(IntPtr host, int width, int height)
    {
        if (s_GlobalSurfaces.TryGetValue(host, out var surface))
        {
            surface.Surface.Resize((uint)width, (uint)height);
        }
    }

    public IntPtr GetSurfaceSharedHandle(IntPtr host, uint frameIndex)
    {
        if (s_GlobalSurfaces.TryGetValue(host, out var surfaceInfo))
        {
            return surfaceInfo.Surface.GetSharedHandle(frameIndex);
        }
        return IntPtr.Zero;
    }

    public ulong GetLastRenderTicket(IntPtr host)
    {
        if (s_GlobalSurfaces.TryGetValue(host, out var surfaceInfo))
        {
            // B11: Ticket read must be atomic
            lock (surfaceInfo.Surface)
            {
                var ticket = surfaceInfo.Surface.GetLastRenderTicket();
                return ticket;
            }
        }
        
        return 0;
    }

    public uint GetLastRenderFrameIndex(IntPtr host)
    {
        if (s_GlobalSurfaces.TryGetValue(host, out var surfaceInfo))
        {
            return surfaceInfo.Surface.GetLastRenderFrameIndex();
        }
        return 0;
    }

    public bool GetOutputInfo(IntPtr host, out RenderOutputInfo info)
    {
        if (s_GlobalSurfaces.TryGetValue(host, out var surfaceInfo))
        {
            info = surfaceInfo.Surface.GetOutputInfo();
            return true;
        }

        info = default;
        return false;
    }

    public void ReleaseConsumedSemaphoreHandle(IntPtr host, IntPtr handle)
    {
        if (s_GlobalSurfaces.TryGetValue(host, out var surfaceInfo))
        {
            surfaceInfo.Surface.ReleaseConsumedSemaphoreHandle(handle);
        }
    }

    public void ReportConsumedFrameIndex(IntPtr host, uint frameIndex)
    {
        if (s_GlobalSurfaces.TryGetValue(host, out var surfaceInfo))
        {
            surfaceInfo.Surface.ReportConsumedFrameIndex(frameIndex);
        }
    }

    public uint GetLastRenderWidth(IntPtr host)
    {
        if (s_GlobalSurfaces.TryGetValue(host, out var surfaceInfo) && surfaceInfo.Surface is RenderSurface concrete)
        {
            return concrete.GetLastRenderWidth();
        }
        return 0;
    }

    public uint GetLastRenderHeight(IntPtr host)
    {
        if (s_GlobalSurfaces.TryGetValue(host, out var surfaceInfo) && surfaceInfo.Surface is RenderSurface concrete)
        {
            return concrete.GetLastRenderHeight();
        }
        return 0;
    }

    public System.Threading.Tasks.Task WaitForRenderTicketAsync(IntPtr host, ulong ticket)
    {
        if (s_GlobalSurfaces.TryGetValue(host, out var surfaceInfo))
        {
            return surfaceInfo.Surface.WaitForRenderTicketAsync(ticket);
        }
        return System.Threading.Tasks.Task.CompletedTask;
    }

    public void UnregisterSurface(IntPtr host)
    {
        m_CommandQueue.Enqueue(new UnregisterSurfaceCommand(host));
    }

    internal void InternalUnregisterSurface(IntPtr host)
    {
        if (s_GlobalSurfaces.TryGetValue(host, out var surfaceInfo))
        {
            surfaceInfo.Surface.DisposeSurface();
            s_GlobalSurfaces.TryRemove(host, out _);

            if (s_GlobalSurfaces.Count == 0)
            {
                AllSurfacesDestroyed?.Invoke();
            }
            return;
        }

        throw new Exception($"Surface of host {host} not exists");
    }

    public void Shutdown()
    {
#if !ARISEN_ENGINE_EDITOR
        if (m_WindowProvider != null)
        {
            m_WindowProvider.WindowResized -= OnRuntimeWindowResized;
            m_WindowProvider = null;
        }

        m_RuntimeWindowHost = IntPtr.Zero;
#endif

        foreach (var surface in s_GlobalSurfaces.Values)
        {
            surface.Surface.DisposeSurface();
        }
        s_GlobalSurfaces.Clear();

        m_CurrentPipeline?.Dispose();
        m_CurrentPipeline = null;
        m_CurrentAsset = null;
    }

    public void Dispose()
    {
        Shutdown();
    }

#if !ARISEN_ENGINE_EDITOR
    private void OnRuntimeWindowResized(WindowResizeInfo resizeInfo)
    {
        if (m_RuntimeWindowHost == IntPtr.Zero) return;
        ResizeSurface(
            m_RuntimeWindowHost,
            Math.Max(1, resizeInfo.Width),
            Math.Max(1, resizeInfo.Height));
    }
#endif

    private struct SurfaceInfo
    {
        public string Name;
        public IntPtr Parent;
        public IRenderSurface Surface;
        public SurfaceType SurfaceType;
        public uint SurfaceId => Surface.SurfaceId;
    }
}
