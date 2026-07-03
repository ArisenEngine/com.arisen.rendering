using Arisen.Native.RHI;
using ArisenEngine.Core.RHI;

namespace ArisenEngine.Rendering;

/// <summary>
/// Immutable per-frame data consumed by RenderGraph setup and command-recording workers.
/// The pointed-to data is copied into FrameArena by RenderSubsystem before rendering starts.
/// </summary>
public readonly unsafe struct RenderFrameSnapshot
{
    public RenderFrameSnapshot(
        RHIDevice device,
        RHISwapChain swapChain,
        RHIImageHandle targetImage,
        uint surfaceId,
        RenderOutputKind outputKind,
        uint frameIndex,
        float deltaTime,
        uint width,
        uint height,
        Camera* cameraPtr,
        int cameraCount,
        MeshDrawCommand* drawListPtr,
        int drawListCount)
    {
        Device = device;
        SwapChain = swapChain;
        TargetImage = targetImage;
        SurfaceId = surfaceId;
        OutputKind = outputKind;
        FrameIndex = frameIndex;
        DeltaTime = deltaTime;
        Width = width;
        Height = height;
        CameraPtr = cameraPtr;
        CameraCount = cameraCount;
        DrawListPtr = drawListPtr;
        DrawListCount = drawListCount;
    }

    public RHIDevice Device { get; }
    public RHISwapChain SwapChain { get; }
    public RHIImageHandle TargetImage { get; }
    public uint SurfaceId { get; }
    public RenderOutputKind OutputKind { get; }
    public uint FrameIndex { get; }
    public float DeltaTime { get; }
    public uint Width { get; }
    public uint Height { get; }
    public Camera* CameraPtr { get; }
    public int CameraCount { get; }
    public MeshDrawCommand* DrawListPtr { get; }
    public int DrawListCount { get; }

    public ReadOnlySpan<Camera> Cameras => new(CameraPtr, CameraCount);
    public ReadOnlySpan<MeshDrawCommand> DrawList => new(DrawListPtr, DrawListCount);
}
