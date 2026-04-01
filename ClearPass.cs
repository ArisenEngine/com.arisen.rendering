using ArisenEngine.Core.RHI;
using System.Diagnostics;

namespace ArisenEngine.Rendering;

/// <summary>
/// A simple pass that clears the current render target.
/// </summary>
public sealed class ClearPass : RenderPassNode
{
    private readonly Color m_ClearColor;

    public ClearPass(Color color, string name = "ClearPass")
    {
        m_ClearColor = color;
        Name = name;
    }

    protected override void Record(RenderContext context, RHICommandBuffer commandBuffer)
    {
        // 1. Begin recording
        commandBuffer.Begin();

        // 2. Begin dynamic rendering (modern Vulkan/RHI path)
        // We use the SwapChain's current image view for clearing
        var colorImageView = context.SwapChain.GetCurrentImage().GetView();
        
        commandBuffer.BeginRendering(
            colorImageView, 
            EImageLayout.COLOR_ATTACHMENT_OPTIMAL,
            EAttachmentLoadOp.ATTACHMENT_LOAD_OP_CLEAR,
            EAttachmentStoreOp.ATTACHMENT_STORE_OP_STORE,
            m_ClearColor.R, m_ClearColor.G, m_ClearColor.B, m_ClearColor.A,
            0, 0, context.SwapChain.Width, context.SwapChain.Height
        );

        // 3. End rendering and recording
        commandBuffer.EndRendering();
        commandBuffer.End();
    }
}
