using Arisen.Native.RHI;
using ArisenEngine.Core.RHI;
using ArisenEngine.Core.Memory;

namespace ArisenEngine.Rendering;

/// <summary>
/// Provides contextual information and resources for a single frame's rendering.
/// </summary>
public struct RenderContext
{
    public FrameArena Arena { get; }
    public RenderFrameSnapshot Snapshot { get; }
    internal RenderFrameSubmission Submission { get; }

    public RHIDevice Device => Snapshot.Device;
    public RHISwapChain SwapChain => Snapshot.SwapChain;
    public uint FrameIndex => Snapshot.FrameIndex;
    public float DeltaTime => Snapshot.DeltaTime;
    public uint Width => Snapshot.Width;
    public uint Height => Snapshot.Height;
    public uint SurfaceId => Snapshot.SurfaceId;
    public RenderOutputKind OutputKind => Snapshot.OutputKind;
    public RHIImageHandle TargetImage => Snapshot.TargetImage;
    public int CameraCount => Snapshot.CameraCount;
    public int DrawListCount => Snapshot.DrawListCount;
    public readonly ReadOnlySpan<Camera> Cameras => Snapshot.Cameras;
    public readonly ReadOnlySpan<MeshDrawCommand> DrawList => Snapshot.DrawList;

    internal RenderContext(FrameArena arena, RenderFrameSnapshot snapshot, RenderFrameSubmission submission)
    {
        Arena = arena;
        Snapshot = snapshot;
        Submission = submission;
    }
}
