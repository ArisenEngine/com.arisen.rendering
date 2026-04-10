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

        // 2. Register it as a service so the Editor Viewport can resolve it via types
        registry.RegisterService<RenderSubsystem>(m_RenderSubsystem);

        // 3. Register it as a tickable heart of the engine
        EngineKernel.Instance.RegisterSubsystem(m_RenderSubsystem);

        // Ensure services are resolved topologically rather than statically
        var windowProvider = registry.GetService<ArisenKernel.Contracts.IWindowProvider>();
        var rhiDevice = registry.GetService<ArisenKernel.Contracts.IRHIDevice>();
        
        KernelLog.Info("[RenderingPackage] Loaded: RenderSubsystem successfully bound to RHI and Windowing providers.");
    }

    public void OnUnload(IServiceRegistry registry)
    {
        m_RenderSubsystem?.Dispose();
        m_RenderSubsystem = null;
        KernelLog.Info("[RenderingPackage] Unloaded.");
    }
}
