using System.Runtime.InteropServices;
using Arisen.Native.RHI;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using ArisenKernel.Diagnostics;
using ArisenKernel.Lifecycle;

namespace ArisenEngine.Rendering;

internal sealed class RenderOutputReadbackPass : RenderPassNode, IDisposable
{
    private const long MaximumReadbackByteCount = 256L * 1024L * 1024L;

    private readonly IRuntimeVisualSummaryService m_Service;
    private RHIFactory m_Factory;
    private RHIBufferHandle m_ReadbackBuffer = RHIBufferHandle.Invalid;
    private RHIImageHandle m_DepthImage = RHIImageHandle.Invalid;
    private long m_ColorReadbackByteCount;
    private long m_DepthReadbackByteCount;
    private long m_TotalReadbackByteCount;
    private ulong m_DepthBufferOffset;
    private EFormat m_Format = EFormat.FORMAT_UNDEFINED;
    private EFormat m_DepthFormat = EFormat.FORMAT_UNDEFINED;
    private uint m_Width;
    private uint m_Height;
    private uint m_FrameIndex;
    private uint m_SurfaceId;
    private RenderOutputKind m_OutputKind;
    private bool m_CapturePending;
    private bool m_Disposed;

    public RenderOutputReadbackPass(
        IRuntimeVisualSummaryService service,
        string name = "RenderOutputReadbackPass")
        : base(name)
    {
        m_Service = service ?? throw new ArgumentNullException(nameof(service));
    }

    public bool TryPrepare(RenderContext context, RenderGraphTexture? frameDepth)
    {
        ThrowIfDisposed();
        if (!m_Service.IsEnabled ||
            context.OutputKind != RenderOutputKind.NativeSwapchain ||
            context.FrameIndex != m_Service.CaptureFrameIndex ||
            !m_Service.TryBeginCapture(context.FrameIndex))
        {
            return false;
        }

        try
        {
            if (frameDepth == null || !frameDepth.IsValid)
            {
                throw new InvalidOperationException(
                    "Visual-summary capture requires a valid published frame-depth texture.");
            }

            if (frameDepth.Width != context.Width || frameDepth.Height != context.Height)
            {
                throw new InvalidOperationException(
                    $"Visual-summary frame depth is {frameDepth.Width}x{frameDepth.Height}, " +
                    $"but final color is {context.Width}x{context.Height}.");
            }

            if ((frameDepth.Usage & (uint)EImageUsageFlagBits.IMAGE_USAGE_TRANSFER_SRC_BIT) == 0)
            {
                throw new InvalidOperationException(
                    "Visual-summary frame depth was not created with transfer-source usage.");
            }

            if ((frameDepth.AspectMask & EImageAspectFlagBits.IMAGE_ASPECT_DEPTH_BIT) == 0)
            {
                throw new InvalidOperationException(
                    $"Visual-summary frame depth has incompatible aspect '{frameDepth.AspectMask}'.");
            }

            m_Factory = context.Device.GetFactory();
            var imageView = context.SwapChain.GetImageView(context.FrameIndex);
            m_Format = m_Factory.GetImageViewFormat(imageView);
            m_DepthImage = frameDepth.Image;
            m_DepthFormat = frameDepth.Format;
            m_ColorReadbackByteCount = RenderOutputImageSummaryBuilder.GetRequiredByteCount(
                context.Width,
                context.Height,
                m_Format);
            m_DepthReadbackByteCount = RenderDepthImageSummaryBuilder.GetRequiredByteCount(
                frameDepth.Width,
                frameDepth.Height,
                m_DepthFormat);
            m_DepthBufferOffset = checked((ulong)m_ColorReadbackByteCount);
            m_TotalReadbackByteCount = checked(m_ColorReadbackByteCount + m_DepthReadbackByteCount);
            if (m_TotalReadbackByteCount > MaximumReadbackByteCount)
            {
                throw new InvalidOperationException(
                    $"Visual-summary readback requires {m_TotalReadbackByteCount} bytes, exceeding the " +
                    $"{MaximumReadbackByteCount}-byte bounded capture limit.");
            }

            EnsureReadbackBuffer();
            m_Width = context.Width;
            m_Height = context.Height;
            m_FrameIndex = context.FrameIndex;
            m_SurfaceId = context.SurfaceId;
            m_OutputKind = context.OutputKind;
            m_CapturePending = true;

            Logger.Log(
                $"[VisualSummary] Prepared readback | Profile: {m_Service.ProfileName} | " +
                $"Frame: {m_FrameIndex} | Size: {m_Width}x{m_Height} | Format: {m_Format} | " +
                $"DepthFormat: {m_DepthFormat} | ColorBytes: {m_ColorReadbackByteCount} | " +
                $"DepthBytes: {m_DepthReadbackByteCount} | TotalBytes: {m_TotalReadbackByteCount}");
            return true;
        }
        catch (Exception ex)
        {
            m_Service.ReportFailure(ex.Message);
            throw;
        }
    }

