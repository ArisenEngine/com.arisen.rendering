using Arisen.Native.RHI;

namespace ArisenEngine.Rendering;

public readonly record struct RenderGraphTextureDescriptor(
    string DebugName,
    uint Width,
    uint Height,
    EFormat Format,
    uint Usage,
    EImageAspectFlagBits AspectMask,
    bool RegisterBindlessSampled,
    uint ArrayLayers)
{
    public RenderGraphTextureDescriptor WithAdditionalUsage(EImageUsageFlagBits usage)
    {
        return this with { Usage = Usage | (uint)usage };
    }

    public static RenderGraphTextureDescriptor ColorAttachmentSampled2D(
        string debugName,
        uint width,
        uint height,
        EFormat format)
    {
        return new RenderGraphTextureDescriptor(
            debugName,
            width,
            height,
            format,
            (uint)EImageUsageFlagBits.IMAGE_USAGE_COLOR_ATTACHMENT_BIT |
            (uint)EImageUsageFlagBits.IMAGE_USAGE_SAMPLED_BIT,
            EImageAspectFlagBits.IMAGE_ASPECT_COLOR_BIT,
            RegisterBindlessSampled: true,
            ArrayLayers: 1);
    }

    public static RenderGraphTextureDescriptor DepthAttachment2D(
        string debugName,
        uint width,
        uint height,
        EFormat format)
    {
        return new RenderGraphTextureDescriptor(
            debugName,
            width,
            height,
            format,
            (uint)EImageUsageFlagBits.IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT,
            EImageAspectFlagBits.IMAGE_ASPECT_DEPTH_BIT,
            RegisterBindlessSampled: false,
            ArrayLayers: 1);
    }

    public static RenderGraphTextureDescriptor DepthAttachmentSampled2D(
        string debugName,
        uint width,
        uint height,
        EFormat format)
    {
        return new RenderGraphTextureDescriptor(
            debugName,
            width,
            height,
            format,
            (uint)EImageUsageFlagBits.IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT |
            (uint)EImageUsageFlagBits.IMAGE_USAGE_SAMPLED_BIT,
            EImageAspectFlagBits.IMAGE_ASPECT_DEPTH_BIT,
            RegisterBindlessSampled: true,
            ArrayLayers: 1);
    }

    public static RenderGraphTextureDescriptor DepthAttachmentSampled2DArray(
        string debugName,
        uint width,
        uint height,
        EFormat format,
        uint arrayLayers)
    {
        return new RenderGraphTextureDescriptor(
            debugName,
            width,
            height,
            format,
            (uint)EImageUsageFlagBits.IMAGE_USAGE_DEPTH_STENCIL_ATTACHMENT_BIT |
            (uint)EImageUsageFlagBits.IMAGE_USAGE_SAMPLED_BIT,
            EImageAspectFlagBits.IMAGE_ASPECT_DEPTH_BIT,
            RegisterBindlessSampled: true,
            arrayLayers);
    }
}
