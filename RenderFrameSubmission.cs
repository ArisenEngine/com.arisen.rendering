using Arisen.Native.RHI;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.RHI;
using ArisenKernel.Diagnostics;

namespace ArisenEngine.Rendering;

/// <summary>
/// Per-surface owner for output acquire, ordered queue submission, presentation,
/// and frame-output diagnostics.
/// </summary>
internal sealed class RenderFrameSubmission
{
    private const uint DiagnosticsFrameInterval = 60;

    private RHIDevice m_Device;
    private RHISwapChain m_SwapChain;
    private uint m_SurfaceId;
    private uint m_FrameIndex;
    private uint m_Width;
    private uint m_Height;
    private RenderOutputKind m_OutputKind;
    private bool m_Acquired;
    private bool m_FrameCompleteSignaled;
    private int m_SubmitCount;
    private ulong m_LastTicket;
    private readonly RenderTargetImageStateTracker m_TargetImageState = new();

    public uint SurfaceId => m_SurfaceId;
    public uint FrameIndex => m_FrameIndex;
    public uint Width => m_Width;
    public uint Height => m_Height;
    public RenderOutputKind OutputKind => m_OutputKind;
    public RHIImageHandle TargetImage { get; private set; } = RHIImageHandle.Invalid;
    public bool TargetImageRequiresInitialization { get; private set; }
    public RHISwapChain SwapChain => m_SwapChain;
    public ulong LastTicket => m_LastTicket;
    public int SubmitCount => m_SubmitCount;
    public bool HasAcquiredFrame => m_Acquired;

    public bool Begin(
        RHIDevice device,
        RHISwapChain swapChain,
        uint surfaceId,
        RenderOutputKind outputKind,
        uint frameIndex,
        uint width,
        uint height)
    {
        using var _ = Profiler.Zone("RenderSubmission.BeginFrame");

        m_Device = device;
        m_SwapChain = swapChain;
        m_SurfaceId = surfaceId;
        m_OutputKind = outputKind;
        m_FrameIndex = frameIndex;
        m_Width = width;
        m_Height = height;
        m_SubmitCount = 0;
        m_LastTicket = 0;
        m_FrameCompleteSignaled = false;
        m_Acquired = false;
        TargetImage = RHIImageHandle.Invalid;
        TargetImageRequiresInitialization = false;

        if (!m_Device.IsValid)
        {
            LogSkippedAcquire("invalid RHI device");
            return false;
        }

        if (!m_SwapChain.IsValid)
        {
            LogSkippedAcquire("invalid swapchain");
            return false;
        }

        TargetImage = m_SwapChain.BeginFrame(frameIndex);
        m_Acquired = TargetImage.IsValid;
        TargetImageRequiresInitialization = m_TargetImageState.RequiresInitialization(TargetImage);
        Profiler.PlotValue("RenderSubmission.AcquireSucceeded", m_Acquired ? 1 : 0);

        if (!m_Acquired)
        {
            LogSkippedAcquire("swapchain did not return a valid image");
            return false;
        }

        if (ShouldLogDiagnostics())
        {
            Logger.Log(
                $"[RenderSubmission] BeginFrame | Surface: 0x{m_SurfaceId:X} | Frame: {m_FrameIndex} | " +
                $"Size: {m_Width}x{m_Height} | Output: {m_OutputKind} | " +
                $"Image: {TargetImage.Index}:{TargetImage.Generation}");
        }

        return true;
    }

    public ulong SubmitGraphics(RHICommandBuffer commandBuffer, bool waitForFrameAcquire, bool signalFrameComplete)
    {
        using var _ = Profiler.Zone("RenderSubmission.SubmitGraphics");

        if (!m_Acquired)
        {
            throw new InvalidOperationException(
                $"Render submission for surface 0x{m_SurfaceId:X} attempted to submit before acquiring a frame.");
        }

        if (!commandBuffer.IsValid)
        {
            throw new InvalidOperationException(
                $"Render submission for surface 0x{m_SurfaceId:X} received an invalid graphics command buffer.");
        }

        var waitSwapChain = waitForFrameAcquire ? m_SwapChain : (RHISwapChain?)null;
        var signalSwapChain = signalFrameComplete ? m_SwapChain : (RHISwapChain?)null;
        m_LastTicket = m_Device.Submit(
            commandBuffer,
            waitSwapChain,
            signalSwapChain,
            m_FrameIndex);
        m_SubmitCount++;
        m_FrameCompleteSignaled |= signalFrameComplete;

        return m_LastTicket;
    }

    public void End()
    {
        using var _ = Profiler.Zone("RenderSubmission.EndFrame");

        Profiler.PlotValue("RenderSubmission.SubmitCount", m_SubmitCount);
        Profiler.PlotValue("RenderSubmission.LastTicket", m_LastTicket);
        Profiler.PlotValue("RenderSubmission.Presented", 0);

        if (!m_Acquired)
        {
            return;
        }

        if (m_SubmitCount == 0)
        {
            KernelLog.WarningFormat(
                "[RenderSubmission] Skipped present. Surface=0x{0:X}, Frame={1}, Reason=no submitted command buffers",
                m_SurfaceId,
                m_FrameIndex);
            return;
        }

        if (!m_FrameCompleteSignaled)
        {
            KernelLog.WarningFormat(
                "[RenderSubmission] Skipped present. Surface=0x{0:X}, Frame={1}, Reason=frame completion was not signaled",
                m_SurfaceId,
                m_FrameIndex);
            return;
        }

        m_SwapChain.EndFrame(m_FrameIndex);
        m_TargetImageState.MarkInitialized(TargetImage);
        Profiler.PlotValue("RenderSubmission.Presented", 1);

        if (ShouldLogDiagnostics())
        {
            Logger.Log(
                $"[RenderSubmission] EndFrame | Surface: 0x{m_SurfaceId:X} | Frame: {m_FrameIndex} | " +
                $"Submits: {m_SubmitCount} | LastTicket: {m_LastTicket}");
        }
    }

    private void LogSkippedAcquire(string reason)
    {
        Profiler.PlotValue("RenderSubmission.AcquireSucceeded", 0);

        if (ShouldLogDiagnostics())
        {
            KernelLog.WarningFormat(
                "[RenderSubmission] Skipped frame acquire. Surface=0x{0:X}, Frame={1}, Size={2}x{3}, Reason={4}",
                m_SurfaceId,
                m_FrameIndex,
                m_Width,
                m_Height,
                reason);
        }
    }

    private bool ShouldLogDiagnostics()
    {
        return m_FrameIndex % DiagnosticsFrameInterval == 0;
    }
}
