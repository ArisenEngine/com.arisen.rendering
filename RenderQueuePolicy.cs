using ArisenEngine.Rendering.Resources;

namespace ArisenEngine.Rendering;

public enum RenderQueueClass : byte
{
    Opaque = 0,
    AlphaTest = 1,
    Transparent = 2
}

public readonly record struct RenderQueueInfo(ushort Value, RenderQueueClass Class)
{
    public static RenderQueueInfo Opaque => new(RenderQueuePolicy.OpaqueQueue, RenderQueueClass.Opaque);
    public static RenderQueueInfo AlphaTest => new(RenderQueuePolicy.AlphaTestQueue, RenderQueueClass.AlphaTest);
    public static RenderQueueInfo Transparent => new(RenderQueuePolicy.TransparentQueue, RenderQueueClass.Transparent);
}

public static class RenderQueuePolicy
{
    public const ushort OpaqueQueue = 2000;
    public const ushort AlphaTestQueue = 2450;
    public const ushort TransparentQueue = 3000;
    public const string AlphaTestKeyword = "ALPHA_TEST";

    public static RenderQueueInfo Resolve(MaterialRenderState renderState, IReadOnlyList<string>? shaderKeywords)
    {
        if (renderState.BlendEnabled)
        {
            return RenderQueueInfo.Transparent;
        }

        return HasKeyword(shaderKeywords, AlphaTestKeyword)
            ? RenderQueueInfo.AlphaTest
            : RenderQueueInfo.Opaque;
    }

    private static bool HasKeyword(IReadOnlyList<string>? shaderKeywords, string keyword)
    {
        if (shaderKeywords == null || shaderKeywords.Count == 0)
        {
            return false;
        }

        for (int i = 0; i < shaderKeywords.Count; i++)
        {
            if (string.Equals(shaderKeywords[i], keyword, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