    protected override void Record(RenderContext context, RenderCommandList commandList)
    {
        if (!m_CapturePending || !m_ReadbackBuffer.IsValid)
        {
            throw new InvalidOperationException(
                "Visual-summary readback pass recorded without a prepared destination buffer.");
        }

        commandList.CopyImageToBuffer2D(
            context.TargetImage,
            EImageLayout.IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
            EImageAspectFlagBits.IMAGE_ASPECT_COLOR_BIT,
            m_ReadbackBuffer,
            0,
            m_Width,
            m_Height);
        commandList.CopyImageToBuffer2D(
            m_DepthImage,
            EImageLayout.IMAGE_LAYOUT_TRANSFER_SRC_OPTIMAL,
            EImageAspectFlagBits.IMAGE_ASPECT_DEPTH_BIT,
            m_ReadbackBuffer,
            m_DepthBufferOffset,
            m_Width,
            m_Height);
    }

    public void Complete(RenderContext context, ulong submittedTicket)
    {
        if (!m_CapturePending)
        {
            return;
        }

        try
        {
            if (submittedTicket == 0)
            {
                throw new InvalidOperationException(
                    "Visual-summary readback produced no GPU submission ticket.");
            }

            context.Device.WaitQueueTicket(submittedTicket);
            var mappedData = m_Factory.MapBuffer(m_ReadbackBuffer);
            if (mappedData == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "Visual-summary readback buffer could not be mapped.");
            }

            byte[] pixels;
            try
            {
                pixels = GC.AllocateUninitializedArray<byte>(checked((int)m_TotalReadbackByteCount));
                Marshal.Copy(mappedData, pixels, 0, pixels.Length);
            }
            finally
            {
                m_Factory.UnmapBuffer(m_ReadbackBuffer);
            }

            var colorByteCount = checked((int)m_ColorReadbackByteCount);
            var depthByteCount = checked((int)m_DepthReadbackByteCount);
            var depthOffset = checked((int)m_DepthBufferOffset);
            var artifact = RenderOutputImageSummaryBuilder.Build(
                pixels.AsSpan(0, colorByteCount),
                m_Width,
                m_Height,
                m_Format,
                pixels.AsSpan(depthOffset, depthByteCount),
                m_DepthFormat,
                m_Service.ProfileName,
                m_OutputKind,
                m_SurfaceId,
                m_FrameIndex);
            RenderOutputImageSummaryWriter.WriteAtomic(m_Service.OutputPath, artifact);

            if (artifact.Passed)
            {
                m_Service.ReportSuccess();
                KernelLog.InfoFormat(
                    "[VisualSummary] Passed. Output={0}, NonBlank={1}/{2}, LuminanceRange={3:F6}",
                    m_Service.OutputPath,
                    artifact.NonBlankPixelCount,
                    artifact.PixelCount,
                    artifact.MaximumLuminance - artifact.MinimumLuminance);
                KernelLog.InfoFormat(
                    "[VisualSummary] Depth passed. Written={0}/{1}, Clear={2}/{1}, Range={3:F6}",
                    artifact.Depth.WrittenDepthPixelCount,
                    artifact.Depth.PixelCount,
                    artifact.Depth.ClearDepthPixelCount,
                    artifact.Depth.MaximumDepth - artifact.Depth.MinimumDepth);
            }
            else
            {
                var failure =
                    $"Visual-summary checks failed. NonBlank={artifact.NonBlankPixelCount}/" +
                    $"{artifact.Checks.RequiredNonBlankPixelCount}, LuminanceRange=" +
                    $"{artifact.MaximumLuminance - artifact.MinimumLuminance:F6}/" +
                    $"{artifact.Checks.RequiredLuminanceRange:F6}, DepthWritten=" +
                    $"{artifact.Depth.WrittenDepthPixelCount}/" +
                    $"{artifact.Depth.Checks.RequiredWrittenDepthPixelCount}, DepthRange=" +
                    $"{artifact.Depth.MaximumDepth - artifact.Depth.MinimumDepth:F6}/" +
                    $"{artifact.Depth.Checks.RequiredDepthRange:F6}. Artifact: {m_Service.OutputPath}";
                m_Service.ReportFailure(failure);
                KernelLog.WarningFormat("[VisualSummary] {0}", failure);
            }
        }
        catch (Exception ex)
        {
            m_Service.ReportFailure(ex.Message);
            throw;
        }
        finally
        {
            m_CapturePending = false;
        }
    }

    public void Dispose()
    {
        if (m_Disposed)
        {
            return;
        }

        if (m_ReadbackBuffer.IsValid && m_Factory.IsValid)
        {
            m_Factory.ReleaseBuffer(m_ReadbackBuffer);
        }

        m_ReadbackBuffer = RHIBufferHandle.Invalid;
        m_Disposed = true;
    }

    private void EnsureReadbackBuffer()
    {
        if (m_ReadbackBuffer.IsValid)
        {
            m_Factory.ReleaseBuffer(m_ReadbackBuffer);
            m_ReadbackBuffer = RHIBufferHandle.Invalid;
        }

        m_ReadbackBuffer = m_Factory.CreateBuffer(
            checked((ulong)m_TotalReadbackByteCount),
            (uint)EBufferUsageFlagBits.BUFFER_USAGE_TRANSFER_DST_BIT,
            ESharingMode.SHARING_MODE_EXCLUSIVE,
            ERHIMemoryUsage.Readback,
            "RuntimeVisualSummaryReadback");
        if (!m_ReadbackBuffer.IsValid)
        {
            throw new InvalidOperationException(
                "Visual-summary readback buffer allocation failed.");
        }
    }

    private void ThrowIfDisposed()
    {
        if (m_Disposed)
        {
            throw new ObjectDisposedException(nameof(RenderOutputReadbackPass));
        }
    }
}
