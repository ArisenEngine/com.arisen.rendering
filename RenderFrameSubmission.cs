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
    private RHIDevice m_Device;
    private RHISwapChain m_SwapChain;
    private uint m_SurfaceId;
    private uint m_FrameIndex;
    private uint m_Width;
    private uint m_Height;
    private RenderOutputKind m_OutputKind;
    private readonly RenderFrameSubmissionState m_State = new();
    private readonly RenderTargetImageStateTracker m_TargetImageState = new();

    public uint SurfaceId => m_SurfaceId;
    public uint FrameIndex => m_FrameIndex;
    public uint Width => m_Width;
    public uint Height => m_Height;
    public RenderOutputKind OutputKind => m_OutputKind;
    public RHIImageHandle TargetImage { get; private set; } = RHIImageHandle.Invalid;
    public bool TargetImageRequiresInitialization { get; private set; }
    public RHISwapChain SwapChain => m_SwapChain;
    public ulong LastTicket => m_State.LastTicket;
    public int SubmitCount => m_State.SubmitCount;
    public bool HasAcquiredFrame => m_State.HasFrameOwnership;

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

        if (m_State.HasFrameOwnership || m_State.RetirementPending)
        {
            throw new InvalidOperationException(
                $"Render submission for surface 0x{m_SurfaceId:X} still owns frame {m_FrameIndex}.");
        }

        m_State.ResetForBegin();
        m_Device = device;
        m_SwapChain = swapChain;
        m_SurfaceId = surfaceId;
        m_OutputKind = outputKind;
        m_FrameIndex = frameIndex;
        m_Width = width;
        m_Height = height;
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
        bool acquired = TargetImage.IsValid;
        if (acquired)
        {
            m_State.MarkAcquired();
        }
        TargetImageRequiresInitialization = m_TargetImageState.RequiresInitialization(TargetImage);
        Profiler.PlotValue("RenderSubmission.AcquireSucceeded", acquired ? 1 : 0);

        if (!acquired)
        {
            LogSkippedAcquire("swapchain did not return a valid image");
            return false;
        }

        if (RenderDiagnostics.IsEnabled(RenderDiagnosticCategory.Submission))
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

        if (!m_State.HasFrameOwnership)
        {
            throw new InvalidOperationException(
                $"Render submission for surface 0x{m_SurfaceId:X} attempted to submit before acquiring a frame.");
        }

        if (!commandBuffer.IsValid)
        {
            throw new InvalidOperationException(
                $"Render submission for surface 0x{m_SurfaceId:X} received an invalid graphics command buffer.");
        }

        m_State.ValidateSubmit(waitForFrameAcquire, signalFrameComplete);
        var waitSwapChain = waitForFrameAcquire ? m_SwapChain : (RHISwapChain?)null;
        var signalSwapChain = signalFrameComplete ? m_SwapChain : (RHISwapChain?)null;
        ulong ticket = m_Device.Submit(
            commandBuffer,
            waitSwapChain,
            signalSwapChain,
            m_FrameIndex);
        m_State.CommitSubmit(ticket, waitForFrameAcquire, signalFrameComplete);

        return ticket;
    }

    public bool End()
    {
        using var _ = Profiler.Zone("RenderSubmission.EndFrame");

        Profiler.PlotValue("RenderSubmission.SubmitCount", m_State.SubmitCount);
        Profiler.PlotValue("RenderSubmission.LastTicket", m_State.LastTicket);
        Profiler.PlotValue("RenderSubmission.Presented", 0);

        RenderFrameEndAction endAction = m_State.GetEndAction();
        if (endAction == RenderFrameEndAction.None)
        {
            return false;
        }

        if (endAction == RenderFrameEndAction.Retire)
        {
            string reason = m_State.SubmitCount == 0
                ? "no submitted command buffers"
                : "frame completion was not signaled";
            KernelLog.WarningFormat(
                "[RenderSubmission] Skipped present. Surface=0x{0:X}, Frame={1}, Reason={2}",
                m_SurfaceId,
                m_FrameIndex,
                reason);
            Retire();
            return false;
        }

        m_SwapChain.EndFrame(m_FrameIndex);
        m_State.MarkPresented();
        m_TargetImageState.MarkInitialized(TargetImage);
        Profiler.PlotValue("RenderSubmission.Presented", 1);

        if (RenderDiagnostics.IsEnabled(RenderDiagnosticCategory.Submission))
        {
            Logger.Log(
                $"[RenderSubmission] EndFrame | Surface: 0x{m_SurfaceId:X} | Frame: {m_FrameIndex} | " +
                $"Submits: {m_State.SubmitCount} | LastTicket: {m_State.LastTicket}");
        }

        if (m_OutputKind == RenderOutputKind.NativeSwapchain)
        {
            CommitOutput();
        }

        return true;
    }

    public void CommitOutput()
    {
        if (!m_State.HasFrameOwnership ||
            m_State.Phase != RenderFrameSubmissionPhase.Presented)
        {
            throw new InvalidOperationException(
                $"Render submission for surface 0x{m_SurfaceId:X} cannot commit an unpresented frame.");
        }

        m_State.CommitOutput();
    }

    public void Retire()
    {
        if (!m_State.TryBeginRetirement())
        {
            return;
        }

        try
        {
            using var _ = Profiler.Zone("RenderSubmission.RetireFrame");
            ulong retirementTicket = m_SwapChain.RetireFrame(m_FrameIndex);
            m_State.CommitRetirement(retirementTicket);
        }
        catch
        {
            m_State.CancelRetirement();
            throw;
        }
    }

    private void LogSkippedAcquire(string reason)
    {
        Profiler.PlotValue("RenderSubmission.AcquireSucceeded", 0);

        if (RenderDiagnostics.IsEnabled(RenderDiagnosticCategory.Submission))
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

}
