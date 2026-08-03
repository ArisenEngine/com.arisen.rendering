using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Rendering.Resources;
using ArisenEngine.ShaderLab;

namespace ArisenEngine.Rendering;

public enum ShaderAssetBackend
{
    Vulkan,
    DirectX12,
    Metal
}

public sealed record ShaderStageAsset(
    string Name,
    NativeRHI.EProgramStage ProgramStage,
    string EntryPoint);

public readonly record struct ShaderVariantKey(
    ShaderAssetBackend Backend,
    string TargetEnvironment,
    string ShaderModel,
    string OptimizationLevel,
    bool DebugInfo)
{
    public static ShaderVariantKey VulkanDebug { get; } = new(
        ShaderAssetBackend.Vulkan,
        "vulkan1.3",
        "6_4",
        "0",
        DebugInfo: true);

    public string GetCookedVariant(string entryPoint, IReadOnlyList<string>? keywords = null)
    {
        var debugSuffix = DebugInfo ? ".debug" : string.Empty;
        var keywordSuffix = GetKeywordVariantSuffix(keywords);
        return $"{Backend.ToString().ToLowerInvariant()}.{TargetEnvironment}.sm{ShaderModel}.o{OptimizationLevel}{debugSuffix}{keywordSuffix}.{entryPoint}";
    }

    public string GetVariantIdentity(IReadOnlyList<string>? keywords = null)
    {
        return $"{Backend}|{TargetEnvironment}|{ShaderModel}|{OptimizationLevel}|{DebugInfo}|{GetKeywordSetKey(keywords)}";
    }

    public static string GetKeywordSetKey(IReadOnlyList<string>? keywords)
    {
        var normalized = NormalizeKeywordSet(keywords);
        return normalized.Length == 0 ? string.Empty : string.Join("+", normalized);
    }

    public static string[] NormalizeKeywordSet(IReadOnlyList<string>? keywords)
    {
        if (keywords == null || keywords.Count == 0)
        {
            return Array.Empty<string>();
        }

        var values = new List<string>(keywords.Count);
        for (int i = 0; i < keywords.Count; i++)
        {
            var keyword = keywords[i]?.Trim();
            if (!string.IsNullOrWhiteSpace(keyword) &&
                !values.Contains(keyword, StringComparer.Ordinal))
            {
                values.Add(keyword);
            }
        }

        if (values.Count == 0)
        {
            return Array.Empty<string>();
        }

        values.Sort(StringComparer.Ordinal);
        return values.ToArray();
    }

    private static string GetKeywordVariantSuffix(IReadOnlyList<string>? keywords)
    {
        var normalized = NormalizeKeywordSet(keywords);
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var safeNames = new string[normalized.Length];
        for (int i = 0; i < normalized.Length; i++)
        {
            safeNames[i] = SanitizeKeywordForVariant(normalized[i]);
        }

        return ".kw-" + string.Join("-", safeNames);
    }

    private static string SanitizeKeywordForVariant(string keyword)
    {
        Span<char> buffer = keyword.Length <= 128
            ? stackalloc char[keyword.Length]
            : new char[keyword.Length];

        for (int i = 0; i < keyword.Length; i++)
        {
            var ch = keyword[i];
            buffer[i] = char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_';
        }

        return new string(buffer);
    }
}

public sealed record ShaderAsset(
    Guid Guid,
    string Name,
    IReadOnlyList<ShaderStageAsset> Stages,
    ShaderVariantKey Variant,
    IReadOnlyList<string>? Defines = null,
    IReadOnlyList<string>? Includes = null,
    IReadOnlyList<string>? VariantKeywords = null)
{
    public string GetVariantIdentity()
    {
        return Variant.GetVariantIdentity(VariantKeywords);
    }
}

public readonly record struct CookedShaderStage(
    ShaderStageAsset Stage,
    string Variant,
    CookedAssetHandle Handle)
{
    public bool IsValid => Handle.IsValid;
}

public static class ShaderAssetCooker
{
    public const string ShaderSourceAssetType = "ShaderSource";
    public const int CookedFormatVersion = 1;

