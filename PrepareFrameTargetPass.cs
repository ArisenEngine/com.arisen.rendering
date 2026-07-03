using Arisen.Native.RHI;
using ArisenEngine.Core.RHI;

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
        // We currently clear the whole target every frame, so UNDEFINED is valid for the old
        // layout. Shared editor output additionally needs ownership acquired back from the
        // external compositor before any color attachment writes.
        if (context.OutputKind == RenderOutputKind.EditorSharedTexture)
        {
            commandList.TransitionImageLayout(
                context.TargetImage,
                EImageLayout.IMAGE_LAYOUT_UNDEFINED,
                EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                RHIQueueFamily.External,
                RHIQueueFamily.Ignored);
            return;
        }

        commandList.TransitionImageLayout(
            context.TargetImage,
            EImageLayout.IMAGE_LAYOUT_UNDEFINED,
            EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL);
    }
}
