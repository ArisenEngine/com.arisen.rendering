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
    public RHIDevice Device { get; }
    public RHISwapChain SwapChain { get; }
    public uint FrameIndex { get; }
    public float DeltaTime { get; }
    public uint Width { get; }
    public uint Height { get; }

    public RenderContext(FrameArena arena, RHIDevice device, RHISwapChain swapChain, uint frameIndex, float deltaTime, uint width, uint height)
    {
        Arena = arena;
        Device = device;
        SwapChain = swapChain;
        FrameIndex = frameIndex;
        DeltaTime = deltaTime;
        Width = width;
        Height = height;
    }
}