    public static CookedShaderStage LoadOrCookStage(
        IAssetDatabase assetDatabase,
        ShaderAsset shader,
        string stageName)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (shader == null)
        {
            throw new ArgumentNullException(nameof(shader));
        }

        var stage = FindStage(shader, stageName);
        var variant = shader.Variant.GetCookedVariant(stage.EntryPoint, shader.VariantKeywords);
        if (!assetDatabase.CanReadSourceAssets)
        {
            return LoadCookedStage(assetDatabase, shader, stage, variant);
        }

        if (!assetDatabase.TryGetAsset(shader.Guid, out var sourceAsset))
        {
            throw new InvalidOperationException($"[ShaderAssetCooker] Shader asset '{shader.Guid}' was not found.");
        }

        if (!string.Equals(sourceAsset.AssetType, ShaderSourceAssetType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[ShaderAssetCooker] Shader asset '{shader.Guid}' has asset type '{sourceAsset.AssetType}', expected '{ShaderSourceAssetType}'.");
        }

        var newestSourceWriteTimeUtc = GetNewestSourceWriteTimeUtc(sourceAsset.SourcePath, shader.Includes);

        if (!assetDatabase.TryGetCookedArtifact(shader.Guid, variant, out CookedAssetRecord current) ||
            !File.Exists(current.Path) ||
            File.GetLastWriteTimeUtc(current.Path) < newestSourceWriteTimeUtc)
        {
            using CookedArtifactWrite write = assetDatabase.BeginCookedArtifactWrite(
                shader.Guid,
                variant,
                GetCookedExtension(shader.Variant.Backend));
            CookStage(sourceAsset, shader, stage, variant, write.OutputPath);

            var outputInfo = new FileInfo(write.OutputPath);
            if (!outputInfo.Exists || outputInfo.Length == 0)
            {
                throw new InvalidOperationException(
                    $"[ShaderAssetCooker] Shader asset '{shader.Guid}' stage '{stage.Name}' produced no cooked bytecode.");
            }

            write.Commit(sourceAsset.AssetType);
        }

        return LoadCookedStage(assetDatabase, shader, stage, variant);
    }

    private static CookedShaderStage LoadCookedStage(
        IAssetDatabase assetDatabase,
        ShaderAsset shader,
        ShaderStageAsset stage,
        string variant)
    {
        if (!assetDatabase.TryLoadCookedAsset(shader.Guid, variant, ShaderSourceAssetType, out var handle))
        {
            throw new InvalidOperationException(
                $"[ShaderAssetCooker] Cooked shader asset '{shader.Guid}' stage '{stage.Name}' " +
                $"variant '{variant}' is unavailable.");
        }

        return new CookedShaderStage(stage, variant, handle);
    }

    private static ShaderStageAsset FindStage(ShaderAsset shader, string stageName)
    {
        foreach (var stage in shader.Stages)
        {
            if (string.Equals(stage.Name, stageName, StringComparison.OrdinalIgnoreCase))
            {
                return stage;
            }
        }

        throw new InvalidOperationException(
            $"[ShaderAssetCooker] Shader asset '{shader.Guid}' does not define stage '{stageName}'.");
    }

