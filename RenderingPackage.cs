using ArisenKernel.Packages;
using ArisenKernel.Services;

namespace ArisenEngine.Rendering;

public class RenderingPackage : IPackageEntry
{
    public void OnLoad(IServiceRegistry registry)
    {
        System.Console.WriteLine("[RenderingPackage] Loaded: Arisen Render Pipeline");
        
        // Ensure services are resolved topologically rather than statically
        var windowProvider = registry.GetService<ArisenKernel.Contracts.IWindowProvider>();
        var rhiDevice = registry.GetService<ArisenKernel.Contracts.IRHIDevice>();
        
        System.Console.WriteLine("[RenderingPackage] Successfully resolved IWindowProvider and IRHIDevice from ServiceRegistry.");
    }

    public void OnUnload(IServiceRegistry registry)
    {
    }
}
