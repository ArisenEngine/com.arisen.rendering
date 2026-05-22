namespace ArisenEngine.Rendering;

/// <summary>
/// Describes how the rendered frame will be consumed after the RenderGraph finishes.
/// User render pipelines should not branch on backend-specific surface ids; output policy
/// is centralized in engine-owned setup/finalization passes.
/// </summary>
public enum RenderOutputKind
{
    NativeSwapchain,
    EditorSharedTexture,
    Offscreen
}