    private static void CookStage(
        AssetRecord sourceAsset,
        ShaderAsset shader,
        ShaderStageAsset stage,
        string variant,
        string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var compileInputPath = sourceAsset.SourcePath;
        var includes = shader.Includes ?? Array.Empty<string>();
        if (ShaderLabSource.IsShaderLabPath(sourceAsset.SourcePath))
        {
            var shaderLab = ShaderLabSource.Load(sourceAsset.SourcePath);
            compileInputPath = shaderLab.WriteStageHlsl(stage, outputPath + ".hlsl");
            includes = MergeIncludes(includes, shaderLab.Includes);
        }

        var compilerDefines = BuildCompilerDefines(shader);
        var variantKeywords = ShaderVariantKey.NormalizeKeywordSet(shader.VariantKeywords);
        var result = ShaderCompiler.Compile(
            compileInputPath,
            stage.ProgramStage,
            new ShaderCompiler.CompileOptions
            {
                Entry = stage.EntryPoint,
                ShaderModel = shader.Variant.ShaderModel,
                Target = GetCompilerTarget(shader.Variant.Backend),
                TargetEnv = shader.Variant.TargetEnvironment,
                OptimizeLevel = shader.Variant.OptimizationLevel,
                Defines = compilerDefines,
                Includes = includes,
                OutputPath = outputPath
            });

        if (!result.Success || !File.Exists(outputPath))
        {
            throw new InvalidOperationException(
                $"[ShaderAssetCooker] Failed to cook shader asset '{shader.Guid}' stage '{stage.Name}' entry '{stage.EntryPoint}' backend '{shader.Variant.Backend}' target '{shader.Variant.TargetEnvironment}' variant '{variant}' keywords [{string.Join(", ", variantKeywords)}] defines [{string.Join(", ", compilerDefines)}]. {result.Message}");
        }

        Logger.Log(
            $"[ShaderAssetCooker] Cooked shader asset {shader.Guid} | Stage: {stage.Name} | Variant: {variant} | Keywords: [{string.Join(", ", variantKeywords)}] | Output: {outputPath}");
    }

    private static IReadOnlyList<string> BuildCompilerDefines(ShaderAsset shader)
    {
        var defines = shader.Defines ?? Array.Empty<string>();
        var keywords = ShaderVariantKey.NormalizeKeywordSet(shader.VariantKeywords);
        if (defines.Count == 0 && keywords.Length == 0)
        {
            return Array.Empty<string>();
        }

        var result = new List<string>(defines.Count + keywords.Length);
        AppendUnique(result, defines);
        AppendUnique(result, keywords);
        return result;
    }

    private static void AppendUnique(List<string> result, IReadOnlyList<string> values)
    {
        for (int i = 0; i < values.Count; i++)
        {
            var value = values[i];
            if (!string.IsNullOrWhiteSpace(value) &&
                !result.Contains(value, StringComparer.Ordinal))
            {
                result.Add(value);
            }
        }
    }

    private static IReadOnlyList<string> MergeIncludes(
        IReadOnlyList<string> first,
        IReadOnlyList<string> second)
    {
        if (first.Count == 0)
        {
            return second;
        }

        if (second.Count == 0)
        {
            return first;
        }

        var merged = new List<string>(first.Count + second.Count);
        AppendIncludes(merged, first);
        AppendIncludes(merged, second);
        return merged;
    }

    private static void AppendIncludes(List<string> merged, IReadOnlyList<string> includes)
    {
        for (int i = 0; i < includes.Count; i++)
        {
            var include = includes[i];
            if (!string.IsNullOrWhiteSpace(include) &&
                !merged.Contains(include, StringComparer.OrdinalIgnoreCase))
            {
                merged.Add(include);
            }
        }
    }

    private static DateTime GetNewestSourceWriteTimeUtc(string sourcePath, IReadOnlyList<string>? includes)
    {
        var newest = File.GetLastWriteTimeUtc(sourcePath);
        if (includes == null || includes.Count == 0)
        {
            return newest;
        }

        var sourceDirectory = Path.GetDirectoryName(sourcePath) ?? Directory.GetCurrentDirectory();
        foreach (var include in includes)
        {
            if (string.IsNullOrWhiteSpace(include))
            {
                continue;
            }

            var includePath = Path.IsPathRooted(include)
                ? include
                : Path.Combine(sourceDirectory, include);

            if (File.Exists(includePath))
            {
                var writeTime = File.GetLastWriteTimeUtc(includePath);
                if (writeTime > newest)
                {
                    newest = writeTime;
                }
            }
        }

        return newest;
    }

    private static string GetCompilerTarget(ShaderAssetBackend backend)
    {
        return backend switch
        {
            ShaderAssetBackend.Vulkan => "-spirv",
            _ => throw new NotSupportedException($"Shader backend '{backend}' is not implemented yet.")
        };
    }

    private static string GetCookedExtension(ShaderAssetBackend backend)
    {
        return backend switch
        {
            ShaderAssetBackend.Vulkan => ".spv",
            _ => ".shaderbin"
        };
    }
}
