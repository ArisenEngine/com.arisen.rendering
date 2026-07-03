namespace ArisenEngine.Rendering;

/// <summary>
/// A single CPU command-recording unit for a RenderGraph pass.
/// Draw ranges point into RenderFrameSnapshot.DrawList.
/// </summary>
public readonly struct RenderPassWorkItem
{
    public RenderPassWorkItem(int index, int drawStart, int drawCount)
    {
        Index = index;
        DrawStart = drawStart;
        DrawCount = drawCount;
    }

    public int Index { get; }
    public int DrawStart { get; }
    public int DrawCount { get; }
    public bool HasDrawRange => DrawStart >= 0 && DrawCount > 0;

    public static RenderPassWorkItem Pass(int index) => new(index, -1, 0);
    public static RenderPassWorkItem DrawRange(int index, int drawStart, int drawCount)
        => new(index, drawStart, drawCount);
}
