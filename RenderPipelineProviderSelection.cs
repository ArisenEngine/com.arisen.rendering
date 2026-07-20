using ArisenKernel.Packages;

namespace ArisenEngine.Rendering;

public static class RenderPipelineProviderSelection
{
    public static void Activate(ProjectManifest project, IRenderPipelineProvider provider)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(provider);

        var selection = project.RenderPipeline
            ?? throw new InvalidOperationException(
                "Workspace manifest must select a RenderPipeline settings asset.");
        if (!selection.IsValid)
        {
            throw new InvalidOperationException(
                "Workspace RenderPipeline selection requires a valid Guid and PackageId.");
        }

        provider.Activate(selection);
    }
}
