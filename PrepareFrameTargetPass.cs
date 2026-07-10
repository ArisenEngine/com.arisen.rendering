namespace ArisenEngine.Rendering;

/// <summary>
/// Engine-owned pass that prepares the current frame target for user rendering passes.
/// It owns output-mode-specific acquire and initial layout policy so generic/user passes do
/// not need to know about editor shared textures or native presentation details.
/// </summary>
public sealed class PrepareFrameTargetPass : RenderPassNode
{
    public PrepareFrameTargetPass(string name = "PrepareFrameTargetPass") : base(name)
    {
    }

    protected override void Record(RenderContext context, RenderCommandList commandList)
    {
        // RenderGraph records the planned FrameColor transition before this pass.
    }
}
