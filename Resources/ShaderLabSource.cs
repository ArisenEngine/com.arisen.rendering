using Arisen.Native.RHI;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.ShaderLab;

namespace ArisenEngine.Rendering.Resources;

public sealed class ShaderLabSource
{
    private readonly string m_SourcePath;
    private readonly ShaderLabShader m_Shader;
    private readonly Pass m_Pass;

    private ShaderLabSource(string sourcePath, ShaderLabShader shader, Pass pass)
    {
        m_SourcePath = sourcePath;
        m_Shader = shader;
        m_Pass = pass;
    }

    public string Name => string.IsNullOrWhiteSpace(m_Shader.name)
        ? Path.GetFileNameWithoutExtension(m_SourcePath)
        : m_Shader.name;

    public IReadOnlyList<string> Includes => BuildIncludes(m_SourcePath, m_Pass);
    public IReadOnlyList<string> CompileTimeKeywords => BuildCompileTimeKeywords(m_Pass);
    public MaterialShaderContract MaterialContract => BuildMaterialContract(m_Shader, m_Pass, m_SourcePath);
    public MaterialRenderState RenderState => BuildRenderState(m_Pass, m_SourcePath);

    public static bool IsShaderLabPath(string sourcePath)
    {
        return string.Equals(Path.GetExtension(sourcePath), ".shader", StringComparison.OrdinalIgnoreCase);
    }

    public static ShaderLabSource Load(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("[ShaderLabSource] Source path is required.", nameof(sourcePath));
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("[ShaderLabSource] ShaderLab source was not found.", sourcePath);
        }

