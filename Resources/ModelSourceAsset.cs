using System.Numerics;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Serialization;

namespace ArisenEngine.Rendering.Resources;

public enum ModelSourceFormat
{
    GltfJson,
    GltfBinary
}

public readonly record struct ModelRootTransform(
    Vector3 Position,
    Quaternion Rotation,
    Vector3 Scale)
{
    public static ModelRootTransform Identity { get; } = new(
        Vector3.Zero,
        Quaternion.Identity,
        Vector3.One);
}

public sealed record ModelImportSettings(
    string OutputRoot,
    int SceneIndex,
    float UnitScale,
    ModelRootTransform RootTransform,
    bool EmitTextures);

public sealed record ModelShaderReference(
    Guid Guid,
    string Name);

public sealed record ModelSourceDescriptor(
    Guid Guid,
    string Name,
    string SourcePath,
    string ResolvedSourcePath,
    ModelSourceFormat SourceFormat,
    ModelImportSettings Import,
    ModelShaderReference Shader);

public static class ModelSourceAssetLoader
{
    public const string ModelAssetType = "Model";

    public static ModelSourceDescriptor LoadSource(IAssetDatabase assetDatabase, Guid modelGuid)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (!assetDatabase.TryGetAsset(modelGuid, out var sourceAsset))
        {
            throw new InvalidOperationException($"[ModelSourceAssetLoader] Model asset '{modelGuid}' was not found.");
        }

