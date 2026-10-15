namespace ArisenEngine.Rendering;

/// <summary>
/// Skin palettes are a mesh property rather than a material keyword, so a draw that uses a cooked
/// skeleton compiles the material's shader once more with the SKINNED keyword. The shader must
/// declare that keyword (<c>#pragma shader_feature SKINNED</c>) and the variant has to be cooked
/// ahead of time for cooked-only runs; the vertex layout also changes, which is why the pass keeps a
/// separate pipeline for it.
/// </summary>
public static class MeshSkinningShaderVariant
{
    public const string Keyword = "SKINNED";

    public static ShaderAsset Create(ShaderAsset shader)
    {
        ArgumentNullException.ThrowIfNull(shader);
        var keywords = new List<string>();
        if (shader.VariantKeywords != null)
        {
            keywords.AddRange(shader.VariantKeywords);
        }

        keywords.Add(Keyword);
        return shader with { VariantKeywords = ShaderVariantKey.NormalizeKeywordSet(keywords) };
    }

    public static bool IsSkinned(ShaderAsset shader)
    {
        ArgumentNullException.ThrowIfNull(shader);
        if (shader.VariantKeywords == null)
        {
            return false;
        }

        foreach (var keyword in shader.VariantKeywords)
        {
            if (string.Equals(keyword, Keyword, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
