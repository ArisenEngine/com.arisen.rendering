using Arisen.Native.RHI;
using ArisenEngine.Core.RHI;

namespace ArisenEngine.Rendering;

/// <summary>
/// CPU-side recording facade used by RenderGraph passes.
/// </summary>
public readonly struct RenderCommandList
{
    private readonly RHICommandBuffer m_CommandBuffer;

    public RHICommandBuffer CommandBuffer => m_CommandBuffer;
    public bool IsValid => m_CommandBuffer.IsValid;

    internal RenderCommandList(RHICommandBuffer commandBuffer)
    {
        m_CommandBuffer = commandBuffer;
    }

    public void BeginRendering(RHIImageViewHandle colorImageView, EImageLayout imageLayout,
        EAttachmentLoadOp loadOp, EAttachmentStoreOp storeOp,
        float clearR, float clearG, float clearB, float clearA,
        int x, int y, uint width, uint height)
    {
        m_CommandBuffer.BeginRendering(
            colorImageView,
            imageLayout,
            loadOp,
            storeOp,
            clearR, clearG, clearB, clearA,
            x, y, width, height);
    }

    public void BeginRendering(
        RHIImageViewHandle colorImageView,
        EImageLayout imageLayout,
        EAttachmentLoadOp loadOp,
        EAttachmentStoreOp storeOp,
        float clearR,
        float clearG,
        float clearB,
        float clearA,
        RHIImageViewHandle depthImageView,
        EImageLayout depthImageLayout,
        EAttachmentLoadOp depthLoadOp,
        EAttachmentStoreOp depthStoreOp,
        float clearDepth,
        uint clearStencil,
        int x,
        int y,
        uint width,
        uint height)
    {
        m_CommandBuffer.BeginRendering(
            colorImageView,
            imageLayout,
            loadOp,
            storeOp,
            clearR,
            clearG,
            clearB,
            clearA,
            depthImageView,
            depthImageLayout,
            depthLoadOp,
            depthStoreOp,
            clearDepth,
            clearStencil,
            x,
            y,
            width,
            height);
    }

    public void EndRendering()
    {
        m_CommandBuffer.EndRendering();
    }

    public void BeginRenderingDepthOnly(
        RHIImageViewHandle depthImageView,
        EImageLayout depthImageLayout,
        EAttachmentLoadOp depthLoadOp,
        EAttachmentStoreOp depthStoreOp,
        float clearDepth,
        uint clearStencil,
        int x,
        int y,
        uint width,
        uint height)
    {
        m_CommandBuffer.BeginRenderingDepthOnly(
            depthImageView,
            depthImageLayout,
            depthLoadOp,
            depthStoreOp,
            clearDepth,
            clearStencil,
            x,
            y,
            width,
            height);
    }

    public void BindPipeline(RHIPipelineHandle pipeline)
    {
        m_CommandBuffer.BindPipeline(pipeline);
    }

    public unsafe void PushConstants<T>(T data, EShaderStage stageFlags, uint offset = 0)
        where T : unmanaged
    {
        m_CommandBuffer.PushConstants(offset, (uint)sizeof(T), (IntPtr)(&data), stageFlags);
    }

    public void SetViewport(float x, float y, float width, float height, float minDepth = 0.0f, float maxDepth = 1.0f)
    {
        m_CommandBuffer.SetViewport(x, y, width, height, minDepth, maxDepth);
    }

    public void SetScissor(uint offsetX, uint offsetY, uint width, uint height)
    {
        m_CommandBuffer.SetScissor(offsetX, offsetY, width, height);
    }

    public void BindVertexBuffers(RHIBufferHandle buffer, ulong offset = 0)
    {
        m_CommandBuffer.BindVertexBuffers(buffer, offset);
    }

    public void BindIndexBuffer(RHIBufferHandle buffer, ulong offset, EIndexType indexType)
    {
        m_CommandBuffer.BindIndexBuffer(buffer, offset, indexType);
    }

    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0,
        uint firstBinding = 0)
    {
        m_CommandBuffer.Draw(vertexCount, instanceCount, firstVertex, firstInstance, firstBinding);
    }

    public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0,
        uint firstInstance = 0, uint firstBinding = 0)
    {
        m_CommandBuffer.DrawIndexed(indexCount, instanceCount, firstIndex, vertexOffset, firstInstance, firstBinding);
    }

    public void CopyImageToBuffer2D(
        RHIImageHandle source,
        EImageLayout sourceLayout,
        EImageAspectFlagBits sourceAspect,
        RHIBufferHandle destination,
        ulong destinationOffset,
        uint width,
        uint height)
    {
        m_CommandBuffer.CopyImageToBuffer2D(
            source,
            sourceLayout,
            sourceAspect,
            destination,
            destinationOffset,
            width,
            height);
    }

    public void TransitionImageLayout(RHIImageHandle image, EImageLayout targetLayout)
    {
        m_CommandBuffer.TransitionImageLayout(image, targetLayout);
    }

    public void TransitionImageLayout(RHIImageHandle image, EImageLayout oldLayout, EImageLayout targetLayout)
    {
        m_CommandBuffer.TransitionImageLayout(image, oldLayout, targetLayout);
    }

    public void TransitionImageLayout(RHIImageHandle image, EImageLayout oldLayout, EImageLayout targetLayout,
        uint srcQueueFamilyIndex, uint dstQueueFamilyIndex)
    {
        m_CommandBuffer.TransitionImageLayout(
            image,
            oldLayout,
            targetLayout,
            srcQueueFamilyIndex,
            dstQueueFamilyIndex);
    }

    public void PipelineBarrier(
        EPipelineStageFlagBits srcStage,
        EPipelineStageFlagBits dstStage,
        ReadOnlySpan<RHIImageMemoryBarrier> imageBarriers,
        uint dependency = 0)
    {
        m_CommandBuffer.PipelineBarrier(srcStage, dstStage, imageBarriers, dependency);
    }

    public void PipelineBarrier(
        EPipelineStageFlagBits srcStage,
        EPipelineStageFlagBits dstStage,
        ReadOnlySpan<RHIBufferMemoryBarrier> bufferBarriers,
        uint dependency = 0)
    {
        m_CommandBuffer.PipelineBarrier(srcStage, dstStage, bufferBarriers, dependency);
    }

    public void PipelineBarrier(
        EPipelineStageFlagBits srcStage,
        EPipelineStageFlagBits dstStage,
        ReadOnlySpan<RHIImageMemoryBarrier> imageBarriers,
        ReadOnlySpan<RHIBufferMemoryBarrier> bufferBarriers,
        uint dependency = 0)
    {
        m_CommandBuffer.PipelineBarrier(srcStage, dstStage, imageBarriers, bufferBarriers, dependency);
    }
}