        return LoadSource(sourceAsset);
    }

    public static ModelSourceDescriptor LoadSource(AssetRecord sourceAsset)
    {
        if (sourceAsset == null)
        {
            throw new ArgumentNullException(nameof(sourceAsset));
        }

        if (!string.Equals(sourceAsset.AssetType, ModelAssetType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[ModelSourceAssetLoader] Asset '{sourceAsset.Guid}' has asset type '{sourceAsset.AssetType}', expected '{ModelAssetType}'.");
        }

        var source = SerializationUtil.Deserialize<SerializedModelSourceAsset>(sourceAsset.SourcePath, serializeIfNotExist: false);
        source.Validate(sourceAsset.SourcePath);

        var resolvedSourcePath = ResolveSourcePath(sourceAsset.SourcePath, source.Source.Path);
        if (!File.Exists(resolvedSourcePath))
        {
            throw new FileNotFoundException(
                $"[ModelSourceAssetLoader] Model source '{sourceAsset.SourcePath}' references missing glTF source '{resolvedSourcePath}'.",
                resolvedSourcePath);
        }

        return new ModelSourceDescriptor(
            sourceAsset.Guid,
            string.IsNullOrWhiteSpace(source.Name)
                ? Path.GetFileNameWithoutExtension(sourceAsset.SourcePath)
                : source.Name,
            source.Source.Path,
            resolvedSourcePath,
            source.Source.ResolveFormat(sourceAsset.SourcePath, resolvedSourcePath),
            new ModelImportSettings(
                source.Import.OutputRoot,
                source.Import.SceneIndex,
                source.Import.UnitScale,
                source.Import.ResolveRootTransform(),
                source.Import.EmitTextures),
            new ModelShaderReference(source.Shader.Guid, source.Shader.Name));
    }

    public static GltfModelImportPlan CreateGltfPlan(IAssetDatabase assetDatabase, Guid modelGuid)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (!assetDatabase.TryGetAsset(modelGuid, out var sourceAsset))
        {
            throw new InvalidOperationException($"[ModelSourceAssetLoader] Model asset '{modelGuid}' was not found.");
        }

        var model = LoadSource(sourceAsset);
        return CreateGltfPlan(sourceAsset, model);
    }

    public static GltfModelImportPlan CreateGltfPlan(AssetRecord sourceAsset, ModelSourceDescriptor model)
    {
        if (sourceAsset == null)
        {
            throw new ArgumentNullException(nameof(sourceAsset));
        }

        if (model == null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        if (model.SourceFormat != ModelSourceFormat.GltfJson &&
            model.SourceFormat != ModelSourceFormat.GltfBinary)
        {
            throw new NotSupportedException(
                $"[ModelSourceAssetLoader] Model source format '{model.SourceFormat}' is not supported by the glTF planner.");
        }

        return GltfModelImportPlanner.CreatePlan(model.ResolvedSourcePath, sourceAsset.Guid, sourceAsset.PackageId);
    }

    public static GltfModelImportEmissionSettings CreateEmissionSettings(ModelSourceDescriptor model)
    {
        if (model == null)
        {
            throw new ArgumentNullException(nameof(model));
        }

        return new GltfModelImportEmissionSettings(
            model.Shader.Guid,
            model.Shader.Name,
            ResolveOutputName(model.Import.OutputRoot),
            model.Import.EmitTextures);
    }

    public static string ResolveOutputRoot(string modelSourcePath, string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            throw new ArgumentException("Model import output root cannot be empty.", nameof(outputRoot));
        }

        if (Path.IsPathRooted(outputRoot))
        {
            return Path.GetFullPath(outputRoot);
        }

        var assetsDirectory = FindContainingAssetsDirectory(modelSourcePath);
        if (StartsWithAssetsSegment(outputRoot) && assetsDirectory != null)
        {
            return Path.GetFullPath(Path.Combine(assetsDirectory.Parent!.FullName, outputRoot));
        }

        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(modelSourcePath)) ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(sourceDirectory, outputRoot));
    }

    private static string ResolveSourcePath(string modelSourcePath, string relativeOrAbsoluteSourcePath)
    {
        if (string.IsNullOrWhiteSpace(relativeOrAbsoluteSourcePath))
        {
            throw new ArgumentException("Model source path cannot be empty.", nameof(relativeOrAbsoluteSourcePath));
        }

        if (Path.IsPathRooted(relativeOrAbsoluteSourcePath))
        {
            return Path.GetFullPath(relativeOrAbsoluteSourcePath);
        }

        var sourceDirectory = Path.GetDirectoryName(Path.GetFullPath(modelSourcePath)) ?? Directory.GetCurrentDirectory();
        return Path.GetFullPath(Path.Combine(sourceDirectory, relativeOrAbsoluteSourcePath));
    }

    private static string ResolveOutputName(string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot))
        {
            return "Model";
        }

        var normalized = outputRoot.Replace('\\', '/').TrimEnd('/');
        var lastSlash = normalized.LastIndexOf('/');
        return lastSlash >= 0 ? normalized[(lastSlash + 1)..] : normalized;
    }

    private static bool StartsWithAssetsSegment(string path)
    {
        var normalized = path.Replace('\\', '/').TrimStart('/');
        return normalized.Equals("Assets", StringComparison.OrdinalIgnoreCase) ||
               normalized.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase);
    }

    private static DirectoryInfo? FindContainingAssetsDirectory(string path)
    {
        var directoryPath = File.Exists(path)
            ? Path.GetDirectoryName(Path.GetFullPath(path))
            : Path.GetDirectoryName(Path.GetFullPath(path));

        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return null;
        }

        var directory = new DirectoryInfo(directoryPath);
        while (directory != null)
        {
            if (string.Equals(directory.Name, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        return null;
    }

    private sealed class SerializedModelSourceAsset
    {
        public string Name { get; set; } = string.Empty;
        public SerializedModelSource Source { get; set; } = new();
        public SerializedModelImport Import { get; set; } = new();
        public SerializedModelShader Shader { get; set; } = new();

        public void Validate(string sourcePath)
        {
            Source.Validate(sourcePath);
            Import.Validate(sourcePath);
            Shader.Validate(sourcePath);
        }
    }

    private sealed class SerializedModelSource
    {
        public string Path { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;

        public void Validate(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(Path))
            {
                throw new InvalidOperationException($"[ModelSourceAssetLoader] Model '{sourcePath}' is missing Source.Path.");
            }
        }

        public ModelSourceFormat ResolveFormat(string modelSourcePath, string resolvedSourcePath)
        {
            if (!string.IsNullOrWhiteSpace(Format) &&
                Enum.TryParse<ModelSourceFormat>(Format, ignoreCase: true, out var parsed))
            {
                return parsed;
            }

            var extension = System.IO.Path.GetExtension(resolvedSourcePath);
            if (string.Equals(extension, ".gltf", StringComparison.OrdinalIgnoreCase))
            {
                return ModelSourceFormat.GltfJson;
            }

            if (string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase))
            {
                return ModelSourceFormat.GltfBinary;
            }

            throw new NotSupportedException(
                $"[ModelSourceAssetLoader] Model '{modelSourcePath}' references unsupported source format '{extension}'.");
        }
    }

    private sealed class SerializedModelImport
    {
        public string OutputRoot { get; set; } = string.Empty;
        public int SceneIndex { get; set; } = 0;
        public float UnitScale { get; set; } = 1.0f;
        public bool EmitTextures { get; set; } = true;
        public SerializedModelRootTransform? RootTransform { get; set; }

        public void Validate(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(OutputRoot))
            {
                throw new InvalidOperationException($"[ModelSourceAssetLoader] Model '{sourcePath}' is missing Import.OutputRoot.");
            }

            if (SceneIndex < 0)
            {
                throw new InvalidOperationException($"[ModelSourceAssetLoader] Model '{sourcePath}' Import.SceneIndex cannot be negative.");
            }

            if (!float.IsFinite(UnitScale) || UnitScale <= 0.0f)
            {
                throw new InvalidOperationException($"[ModelSourceAssetLoader] Model '{sourcePath}' Import.UnitScale must be greater than zero.");
            }
        }

        public ModelRootTransform ResolveRootTransform()
        {
            return RootTransform == null
                ? ModelRootTransform.Identity
                : new ModelRootTransform(
                    RootTransform.Position?.ToVector3() ?? Vector3.Zero,
                    RootTransform.Rotation?.ToQuaternion() ?? Quaternion.Identity,
                    RootTransform.Scale?.ToVector3() ?? Vector3.One);
        }
    }

    private sealed class SerializedModelShader
    {
        public Guid Guid { get; set; }
        public string Name { get; set; } = string.Empty;

        public void Validate(string sourcePath)
        {
            if (Guid == Guid.Empty)
            {
                throw new InvalidOperationException($"[ModelSourceAssetLoader] Model '{sourcePath}' is missing Shader.Guid.");
            }
        }
    }

    private sealed class SerializedModelRootTransform
    {
        public SerializedVector3? Position { get; set; }
        public SerializedQuaternion? Rotation { get; set; }
        public SerializedVector3? Scale { get; set; }
    }

    private sealed class SerializedVector3
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }

        public Vector3 ToVector3()
        {
            return new Vector3(X, Y, Z);
        }
    }

    private sealed class SerializedQuaternion
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
        public float W { get; set; } = 1.0f;

        public Quaternion ToQuaternion()
        {
            return new Quaternion(X, Y, Z, W);
        }
    }
}
