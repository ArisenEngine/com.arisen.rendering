namespace ArisenEngine.Rendering;

/// <summary>
/// Engine-owned pass that converts the final camera color target into the layout/ownership
/// required by the active output backend. This keeps presentation/export policy out of user
/// rendering passes such as geometry, skybox, or post-processing.
/// </summary>
public sealed class FinalOutputPass : RenderPassNode
{
    public FinalOutputPass(string name = "FinalOutputPass") : base(name)
    {
    }

    protected override void Record(RenderContext context, RenderCommandList commandList)
    {
        // RenderGraph records the planned FrameColor transition before this pass.
    }
}
