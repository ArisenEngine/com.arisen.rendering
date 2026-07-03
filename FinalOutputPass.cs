using Arisen.Native.RHI;
using ArisenEngine.Core.RHI;

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
        // The Avalonia Vulkan interop path consumes imported Vulkan images from
        // TRANSFER_SRC_OPTIMAL and requires ownership release to the external compositor.
        if (context.OutputKind == RenderOutputKind.EditorSharedTexture)
        {
            commandList.TransitionImageLayout(
                context.TargetImage,
                EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
                EImageLayout.IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                RHIQueueFamily.Ignored,
                RHIQueueFamily.External);
            return;
        }

        // Native and offscreen outputs currently share the same final readable/transfer layout.
        // A future SwapchainOutputBackend can specialize this to PRESENT_SRC when native
        // presentation is fully graph-owned.
        commandList.TransitionImageLayout(
            context.TargetImage,
            EImageLayout.IMAGE_LAYOUT_COLOR_ATTACHMENT_OPTIMAL,
            EImageLayout.IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL);
    }
}
