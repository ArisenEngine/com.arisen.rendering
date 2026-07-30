using ArisenKernel.Contracts;
using ArisenKernel.Diagnostics;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArisenEngine.Rendering;

public enum GraphicsDeviceLifecycleState
{
    Running = 0,
    Quiescing = 1,
    RecreatingBackend = 2,
    Restoring = 3,
    Failed = 4
}

public readonly record struct GraphicsDeviceLifecycleSnapshot(
    GraphicsDeviceLifecycleState State,
    ulong Generation,
    RHIBackendDiagnosticMode DiagnosticMode,
    string Diagnostic);

public readonly record struct GraphicsDeviceRestartContext(
    RHIBackendRestartOptions Options,
    ulong PreviousGeneration,
    ulong CurrentGeneration);

public readonly record struct GraphicsDeviceRestartResult(
    bool Succeeded,
    ulong PreviousGeneration,
    ulong CurrentGeneration,
    RHIBackendDiagnosticMode DiagnosticMode,
    string Diagnostic);

public interface IGraphicsDeviceLifecycleParticipant
{
    string ParticipantId { get; }

    /// <summary>
    /// Lower values prepare earlier and restore later so ownership dependencies
    /// unwind and rebuild in opposite orders.
    /// </summary>
    int Order { get; }

    Task PrepareForGraphicsDeviceRestartAsync(
        GraphicsDeviceRestartContext context,
        CancellationToken cancellationToken);

    Task RestoreAfterGraphicsDeviceRestartAsync(
        GraphicsDeviceRestartContext context,
        CancellationToken cancellationToken);
}

public interface IGraphicsDeviceLifecycleService
{
    GraphicsDeviceLifecycleSnapshot Snapshot { get; }

    event Action<GraphicsDeviceLifecycleSnapshot>? StateChanged;

    void RegisterParticipant(IGraphicsDeviceLifecycleParticipant participant);

    void UnregisterParticipant(string participantId);

    Task<GraphicsDeviceRestartResult> RestartAsync(
        RHIBackendRestartOptions options,
        CancellationToken cancellationToken = default);
}

public delegate Task<ulong> GraphicsDeviceRestartExecutor(
    RHIBackendRestartOptions options,
    CancellationToken cancellationToken);

public sealed class GraphicsDeviceLifecycleCoordinator : IGraphicsDeviceLifecycleService
{
    private readonly object m_Gate = new();
    private readonly Dictionary<string, IGraphicsDeviceLifecycleParticipant> m_Participants =
        new(StringComparer.Ordinal);
    private readonly SemaphoreSlim m_RestartGate = new(1, 1);
    private readonly GraphicsDeviceRestartExecutor m_Executor;
    private readonly Func<ulong> m_GetCurrentGeneration;
    private GraphicsDeviceLifecycleSnapshot m_Snapshot = new(
        GraphicsDeviceLifecycleState.Running,
        0,
        RHIBackendDiagnosticMode.None,
        string.Empty);

    public GraphicsDeviceLifecycleCoordinator(
        GraphicsDeviceRestartExecutor executor,
        Func<ulong> getCurrentGeneration)
    {
        m_Executor = executor ?? throw new ArgumentNullException(nameof(executor));
        m_GetCurrentGeneration = getCurrentGeneration ??
            throw new ArgumentNullException(nameof(getCurrentGeneration));
    }

    public GraphicsDeviceLifecycleSnapshot Snapshot
    {
        get
        {
            lock (m_Gate) return m_Snapshot;
        }
    }

    public event Action<GraphicsDeviceLifecycleSnapshot>? StateChanged;

    public void RegisterParticipant(IGraphicsDeviceLifecycleParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentException.ThrowIfNullOrWhiteSpace(participant.ParticipantId);

        lock (m_Gate)
        {
            if (!m_Participants.TryAdd(participant.ParticipantId, participant))
            {
                throw new InvalidOperationException(
                    $"Graphics lifecycle participant '{participant.ParticipantId}' is already registered.");
            }
        }
    }

    public void UnregisterParticipant(string participantId)
    {
        if (string.IsNullOrWhiteSpace(participantId)) return;
        lock (m_Gate) m_Participants.Remove(participantId);
    }

