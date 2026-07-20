using ArisenKernel.Packages;

namespace ArisenEngine.Rendering;

/// <summary>
/// Activates the render-pipeline implementation selected by project composition.
/// </summary>
public interface IRenderPipelineProvider
{
    string ProviderPackageId { get; }

    string SettingsAssetType { get; }

    void Activate(ProjectAssetReference settings);

    void Deactivate();
}
