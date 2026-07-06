using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
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

    public string GetCookedVariant(string entryPoint)
    {
        var debugSuffix = DebugInfo ? ".debug" : string.Empty;
        return $"{Backend.ToString().ToLowerInvariant()}.{TargetEnvironment}.sm{ShaderModel}.o{OptimizationLevel}{debugSuffix}.{entryPoint}";
    }
}

public sealed record ShaderAsset(
    Guid Guid,
    string Name,
    IReadOnlyList<ShaderStageAsset> Stages,
    ShaderVariantKey Variant,
    IReadOnlyList<string>? Defines = null,
    IReadOnlyList<string>? Includes = null);

public readonly record struct CookedShaderStage(
    ShaderStageAsset Stage,
    string Variant,
    CookedAssetHandle Handle)
{
    public bool IsValid => Handle.IsValid;
}

public static class ShaderAssetCooker
{
    private const string ShaderSourceAssetType = "ShaderSource";

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
        if (!assetDatabase.TryGetAsset(shader.Guid, out var sourceAsset))
        {
            throw new InvalidOperationException($"[ShaderAssetCooker] Shader asset '{shader.Guid}' was not found.");
        }

        if (!string.Equals(sourceAsset.AssetType, ShaderSourceAssetType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[ShaderAssetCooker] Shader asset '{shader.Guid}' has asset type '{sourceAsset.AssetType}', expected '{ShaderSourceAssetType}'.");
        }

        var variant = shader.Variant.GetCookedVariant(stage.EntryPoint);
        var outputPath = assetDatabase.GetCookedArtifactPath(shader.Guid, variant, GetCookedExtension(shader.Variant.Backend));
        var newestSourceWriteTimeUtc = GetNewestSourceWriteTimeUtc(sourceAsset.SourcePath, shader.Includes);

        if (!File.Exists(outputPath) || File.GetLastWriteTimeUtc(outputPath) < newestSourceWriteTimeUtc)
        {
            CookStage(sourceAsset, shader, stage, variant, outputPath);
        }

        var outputInfo = new FileInfo(outputPath);
        if (!outputInfo.Exists || outputInfo.Length == 0)
        {
            throw new InvalidOperationException(
                $"[ShaderAssetCooker] Shader asset '{shader.Guid}' stage '{stage.Name}' produced no cooked bytecode.");
        }

        assetDatabase.RegisterCookedArtifact(new CookedAssetRecord(
            shader.Guid,
            sourceAsset.AssetType,
            variant,
            outputInfo.FullName,
            outputInfo.Length,
            outputInfo.LastWriteTimeUtc));

        if (!assetDatabase.TryLoadCookedAsset(shader.Guid, variant, ShaderSourceAssetType, out var handle))
        {
            throw new InvalidOperationException(
                $"[ShaderAssetCooker] Failed to load cooked shader asset '{shader.Guid}' stage '{stage.Name}'.");
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

        var result = ShaderCompiler.Compile(
            sourceAsset.SourcePath,
            stage.ProgramStage,
            new ShaderCompiler.CompileOptions
            {
                Entry = stage.EntryPoint,
                ShaderModel = shader.Variant.ShaderModel,
                Target = GetCompilerTarget(shader.Variant.Backend),
                TargetEnv = shader.Variant.TargetEnvironment,
                OptimizeLevel = shader.Variant.OptimizationLevel,
                Defines = shader.Defines ?? Array.Empty<string>(),
                Includes = shader.Includes ?? Array.Empty<string>(),
                OutputPath = outputPath
            });

        if (!result.Success || !File.Exists(outputPath))
        {
            throw new InvalidOperationException(
                $"[ShaderAssetCooker] Failed to cook shader asset '{shader.Guid}' stage '{stage.Name}' variant '{variant}'. {result.Message}");
        }

        Logger.Log(
            $"[ShaderAssetCooker] Cooked shader asset {shader.Guid} | Stage: {stage.Name} | Variant: {variant} | Output: {outputPath}");
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
