using Arisen.Native.RHI;
using ArisenEngine.Core.RHI;
using ArisenEngine.Core.Memory;
using System.Numerics;

namespace ArisenEngine.Rendering;

/// <summary>
/// Provides contextual information and resources for a single frame's rendering.
/// </summary>
public struct RenderContext
{
    public FrameArena Arena { get; }
    public RHIDevice Device { get; }
    public RHISwapChain SwapChain { get; }
    public uint FrameIndex { get; }
    public float DeltaTime { get; }
    public uint Width { get; }
    public uint Height { get; }
    public uint SurfaceId { get; }
    
    // The list of meshes to be drawn this frame. 
    // We use a raw pointer to allow this struct to be captured by TaskGraph lambdas.
    public unsafe MeshDrawCommand* DrawListPtr;
    public int DrawListCount;

    public unsafe readonly ReadOnlySpan<MeshDrawCommand> DrawList => new(DrawListPtr, DrawListCount);

    public RenderContext(FrameArena arena, RHIDevice device, RHISwapChain swapChain, uint surfaceId, uint frameIndex, float deltaTime, uint width, uint height)
    {
        Arena = arena;
        Device = device;
        SwapChain = swapChain;
        SurfaceId = surfaceId;
        FrameIndex = frameIndex;
        DeltaTime = deltaTime;
        Width = width;
        Height = height;
    }
}
