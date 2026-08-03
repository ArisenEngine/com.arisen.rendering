using System;
using Arisen.Native.RHI;
using ArisenEngine.Core.RHI;

namespace ArisenEngine.Rendering;

internal static class RenderFrameColorOutputPolicy
{
    public static RenderFrameColorRhiState Resolve(
        RenderOutputKind outputKind,
        bool targetImageRequiresInitialization,
        bool isSource)
    {
        if (isSource && targetImageRequiresInitialization)
        {
            return new RenderFrameColorRhiState(
                EImageLayout.IMAGE_LAYOUT_UNDEFINED,
                EAccessFlag.ACCESS_NONE,
                RHIQueueFamily.Ignored,
                EPipelineStageFlagBits.PIPELINE_STAGE_TOP_OF_PIPE_BIT);
        }

        return outputKind switch
        {
            RenderOutputKind.NativeSwapchain => new RenderFrameColorRhiState(
                EImageLayout.IMAGE_LAYOUT_PRESENT_SRC_KHR,
                EAccessFlag.ACCESS_NONE,
                RHIQueueFamily.Ignored,
                isSource
                    ? EPipelineStageFlagBits.PIPELINE_STAGE_TOP_OF_PIPE_BIT
                    : EPipelineStageFlagBits.PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT),
            RenderOutputKind.EditorSharedTexture => new RenderFrameColorRhiState(
                EImageLayout.IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                EAccessFlag.ACCESS_NONE,
                RHIQueueFamily.External,
                isSource
                    ? EPipelineStageFlagBits.PIPELINE_STAGE_TOP_OF_PIPE_BIT
                    : EPipelineStageFlagBits.PIPELINE_STAGE_BOTTOM_OF_PIPE_BIT),
            RenderOutputKind.Offscreen => new RenderFrameColorRhiState(
                EImageLayout.IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
                EAccessFlag.ACCESS_TRANSFER_READ_BIT,
                RHIQueueFamily.Ignored,
                EPipelineStageFlagBits.PIPELINE_STAGE_TRANSFER_BIT),
            _ => throw new ArgumentOutOfRangeException(
                nameof(outputKind),
                outputKind,
                "Unsupported frame output kind.")
        };
    }
}

internal readonly struct RenderFrameColorRhiState
{
    public RenderFrameColorRhiState(
        EImageLayout layout,
        EAccessFlag access,
        uint queueFamily,
        EPipelineStageFlagBits stage)
    {
        Layout = layout;
        Access = access;
        QueueFamily = queueFamily;
        Stage = stage;
    }

    public EImageLayout Layout { get; }
    public EAccessFlag Access { get; }
    public uint QueueFamily { get; }
    public EPipelineStageFlagBits Stage { get; }
}
