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

    public static AssetDependencyStamp GetEnvironmentTextureStamp(
        IAssetDatabase assetDatabase,
        EnvironmentTextureAsset environmentTexture)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (environmentTexture == null)
        {
            throw new ArgumentNullException(nameof(environmentTexture));
        }

        long stamp = GetAssetStamp(assetDatabase, environmentTexture.Guid).Value;
        stamp = Combine(
            stamp,
            GetAssetStamp(assetDatabase, environmentTexture.SourceTexture.Guid).Value);
        return new AssetDependencyStamp(stamp);
    }

    public static DateTime GetEnvironmentTextureDependencyWriteTimeUtc(
        IAssetDatabase assetDatabase,
        EnvironmentTextureAsset environmentTexture)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (environmentTexture == null)
        {
            throw new ArgumentNullException(nameof(environmentTexture));
        }

        var newest = DateTime.MinValue;
        if (assetDatabase.TryGetAsset(environmentTexture.Guid, out var environmentSource))
        {
            newest = GetNewestWriteTimeUtc(newest, environmentSource);
        }

        if (assetDatabase.TryGetAsset(environmentTexture.SourceTexture.Guid, out var textureSource))
        {
            newest = GetNewestWriteTimeUtc(newest, textureSource);
        }

        return newest;
    }

    public static DateTime GetMaterialDependencyWriteTimeUtc(
        IAssetDatabase assetDatabase,
        MaterialAsset material)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (material == null)
        {
            throw new ArgumentNullException(nameof(material));
        }

        var newest = DateTime.MinValue;
        if (assetDatabase.TryGetAsset(material.Guid, out var materialSource))
        {
            newest = GetNewestWriteTimeUtc(newest, materialSource);
        }

        if (assetDatabase.TryGetAsset(material.Shader.Guid, out var shaderSource))
        {
            newest = GetNewestWriteTimeUtc(newest, shaderSource);
            if (material.Shader.Includes is { Count: > 0 })
            {
                string sourceDirectory = Path.GetDirectoryName(shaderSource.SourcePath) ?? Directory.GetCurrentDirectory();
                foreach (string include in material.Shader.Includes)
                {
                    if (string.IsNullOrWhiteSpace(include))
                    {
                        continue;
                    }

                    string includePath = Path.IsPathRooted(include)
                        ? include
                        : Path.Combine(sourceDirectory, include);
                    newest = GetNewestWriteTimeUtc(newest, includePath);
                }
            }
        }

        if (material.Texture2DRefs != null)
        {
            foreach (var textureRef in material.Texture2DRefs)
            {
                if (assetDatabase.TryGetAsset(textureRef.Texture.Guid, out var textureSource))
                {
                    newest = GetNewestWriteTimeUtc(newest, textureSource);
                }
            }
        }

        return newest;
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

        var file = new FileInfo(path);
        long stamp = Combine(0, file.LastWriteTimeUtc.Ticks);
        return Combine(stamp, file.Length);
    }

    private static long Combine(long left, long right)
    {
        if (right == 0)
        {
            return left;
        }

        const ulong offsetBasis = 14695981039346656037UL;
        const ulong prime = 1099511628211UL;
        ulong hash = left == 0 ? offsetBasis : unchecked((ulong)left);
        hash ^= unchecked((ulong)right);
        hash *= prime;
        return hash == 0 ? 1 : unchecked((long)hash);
    }

    private static DateTime GetNewestWriteTimeUtc(DateTime newest, AssetRecord asset)
    {
        newest = GetNewestWriteTimeUtc(newest, asset.SourcePath);
        return GetNewestWriteTimeUtc(newest, asset.MetaPath);
    }

    private static DateTime GetNewestWriteTimeUtc(DateTime newest, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return newest;
        }

        var writeTimeUtc = File.GetLastWriteTimeUtc(path);
        return writeTimeUtc > newest ? writeTimeUtc : newest;
    }
}
