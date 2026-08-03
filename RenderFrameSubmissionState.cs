namespace ArisenEngine.Rendering;

internal enum RenderFrameEndAction : byte
{
    None,
    Retire,
    Present
}

internal enum RenderFrameSubmissionPhase : byte
{
    Idle,
    Acquired,
    Presented,
    Committed,
    Retired
}

/// <summary>
/// Render-thread-owned state for one surface's acquired frame.
/// Native operations commit transitions only after returning successfully.
/// </summary>
internal sealed class RenderFrameSubmissionState
{
    public RenderFrameSubmissionPhase Phase { get; private set; }
    public int SubmitCount { get; private set; }
    public ulong LastTicket { get; private set; }
    public bool FrameCompleteSignaled { get; private set; }
    public bool RetirementPending { get; private set; }

    public bool HasFrameOwnership =>
        Phase is RenderFrameSubmissionPhase.Acquired or RenderFrameSubmissionPhase.Presented;

    public void ResetForBegin()
    {
        if (HasFrameOwnership || RetirementPending)
        {
            throw new InvalidOperationException(
                $"Cannot begin a frame while submission state is {Phase}.");
        }

        Phase = RenderFrameSubmissionPhase.Idle;
        SubmitCount = 0;
        LastTicket = 0;
        FrameCompleteSignaled = false;
        RetirementPending = false;
    }

    public void MarkAcquired()
    {
        if (Phase != RenderFrameSubmissionPhase.Idle || RetirementPending)
        {
            throw new InvalidOperationException(
                $"Cannot acquire a frame while submission state is {Phase}.");
        }

        Phase = RenderFrameSubmissionPhase.Acquired;
    }

    public void ValidateSubmit(bool waitForFrameAcquire, bool signalFrameComplete)
    {
        if (Phase != RenderFrameSubmissionPhase.Acquired || RetirementPending)
        {
            throw new InvalidOperationException(
                $"Cannot submit graphics work while submission state is {Phase}.");
        }
        if (FrameCompleteSignaled)
        {
            throw new InvalidOperationException(
                "Cannot submit graphics work after frame completion was signaled.");
        }
        if (SubmitCount == 0 && !waitForFrameAcquire)
        {
            throw new InvalidOperationException(
                "The first frame submission must wait for swapchain acquisition.");
        }
        if (SubmitCount != 0 && waitForFrameAcquire)
        {
            throw new InvalidOperationException(
                "Only the first frame submission may wait for swapchain acquisition.");
        }
    }

    public void CommitSubmit(
        ulong ticket,
        bool waitForFrameAcquire,
        bool signalFrameComplete)
    {
        ValidateSubmit(waitForFrameAcquire, signalFrameComplete);
        if (ticket == 0 || (LastTicket != 0 && ticket <= LastTicket))
        {
            throw new InvalidOperationException(
                $"Frame submission ticket {ticket} is not newer than {LastTicket}.");
        }

        LastTicket = ticket;
        SubmitCount++;
        FrameCompleteSignaled = signalFrameComplete;
    }

    public RenderFrameEndAction GetEndAction()
    {
        if (Phase != RenderFrameSubmissionPhase.Acquired || RetirementPending)
        {
            return RenderFrameEndAction.None;
        }

        return SubmitCount == 0 || !FrameCompleteSignaled
            ? RenderFrameEndAction.Retire
            : RenderFrameEndAction.Present;
    }

    public void MarkPresented()
    {
        if (GetEndAction() != RenderFrameEndAction.Present)
        {
            throw new InvalidOperationException(
                "A frame can be marked presented only after its completion submission.");
        }

        Phase = RenderFrameSubmissionPhase.Presented;
    }

    public void CommitOutput()
    {
        if (Phase != RenderFrameSubmissionPhase.Presented || RetirementPending)
        {
            throw new InvalidOperationException(
                $"Cannot commit frame output while submission state is {Phase}.");
        }

        Phase = RenderFrameSubmissionPhase.Committed;
    }

    public bool TryBeginRetirement()
    {
        if (!HasFrameOwnership)
        {
            return false;
        }
        if (RetirementPending)
        {
            throw new InvalidOperationException("Frame retirement is already pending.");
        }

        RetirementPending = true;
        return true;
    }

    public void CommitRetirement(ulong ticket)
    {
        if (!RetirementPending || !HasFrameOwnership)
        {
            throw new InvalidOperationException("No owned frame is pending retirement.");
        }
        if (ticket < LastTicket)
        {
            throw new InvalidOperationException(
                $"Frame retirement ticket {ticket} precedes submission ticket {LastTicket}.");
        }

        if (ticket > LastTicket)
        {
            LastTicket = ticket;
        }
        RetirementPending = false;
        Phase = RenderFrameSubmissionPhase.Retired;
    }

    public void CancelRetirement()
    {
        if (!RetirementPending)
        {
            return;
        }

        RetirementPending = false;
    }
}
