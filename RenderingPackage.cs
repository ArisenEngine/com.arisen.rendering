using ArisenKernel.Diagnostics;
using ArisenKernel.Packages;
using ArisenKernel.Services;
using ArisenEngine.Core.Assets;
using ArisenEngine.Rendering.Resources;

namespace ArisenEngine.Rendering;

public class RenderingPackage : IPackageEntry
{
    private RuntimeShaderCookRecipeRegistry? m_ShaderCookRecipes;

    public void OnLoad(IServiceRegistry registry)
    {
        KernelLog.Info("[RenderingPackage] Loading Arisen Render Pipeline...");

        registry.RegisterService<RenderDocService>(RenderDocService.Instance);
        m_ShaderCookRecipes = new RuntimeShaderCookRecipeRegistry();
        registry.RegisterService<IRuntimeShaderCookRecipeRegistry>(m_ShaderCookRecipes);
        registry.GetService<IRuntimeAssetCookerRegistry>().RegisterCooker(
            new RenderingRuntimeAssetCooker(
                registry.GetService<IAssetDatabase>(),
                m_ShaderCookRecipes));

        // Rendering subsystems are declared in package metadata and registered by PackageSubsystem.
        // Runtime builds warm up the selected RHI backend in RuntimeRHIWarmupSubsystem. Editor
        // builds keep hardware warmup in HardwareWarmupStep so it runs after Avalonia's WinUI
        // compositor is up.
        KernelLog.Info("[RenderingPackage] Loaded: rendering subsystems are metadata-driven.");
    }

    public void OnUnload(IServiceRegistry registry)
    {
        m_ShaderCookRecipes = null;
        RenderSubsystem.Instance = null;
        KernelLog.Info("[RenderingPackage] Unloaded.");
    }
}