        var source = File.ReadAllText(sourcePath);
        var parser = new ShaderLabParser(source, Path.GetDirectoryName(sourcePath) ?? string.Empty);
        var shader = parser.ParseGraphicsShader();
        var pass = SelectFirstGraphicsPass(shader, sourcePath);
        return new ShaderLabSource(sourcePath, shader, pass);
    }

    public IReadOnlyList<ShaderStageAsset> BuildStages()
    {
        var stages = new List<ShaderStageAsset>(5);
        AddStage(stages, "Vertex", EProgramStage.Vertex, m_Pass.vertexEntry);
        AddStage(stages, "Fragment", EProgramStage.Fragment, m_Pass.fragmentEntry);
        AddStage(stages, "Geometry", EProgramStage.Geometry, m_Pass.geometryEntry);
        AddStage(stages, "Hull", EProgramStage.Hull, m_Pass.hullEntry);
        AddStage(stages, "Domain", EProgramStage.Domain, m_Pass.domainEntry);

        if (stages.Count == 0)
        {
            throw new InvalidOperationException(
                $"[ShaderLabSource] Shader '{m_SourcePath}' pass '{GetPassName(m_Pass)}' does not declare any graphics stages.");
        }

        return stages;
    }

    public string WriteStageHlsl(ShaderStageAsset stage, string outputPath)
    {
        if (stage == null)
        {
            throw new ArgumentNullException(nameof(stage));
        }

        if (string.IsNullOrWhiteSpace(m_Pass.hlslCode))
        {
            throw new InvalidOperationException(
                $"[ShaderLabSource] Shader '{m_SourcePath}' pass '{GetPassName(m_Pass)}' has no HLSLPROGRAM body.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        File.WriteAllText(outputPath, m_Pass.hlslCode);
        return outputPath;
    }

    private static Pass SelectFirstGraphicsPass(ShaderLabShader shader, string sourcePath)
    {
        for (int subShaderIndex = 0; subShaderIndex < shader.subShaders.Count; subShaderIndex++)
        {
            var subShader = shader.subShaders[subShaderIndex];
            for (int passIndex = 0; passIndex < subShader.passes.Count; passIndex++)
            {
                var pass = subShader.passes[passIndex];
                if (!string.IsNullOrWhiteSpace(pass.hlslCode))
                {
                    return pass;
                }
            }
        }

        throw new InvalidOperationException(
            $"[ShaderLabSource] Shader '{sourcePath}' does not contain a SubShader Pass with HLSLPROGRAM.");
    }

    private static void AddStage(
        List<ShaderStageAsset> stages,
        string name,
        EProgramStage programStage,
        string? entryPoint)
    {
        if (!string.IsNullOrWhiteSpace(entryPoint))
        {
            stages.Add(new ShaderStageAsset(name, programStage, entryPoint));
        }
    }

    private static IReadOnlyList<string> BuildCompileTimeKeywords(Pass pass)
    {
        var values = new List<string>(pass.multiCompile.Count + pass.shaderFeature.Count);
        AppendKeywordNames(values, pass.multiCompile);
        AppendKeywordNames(values, pass.shaderFeature);
        return values.Count == 0 ? Array.Empty<string>() : values;
    }

    private static void AppendKeywordNames(List<string> values, IReadOnlyList<string> keywords)
    {
        for (int i = 0; i < keywords.Count; i++)
        {
            var keyword = keywords[i];
            if (!string.IsNullOrWhiteSpace(keyword) && !values.Contains(keyword, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(keyword);
            }
        }
    }

    private static IReadOnlyList<string> BuildIncludes(string sourcePath, Pass pass)
    {
        var values = new List<string>();
        var sourceDirectory = Path.GetDirectoryName(sourcePath);
        if (!string.IsNullOrWhiteSpace(sourceDirectory))
        {
            values.Add(sourceDirectory);
        }

        for (int i = 0; i < pass.includedHLSLs.Count; i++)
        {
            var include = pass.includedHLSLs[i];
            if (!string.IsNullOrWhiteSpace(include) && !values.Contains(include, StringComparer.OrdinalIgnoreCase))
            {
                values.Add(include);
            }
        }

        return values.Count == 0 ? Array.Empty<string>() : values;
    }

    private static MaterialShaderContract BuildMaterialContract(ShaderLabShader shader, Pass pass, string sourcePath)
    {
        var annotations = ShaderMaterialContractAnnotations.Parse(pass.hlslCode, sourcePath);
        return new MaterialShaderContract(
            MergeNames(shader.materialContract.texture2DRefs, annotations.RequiredTexture2DRefs),
            MergeNames(shader.materialContract.scalarProperties, annotations.RequiredScalarProperties),
            MergeNames(shader.materialContract.vector4Properties, annotations.RequiredVector4Properties));
    }

    private static IReadOnlyList<string> MergeNames(IReadOnlyList<string> first, IReadOnlyList<string> second)
    {
        if (first.Count == 0 && second.Count == 0)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(first.Count + second.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AppendNames(first, result, seen);
        AppendNames(second, result, seen);
        return result;
    }

    private static void AppendNames(
        IReadOnlyList<string> names,
        List<string> result,
        HashSet<string> seen)
    {
        for (int i = 0; i < names.Count; i++)
        {
            var name = names[i];
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
            {
                result.Add(name);
            }
        }
    }

    private static MaterialRenderState BuildRenderState(Pass pass, string sourcePath)
    {
        var blend = pass.states.Blend;
        var blendEnabled = blend != null;
        var srcColor = blend?.SrcColor?.ToString() ?? "One";
        var dstColor = blend?.DstColor?.ToString() ?? "Zero";
        var blendOp = string.IsNullOrWhiteSpace(pass.states.BlendOp) ? "Add" : pass.states.BlendOp;

        return new MaterialRenderState(
            ParseCullMode(sourcePath, pass.states.Cull),
            EFrontFace.FRONT_FACE_COUNTER_CLOCKWISE,
            blendEnabled,
            ParseBlendFactor(sourcePath, srcColor, "SrcColor"),
            ParseBlendFactor(sourcePath, dstColor, "DstColor"),
            ParseBlendOp(sourcePath, blendOp));
    }

    private static ECullModeFlagBits ParseCullMode(string sourcePath, string? value)
    {
        return Normalize(value) switch
        {
            "" or "none" or "off" => ECullModeFlagBits.CULL_MODE_NONE,
            "front" => ECullModeFlagBits.CULL_MODE_FRONT_BIT,
            "back" => ECullModeFlagBits.CULL_MODE_BACK_BIT,
            "frontandback" or "frontback" => ECullModeFlagBits.CULL_MODE_FRONT_AND_BACK,
            _ => throw new InvalidOperationException(
                $"[ShaderLabSource] Shader '{sourcePath}' Cull value '{value}' is unsupported.")
        };
    }

    private static EBlendFactor ParseBlendFactor(string sourcePath, string? value, string fieldName)
    {
        return Normalize(value) switch
        {
            "" or "zero" => EBlendFactor.BLEND_FACTOR_ZERO,
            "one" => EBlendFactor.BLEND_FACTOR_ONE,
            "srccolor" or "sourcecolor" => EBlendFactor.BLEND_FACTOR_SRC_COLOR,
            "oneminussrccolor" or "oneminussourcecolor" => EBlendFactor.BLEND_FACTOR_ONE_MINUS_SRC_COLOR,
            "dstcolor" or "destinationcolor" => EBlendFactor.BLEND_FACTOR_DST_COLOR,
            "oneminusdstcolor" or "oneminusdestinationcolor" => EBlendFactor.BLEND_FACTOR_ONE_MINUS_DST_COLOR,
            "srcalpha" or "sourcealpha" => EBlendFactor.BLEND_FACTOR_SRC_ALPHA,
            "oneminussrcalpha" or "oneminussourcealpha" => EBlendFactor.BLEND_FACTOR_ONE_MINUS_SRC_ALPHA,
            "dstalpha" or "destinationalpha" => EBlendFactor.BLEND_FACTOR_DST_ALPHA,
            "oneminusdstalpha" or "oneminusdestinationalpha" => EBlendFactor.BLEND_FACTOR_ONE_MINUS_DST_ALPHA,
            _ => throw new InvalidOperationException(
                $"[ShaderLabSource] Shader '{sourcePath}' Blend.{fieldName} value '{value}' is unsupported.")
        };
    }

    private static EBlendOp ParseBlendOp(string sourcePath, string? value)
    {
        return Normalize(value) switch
        {
            "" or "add" => EBlendOp.BLEND_OP_ADD,
            "subtract" or "sub" => EBlendOp.BLEND_OP_SUBTRACT,
            "reversesubtract" or "revsub" => EBlendOp.BLEND_OP_REVERSE_SUBTRACT,
            "min" => EBlendOp.BLEND_OP_MIN,
            "max" => EBlendOp.BLEND_OP_MAX,
            _ => throw new InvalidOperationException(
                $"[ShaderLabSource] Shader '{sourcePath}' BlendOp value '{value}' is unsupported.")
        };
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim()
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
    }

    private static string GetPassName(Pass pass)
    {
        return string.IsNullOrWhiteSpace(pass.name) ? "<unnamed>" : pass.name;
    }
}
