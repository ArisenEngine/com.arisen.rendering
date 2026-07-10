using ArisenEngine.Core.Assets;
using ArisenEngine.Rendering;

namespace ArisenEngine.Rendering.Resources;

public readonly record struct AssetDependencyStamp(long Value)
{
    public static AssetDependencyStamp Empty { get; } = new(0);

    public bool IsValid => Value != 0;
}

public static class AssetDependencyTracker
{
    public static AssetDependencyStamp GetAssetStamp(IAssetDatabase assetDatabase, Guid assetGuid)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (!assetDatabase.TryGetAsset(assetGuid, out var asset))
        {
            return AssetDependencyStamp.Empty;
        }

        return GetAssetStamp(asset);
    }

    public static AssetDependencyStamp GetShaderStamp(IAssetDatabase assetDatabase, ShaderAsset shader)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (shader == null)
        {
            throw new ArgumentNullException(nameof(shader));
        }

        if (!assetDatabase.TryGetAsset(shader.Guid, out var shaderSource))
        {
            return AssetDependencyStamp.Empty;
        }

        long stamp = GetAssetStamp(shaderSource).Value;
        if (shader.Includes is { Count: > 0 })
        {
            string sourceDirectory = Path.GetDirectoryName(shaderSource.SourcePath) ?? Directory.GetCurrentDirectory();
            foreach (string include in shader.Includes)
            {
                if (string.IsNullOrWhiteSpace(include))
                {
                    continue;
                }

                string includePath = Path.IsPathRooted(include)
                    ? include
                    : Path.Combine(sourceDirectory, include);
                stamp = Combine(stamp, GetFileStamp(includePath));
            }
        }

        return new AssetDependencyStamp(stamp);
    }

    public static AssetDependencyStamp GetMaterialStamp(IAssetDatabase assetDatabase, MaterialAsset material)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (material == null)
        {
            throw new ArgumentNullException(nameof(material));
        }

        long stamp = GetAssetStamp(assetDatabase, material.Guid).Value;
        stamp = Combine(stamp, GetShaderStamp(assetDatabase, material.Shader).Value);

        if (material.Texture2DRefs != null)
        {
            foreach (var textureRef in material.Texture2DRefs)
            {
                stamp = Combine(stamp, GetAssetStamp(assetDatabase, textureRef.Texture.Guid).Value);
            }
        }

        return new AssetDependencyStamp(stamp);
    }

    private static AssetDependencyStamp GetAssetStamp(AssetRecord asset)
    {
        long stamp = GetFileStamp(asset.SourcePath);
        stamp = Combine(stamp, GetFileStamp(asset.MetaPath));
        return new AssetDependencyStamp(stamp);
    }

    private static long GetFileStamp(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return 0;
        }

        return File.GetLastWriteTimeUtc(path).Ticks;
    }

    private static long Combine(long left, long right)
    {
        return right > left ? right : left;
    }
}