    public async Task<GraphicsDeviceRestartResult> RestartAsync(
        RHIBackendRestartOptions options,
        CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined(options.DiagnosticMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options));
        }

        if (!await m_RestartGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            GraphicsDeviceLifecycleSnapshot active = Snapshot;
            return new GraphicsDeviceRestartResult(
                false,
                active.Generation,
                active.Generation,
                options.DiagnosticMode,
                "A graphics device restart is already active.");
        }

        ulong previousGeneration = 0;
        ulong currentGeneration = 0;
        try
        {
            GraphicsDeviceLifecycleSnapshot current = Snapshot;
            if (current.State == GraphicsDeviceLifecycleState.Failed)
            {
                return new GraphicsDeviceRestartResult(
                    false,
                    current.Generation,
                    current.Generation,
                    options.DiagnosticMode,
                    "The graphics lifecycle is in a fail-stop state.");
            }

            previousGeneration = m_GetCurrentGeneration();
            currentGeneration = previousGeneration;
            IGraphicsDeviceLifecycleParticipant[] participants = SnapshotParticipants();
            var prepareContext = new GraphicsDeviceRestartContext(
                options,
                previousGeneration,
                previousGeneration);

            Publish(new GraphicsDeviceLifecycleSnapshot(
                GraphicsDeviceLifecycleState.Quiescing,
                previousGeneration,
                options.DiagnosticMode,
                string.Empty));

            for (int index = 0; index < participants.Length; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await participants[index]
                    .PrepareForGraphicsDeviceRestartAsync(prepareContext, cancellationToken)
                    .ConfigureAwait(false);
            }

            Publish(new GraphicsDeviceLifecycleSnapshot(
                GraphicsDeviceLifecycleState.RecreatingBackend,
                previousGeneration,
                options.DiagnosticMode,
                string.Empty));

            cancellationToken.ThrowIfCancellationRequested();
            currentGeneration = await m_Executor(options, cancellationToken).ConfigureAwait(false);
            if (currentGeneration <= previousGeneration)
            {
                throw new InvalidOperationException(
                    $"Graphics backend restart did not advance its generation. Previous={previousGeneration}, Current={currentGeneration}.");
            }

            var restoreContext = new GraphicsDeviceRestartContext(
                options,
                previousGeneration,
                currentGeneration);
            Publish(new GraphicsDeviceLifecycleSnapshot(
                GraphicsDeviceLifecycleState.Restoring,
                currentGeneration,
                options.DiagnosticMode,
                string.Empty));

            for (int index = participants.Length - 1; index >= 0; index--)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await participants[index]
                    .RestoreAfterGraphicsDeviceRestartAsync(restoreContext, cancellationToken)
                    .ConfigureAwait(false);
            }

            Publish(new GraphicsDeviceLifecycleSnapshot(
                GraphicsDeviceLifecycleState.Running,
                currentGeneration,
                options.DiagnosticMode,
                string.Empty));
            return new GraphicsDeviceRestartResult(
                true,
                previousGeneration,
                currentGeneration,
                options.DiagnosticMode,
                string.Empty);
        }
        catch (Exception exception)
        {
            string diagnostic = exception is OperationCanceledException
                ? "Graphics device restart was cancelled during an ownership transition."
                : exception.Message;
            Publish(new GraphicsDeviceLifecycleSnapshot(
                GraphicsDeviceLifecycleState.Failed,
                currentGeneration,
                options.DiagnosticMode,
                diagnostic));
            return new GraphicsDeviceRestartResult(
                false,
                previousGeneration,
                currentGeneration,
                options.DiagnosticMode,
                diagnostic);
        }
        finally
        {
            m_RestartGate.Release();
        }
    }

    private IGraphicsDeviceLifecycleParticipant[] SnapshotParticipants()
    {
        lock (m_Gate)
        {
            return m_Participants.Values
                .OrderBy(participant => participant.Order)
                .ThenBy(participant => participant.ParticipantId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    private void Publish(GraphicsDeviceLifecycleSnapshot snapshot)
    {
        Action<GraphicsDeviceLifecycleSnapshot>? handlers;
        lock (m_Gate)
        {
            m_Snapshot = snapshot;
            handlers = StateChanged;
        }

        if (handlers == null) return;
        foreach (Action<GraphicsDeviceLifecycleSnapshot> handler in handlers.GetInvocationList())
        {
            try
            {
                handler(snapshot);
            }
            catch (Exception exception)
            {
                KernelLog.WarningFormat(
                    "[GraphicsDeviceLifecycle] State observer failed. State={0}, Generation={1}, Error={2}",
                    snapshot.State,
                    snapshot.Generation,
                    exception.Message);
            }
        }
    }
}
