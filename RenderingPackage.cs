using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Diagnostics;

namespace ArisenEngine.Rendering;

public class RenderingPackage : IPackageEntry
{
    public void OnLoad(IServiceRegistry registry)
    {
        KernelLog.Info("[RenderingPackage] Loading Arisen Render Pipeline...");

        registry.RegisterService<RenderDocService>(RenderDocService.Instance);

        // RenderSubsystem is declared in package metadata and registered by PackageSubsystem.
        // IRHIDevice and IWindowProvider are resolved lazily by RenderSubsystem/RenderSurface on
        // first use — not here. Graphics init (Vulkan + RenderDoc) is deferred to
        // HardwareWarmupStep so it runs after Avalonia's WinUI compositor is up, which means
        // IRHIDevice isn't registered yet at OnLoad time.
        KernelLog.Info("[RenderingPackage] Loaded: RenderSubsystem is metadata-driven.");
    }

    public void OnUnload(IServiceRegistry registry)
    {
        RenderSubsystem.Instance = null;
        KernelLog.Info("[RenderingPackage] Unloaded.");
    }
}
