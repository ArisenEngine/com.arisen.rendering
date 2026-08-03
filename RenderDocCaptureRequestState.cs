using System;
using System.Threading;
using ArisenKernel.Contracts;

namespace ArisenEngine.Rendering;

public enum RenderDocCaptureRequestStatus
{
    None = 0,
    Pending = 1,
    Capturing = 2,
    PublishingArtifact = 3,
    Succeeded = 4,
    Failed = 5
}

public enum RenderDocCaptureFailureStage
{
    None = 0,
    SurfaceUnregistered = 1,
    CaptureStart = 2,
    SurfaceFrame = 3,
    CaptureEnd = 4,
    RenderSubsystemShutdown = 5,
    ArtifactPublication = 6
}

public readonly record struct RenderDocCaptureRequestSnapshot(
    ulong RequestId,
    RenderSurfaceRegistration Target,
    RenderDocCaptureRequestStatus Status,
    RenderDocCaptureFailureStage FailureStage,
    string Diagnostic,
    string CapturePath)
{
    public bool HasRequest => RequestId != 0;

    public bool IsActive =>
        Status is RenderDocCaptureRequestStatus.Pending or
            RenderDocCaptureRequestStatus.Capturing or
            RenderDocCaptureRequestStatus.PublishingArtifact;

    public bool IsTerminal =>
        Status is RenderDocCaptureRequestStatus.Succeeded or
            RenderDocCaptureRequestStatus.Failed;
}

internal readonly record struct RenderDocCaptureLease(
    ulong RequestId,
    RenderSurfaceRegistration Target)
{
    public bool IsValid => RequestId != 0 && Target.IsValid;
}

/// <summary>
/// Owns the logical lifetime of one RenderDoc request. The render-thread read path performs
/// one volatile reference read and allocates only when the exact target changes state.
/// </summary>
internal sealed class RenderDocCaptureRequestState
{
    private sealed class State
    {
        public static readonly State Empty = new(
            0,
            default,
            RenderDocCaptureRequestStatus.None,
            RenderDocCaptureFailureStage.None,
            string.Empty,
            string.Empty);

        public State(
            ulong requestId,
            RenderSurfaceRegistration target,
            RenderDocCaptureRequestStatus status,
            RenderDocCaptureFailureStage failureStage,
            string diagnostic,
            string capturePath)
        {
            RequestId = requestId;
            Target = target;
            Status = status;
            FailureStage = failureStage;
            Diagnostic = diagnostic;
            CapturePath = capturePath;
        }

        public ulong RequestId { get; }
        public RenderSurfaceRegistration Target { get; }
        public RenderDocCaptureRequestStatus Status { get; }
        public RenderDocCaptureFailureStage FailureStage { get; }
        public string Diagnostic { get; }
        public string CapturePath { get; }

        public bool IsActive =>
            Status is RenderDocCaptureRequestStatus.Pending or
                RenderDocCaptureRequestStatus.Capturing or
                RenderDocCaptureRequestStatus.PublishingArtifact;

        public RenderDocCaptureRequestSnapshot Snapshot => new(
            RequestId,
            Target,
            Status,
            FailureStage,
            Diagnostic,
            CapturePath);
    }

    private readonly object m_RequestGate = new();
    private State m_State = State.Empty;
    private ulong m_NextRequestId;

    public RenderDocCaptureRequestSnapshot Snapshot =>
        Volatile.Read(ref m_State).Snapshot;

    public bool TryRequest(
        RenderSurfaceRegistration target,
        out RenderDocCaptureRequestSnapshot snapshot)
    {
        if (!target.IsValid)
        {
            throw new ArgumentException(
                "A RenderDoc capture requires a valid render-surface registration.",
                nameof(target));
        }

        lock (m_RequestGate)
        {
            State current = Volatile.Read(ref m_State);
            if (current.IsActive)
            {
                snapshot = current.Snapshot;
                return false;
            }
            if (m_NextRequestId == ulong.MaxValue)
            {
                throw new InvalidOperationException(
                    "RenderDoc capture request identity is exhausted.");
            }

            var pending = new State(
                ++m_NextRequestId,
                target,
                RenderDocCaptureRequestStatus.Pending,
                RenderDocCaptureFailureStage.None,
                string.Empty,
                string.Empty);
            Volatile.Write(ref m_State, pending);
            snapshot = pending.Snapshot;
            return true;
        }
    }

