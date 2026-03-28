using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenKernel.Diagnostics;

namespace ArisenEngine.Rendering;

public class RenderingPackage : IPackageEntry
{
    public void OnLoad(IServiceRegistry registry)
    {
        KernelLog.Info("[RenderingPackage] Loaded: Arisen Render Pipeline");
        
        // Ensure services are resolved topologically rather than statically
        var windowProvider = registry.GetService<ArisenKernel.Contracts.IWindowProvider>();
        var rhiDevice = registry.GetService<ArisenKernel.Contracts.IRHIDevice>();
        
        KernelLog.Info("[RenderingPackage] Successfully resolved IWindowProvider and IRHIDevice from ServiceRegistry.");
    }

    public void OnUnload(IServiceRegistry registry)
    {
    }
}
