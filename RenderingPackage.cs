using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Diagnostics;
using ArisenKernel.Lifecycle;

namespace ArisenEngine.Rendering;

public class RenderingPackage : IPackageEntry
{
    private RenderSubsystem? m_RenderSubsystem;

    public void OnLoad(IServiceRegistry registry)
    {
        KernelLog.Info("[RenderingPackage] Loading Arisen Render Pipeline...");

        // 1. Create the RenderSubsystem
        m_RenderSubsystem = new RenderSubsystem();
        RenderSubsystem.Instance = m_RenderSubsystem;
        KernelLog.Info("[RenderingPackage] Created RenderSubsystem");

        // 2. Register it as a service so the Editor Viewport can resolve it via types
        registry.RegisterService<RenderSubsystem>(m_RenderSubsystem);
        registry.RegisterService<RenderDocService>(RenderDocService.Instance);

        // 3. Register it as a tickable heart of the engine
        EngineKernel.Instance.RegisterSubsystem(m_RenderSubsystem);

        // IRHIDevice and IWindowProvider are resolved lazily by RenderSubsystem/RenderSurface on
        // first use — not here. Graphics init (Vulkan + RenderDoc) is deferred to
        // HardwareWarmupStep so it runs after Avalonia's WinUI compositor is up, which means
        // IRHIDevice isn't registered yet at OnLoad time.
        KernelLog.Info("[RenderingPackage] Loaded: RenderSubsystem registered.");
    }

    public void OnUnload(IServiceRegistry registry)
    {
        m_RenderSubsystem?.Dispose();
        m_RenderSubsystem = null;
        KernelLog.Info("[RenderingPackage] Unloaded.");
    }
}