    public bool TryBeginCapture(
        RenderSurfaceRegistration surface,
        out RenderDocCaptureLease lease,
        out RenderDocCaptureRequestSnapshot snapshot)
    {
        while (true)
        {
            State current = Volatile.Read(ref m_State);
            if (current.Status != RenderDocCaptureRequestStatus.Pending ||
                current.Target != surface)
            {
                lease = default;
                snapshot = current.Snapshot;
                return false;
            }

            var capturing = new State(
                current.RequestId,
                current.Target,
                RenderDocCaptureRequestStatus.Capturing,
                RenderDocCaptureFailureStage.None,
                string.Empty,
                string.Empty);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref m_State, capturing, current),
                    current))
            {
                lease = new RenderDocCaptureLease(current.RequestId, current.Target);
                snapshot = capturing.Snapshot;
                return true;
            }
        }
    }

    public bool TryBeginArtifactPublication(
        RenderDocCaptureLease lease,
        string diagnostic,
        out RenderDocCaptureRequestSnapshot snapshot)
    {
        if (!lease.IsValid)
        {
            snapshot = Snapshot;
            return false;
        }

        while (true)
        {
            State current = Volatile.Read(ref m_State);
            if (current.Status != RenderDocCaptureRequestStatus.Capturing ||
                current.RequestId != lease.RequestId ||
                current.Target != lease.Target)
            {
                snapshot = current.Snapshot;
                return false;
            }

            var publishing = new State(
                current.RequestId,
                current.Target,
                RenderDocCaptureRequestStatus.PublishingArtifact,
                RenderDocCaptureFailureStage.None,
                diagnostic ?? string.Empty,
                string.Empty);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref m_State, publishing, current),
                    current))
            {
                snapshot = publishing.Snapshot;
                return true;
            }
        }
    }

    public bool TryCompleteArtifact(
        RenderDocCaptureLease lease,
        string capturePath,
        string diagnostic,
        out RenderDocCaptureRequestSnapshot snapshot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(capturePath);

        if (!lease.IsValid)
        {
            snapshot = Snapshot;
            return false;
        }

        while (true)
        {
            State current = Volatile.Read(ref m_State);
            if (current.Status != RenderDocCaptureRequestStatus.PublishingArtifact ||
                current.RequestId != lease.RequestId ||
                current.Target != lease.Target)
            {
                snapshot = current.Snapshot;
                return false;
            }

            var completed = new State(
                current.RequestId,
                current.Target,
                RenderDocCaptureRequestStatus.Succeeded,
                RenderDocCaptureFailureStage.None,
                diagnostic ?? string.Empty,
                capturePath);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref m_State, completed, current),
                    current))
            {
                snapshot = completed.Snapshot;
                return true;
            }
        }
    }

    public bool TryFail(
        RenderDocCaptureLease lease,
        RenderDocCaptureFailureStage failureStage,
        string diagnostic,
        out RenderDocCaptureRequestSnapshot snapshot)
    {
        if (failureStage == RenderDocCaptureFailureStage.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failureStage));
        }

        if (!lease.IsValid)
        {
            snapshot = Snapshot;
            return false;
        }

        while (true)
        {
            State current = Volatile.Read(ref m_State);
            if (current.Status is not (
                    RenderDocCaptureRequestStatus.Capturing or
                    RenderDocCaptureRequestStatus.PublishingArtifact) ||
                current.RequestId != lease.RequestId ||
                current.Target != lease.Target)
            {
                snapshot = current.Snapshot;
                return false;
            }

            var failed = new State(
                current.RequestId,
                current.Target,
                RenderDocCaptureRequestStatus.Failed,
                failureStage,
                diagnostic ?? string.Empty,
                string.Empty);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref m_State, failed, current),
                    current))
            {
                snapshot = failed.Snapshot;
                return true;
            }
        }
    }

    public bool TryFailActiveForSurface(
        RenderSurfaceRegistration surface,
        RenderDocCaptureFailureStage failureStage,
        string diagnostic,
        out RenderDocCaptureRequestSnapshot snapshot)
    {
        return TryFailActive(
            state =>
                state.Target == surface &&
                (state.Status is RenderDocCaptureRequestStatus.Pending or
                    RenderDocCaptureRequestStatus.Capturing),
            failureStage,
            diagnostic,
            out snapshot);
    }

    public bool TryFailActive(
        RenderDocCaptureFailureStage failureStage,
        string diagnostic,
        out RenderDocCaptureRequestSnapshot snapshot)
    {
        return TryFailActive(
            static _ => true,
            failureStage,
            diagnostic,
            out snapshot);
    }

    private bool TryFailActive(
        Func<State, bool> predicate,
        RenderDocCaptureFailureStage failureStage,
        string diagnostic,
        out RenderDocCaptureRequestSnapshot snapshot)
    {
        if (failureStage == RenderDocCaptureFailureStage.None)
        {
            throw new ArgumentOutOfRangeException(nameof(failureStage));
        }

        while (true)
        {
            State current = Volatile.Read(ref m_State);
            if (!current.IsActive || !predicate(current))
            {
                snapshot = current.Snapshot;
                return false;
            }

            var failed = new State(
                current.RequestId,
                current.Target,
                RenderDocCaptureRequestStatus.Failed,
                failureStage,
                diagnostic ?? string.Empty,
                string.Empty);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref m_State, failed, current),
                    current))
            {
                snapshot = failed.Snapshot;
                return true;
            }
        }
    }
}
