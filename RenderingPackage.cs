using ArisenKernel.Diagnostics;
using ArisenKernel.Packages;
using ArisenKernel.Services;

namespace ArisenEngine.Rendering;

public class RenderingPackage : IPackageEntry
{
    public void OnLoad(IServiceRegistry registry)
    {
        KernelLog.Info("[RenderingPackage] Loading Arisen Render Pipeline...");

        registry.RegisterService<RenderDocService>(RenderDocService.Instance);

        // Rendering subsystems are declared in package metadata and registered by PackageSubsystem.
        // Runtime builds warm up the selected RHI backend in RuntimeRHIWarmupSubsystem. Editor
        // builds keep hardware warmup in HardwareWarmupStep so it runs after Avalonia's WinUI
        // compositor is up.
        KernelLog.Info("[RenderingPackage] Loaded: rendering subsystems are metadata-driven.");
    }

    public void OnUnload(IServiceRegistry registry)
    {
        RenderSubsystem.Instance = null;
        KernelLog.Info("[RenderingPackage] Unloaded.");
    }
}
