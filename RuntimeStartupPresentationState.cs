using System;

namespace ArisenEngine.Rendering;

internal enum RuntimeStartupPresentationDecision
{
    None,
    WaitForWorldActivation,
    WaitForStreamedCells,
    WaitForWorldContent,
    Release
}

/// <summary>
/// Observation of the startup world's activation, of its streamed cells, and of the frame that
/// was just presented.
/// </summary>
internal readonly record struct RuntimeStartupPresentationObservation(
    long Revision,
    bool HasPendingWorld,
    bool HasActiveWorld,
    bool ActiveWorldDeclaresCells,
    int ActiveStreamedCells,
    bool FrameOwnsSceneContent,
    bool StreamingFailed);

/// <summary>
/// Holds the standalone runtime's first presentation until the startup world's own streamed
/// content owns the frame. A deferred startup world load means the render loop would otherwise
/// present the pipeline's empty-content placeholder (fallback clear colour and fallback mesh)
/// before any world content exists, and an activated world whose cells have not loaded yet
/// presents only the persistent scene, which is not the view the showcase is authored around.
/// The gate is armed once per process while that load is pending and releases on the first
/// presented frame that follows the activation boundary, owns an active streamed cell, and
/// carries scene content.
/// </summary>
internal struct RuntimeStartupPresentationState
{
    /// <summary>
    /// True while the runtime still owes the startup world its first displayed frame.
    /// </summary>
    public bool IsArmed { get; private set; }

    /// <summary>
    /// True once the gate released. A released gate never arms again, so a later world switch
    /// or reload can never hold presentation a second time.
    /// </summary>
    public bool HasCompleted { get; private set; }

    public long ArmedRevision { get; private set; }

    /// <summary>Number of presented frames withheld while the gate was armed.</summary>
    public int WaitedFrames { get; private set; }

    public RuntimeStartupPresentationDecision LastDecision { get; private set; }

    /// <summary>
    /// Arms the gate while the startup world load is staged but not activated. A runtime that
    /// already owns an active world, or has no pending world load, never arms: it has no
    /// placeholder frames to hide.
    /// </summary>
    public bool TryArm(in RuntimeStartupPresentationObservation observation)
    {
        if (IsArmed || HasCompleted || observation.HasActiveWorld || !observation.HasPendingWorld)
        {
            return false;
        }

        IsArmed = true;
        ArmedRevision = observation.Revision;
        WaitedFrames = 0;
        LastDecision = RuntimeStartupPresentationDecision.WaitForWorldActivation;
        return true;
    }

    /// <summary>
    /// Decides whether the frame that was just presented may be shown to the user. The gate
    /// releases exactly once per process.
    /// </summary>
    public RuntimeStartupPresentationDecision Evaluate(
        in RuntimeStartupPresentationObservation observation)
    {
        if (!IsArmed)
        {
            return RuntimeStartupPresentationDecision.None;
        }

        if (observation.StreamingFailed)
        {
            // A failed cell must not hold the window off screen; the runtime reports the
            // failure through its own diagnostics and the world keeps rendering what it has.
            Release();
            return RuntimeStartupPresentationDecision.Release;
        }

        if (!observation.HasActiveWorld)
        {
            // The load was withdrawn or reclaimed without activation. Showing the window beats
            // holding it hidden on a request that no longer exists.
            if (!observation.HasPendingWorld)
            {
                Release();
                return RuntimeStartupPresentationDecision.Release;
            }

            WaitedFrames++;
            LastDecision = RuntimeStartupPresentationDecision.WaitForWorldActivation;
            return LastDecision;
        }

        if (observation.ActiveWorldDeclaresCells && observation.ActiveStreamedCells <= 0)
        {
            // The world is active but none of its cells is, so the frame can only carry the
            // persistent scene. That scene is not the startup view: the world's own streamed
            // content is. Waiting here also keeps the window hidden while a runtime whose
            // streaming source sits outside every authored cell loads nothing at all.
            WaitedFrames++;
            LastDecision = RuntimeStartupPresentationDecision.WaitForStreamedCells;
            return LastDecision;
        }

        if (!observation.FrameOwnsSceneContent)
        {
            // A cell is active but its content has not reached the frame yet, so the pipeline is
            // still drawing its empty-content placeholder. Activation alone releases residency,
            // not draw content: an active cell whose terrain and instances are still uploading
            // renders the placeholder for as long as that takes.
            WaitedFrames++;
            LastDecision = RuntimeStartupPresentationDecision.WaitForWorldContent;
            return LastDecision;
        }

        Release();
        return RuntimeStartupPresentationDecision.Release;
    }

    private void Release()
    {
        IsArmed = false;
        HasCompleted = true;
        LastDecision = RuntimeStartupPresentationDecision.Release;
    }
}
