using ArisenEngine.Core.Assets;

namespace ArisenEngine.Rendering.Resources;

public sealed class RenderingRuntimeAssetCooker : IRuntimeAssetCooker
{
    private const string MeshAssetType = "Mesh";
    private const string MaterialAssetType = "Material";
    private const string TextureAssetType = "Texture2D";
    private const string EnvironmentTextureAssetType = "EnvironmentTexture";

    private readonly IAssetDatabase m_AssetDatabase;
    private readonly RuntimeShaderCookRecipeRegistry m_ShaderRecipes;

    public RenderingRuntimeAssetCooker(
        IAssetDatabase assetDatabase,
        RuntimeShaderCookRecipeRegistry shaderRecipes)
    {
        m_AssetDatabase = assetDatabase ?? throw new ArgumentNullException(nameof(assetDatabase));
        m_ShaderRecipes = shaderRecipes ?? throw new ArgumentNullException(nameof(shaderRecipes));
    }

    public string ProviderId => "com.arisen.rendering.runtime-asset-cooker";

    public IReadOnlyCollection<string> AssetTypes { get; } =
    [
        MeshAssetType,
        MaterialAssetType,
        TextureAssetType,
        ShaderAssetCooker.ShaderSourceAssetType,
        EnvironmentTextureAssetType
    ];

    public RuntimeAssetCookerOutput Cook(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request)
    {
        ArgumentNullException.ThrowIfNull(context);
        AssetRecord sourceAsset = GetSourceAsset(request);
        return request.AssetType switch
        {
            MeshAssetType => CookMesh(context, request, sourceAsset),
            MaterialAssetType => CookMaterial(context, request, sourceAsset),
            TextureAssetType => CookTexture(context, request, sourceAsset),
            ShaderAssetCooker.ShaderSourceAssetType => CookShader(context, request),
            EnvironmentTextureAssetType => CookEnvironment(context, request),
            _ => throw new InvalidOperationException(
                $"[RenderingRuntimeAssetCooker] Unsupported asset type '{request.AssetType}'.")
        };
    }

    private RuntimeAssetCookerOutput CookMesh(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request,
        AssetRecord sourceAsset)
    {
        MeshVariantKey variantKey = MeshVariantKey.Default;
        string variant = variantKey.GetCookedVariant();
        ValidateVariant(request, variant);
        InvalidateIfForced(context, request.Guid, variant);
        var mesh = new MeshAsset(
            request.Guid,
            Path.GetFileNameWithoutExtension(sourceAsset.SourcePath),
            variantKey,
            ResolveMeshSourceFormat(sourceAsset.SourcePath));
        CookedMesh cooked = MeshAssetCooker.LoadOrCook(m_AssetDatabase, mesh);
        try
        {
            return CreateOutput(
                request,
                cooked.Variant,
                ".mesh",
                MeshAssetCooker.CookedFormatVersion);
        }
        finally
        {
            m_AssetDatabase.Release(cooked.Handle);
        }
    }

    private RuntimeAssetCookerOutput CookMaterial(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request,
        AssetRecord sourceAsset)
    {
        ValidateVariant(request, MaterialAssetCooker.RuntimeVariant);
        MaterialAsset material = MaterialAssetLoader.LoadSource(m_AssetDatabase, request.Guid);
        var dependencies = new HashSet<RuntimeAssetCookDependencyRequest>();
        AssetRecord shaderSource = GetDependencySource(
            material.Shader.Guid,
            ShaderAssetCooker.ShaderSourceAssetType,
            $"material '{request.Guid:D}' shader");
        foreach (ShaderStageAsset stage in material.Shader.Stages)
        {
            m_ShaderRecipes.RegisterRecipe(
                material.Shader,
                stage.Name,
                $"material:{request.Guid:D}");
            dependencies.Add(new RuntimeAssetCookDependencyRequest(
                material.Shader.Guid,
                shaderSource.PackageId,
                ShaderAssetCooker.ShaderSourceAssetType,
                material.Shader.Variant.GetCookedVariant(
                    stage.EntryPoint,
                    material.Shader.VariantKeywords),
                Required: true));
        }

        foreach (MaterialTexture2DRef textureRef in material.Texture2DRefs)
        {
            AssetRecord textureSource = GetDependencySource(
                textureRef.Texture.Guid,
                TextureAssetType,
                $"material '{request.Guid:D}' texture '{textureRef.Name}'");
            dependencies.Add(new RuntimeAssetCookDependencyRequest(
                textureRef.Texture.Guid,
                textureSource.PackageId,
                TextureAssetType,
                textureRef.Texture.Variant.GetCookedVariant(),
                Required: true));
        }

        InvalidateIfForced(context, request.Guid, MaterialAssetCooker.RuntimeVariant);
        CookedMaterial cooked = MaterialAssetCooker.LoadOrCook(m_AssetDatabase, request.Guid);
        try
        {
            return CreateOutput(
                request,
                cooked.Variant,
                ".material",
                MaterialAssetCooker.CookedFormatVersion,
                dependencies);
        }
        finally
        {
            m_AssetDatabase.Release(cooked.Handle);
        }
    }

    private RuntimeAssetCookerOutput CookTexture(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request,
        AssetRecord sourceAsset)
    {
        Texture2DVariantKey variantKey = request.Variant.Length == 0
            ? Texture2DVariantKey.DefaultSRgb
            : ParseTextureVariant(request.Variant);
        string variant = variantKey.GetCookedVariant();
        InvalidateIfForced(context, request.Guid, variant);
        var texture = new Texture2DAsset(
            request.Guid,
            Path.GetFileNameWithoutExtension(sourceAsset.SourcePath),
            variantKey,
            ResolveTextureSourceFormat(sourceAsset.SourcePath));
        CookedTexture2D cooked = Texture2DAssetCooker.LoadOrCook(m_AssetDatabase, texture);
        try
        {
            return CreateOutput(
                request,
                cooked.Variant,
                ".texture2d",
                Texture2DAssetCooker.CookedFormatVersion);
        }
        finally
        {
            m_AssetDatabase.Release(cooked.Handle);
        }
    }

    private RuntimeAssetCookerOutput CookShader(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request)
    {
        if (request.Variant.Length == 0)
        {
            throw new InvalidOperationException(
                "[RenderingRuntimeAssetCooker] Shader cooking requires an explicit stage variant.");
        }

        if (!m_ShaderRecipes.TryGetRecipe(request.Guid, request.Variant, out RuntimeShaderCookRecipe? recipe))
        {
            throw new InvalidOperationException(
                $"[RenderingRuntimeAssetCooker] No shader recipe is registered for " +
                $"'{request.Guid:D}:{request.Variant}'.");
        }

        InvalidateIfForced(context, request.Guid, request.Variant);
        CookedShaderStage cooked = ShaderAssetCooker.LoadOrCookStage(
            m_AssetDatabase,
            recipe.Shader,
            recipe.StageName);
        try
        {
            CookedAssetRecord artifact = GetCookedArtifact(request.Guid, cooked.Variant);
            return CreateOutput(
                request,
                cooked.Variant,
                Path.GetExtension(artifact.Path),
                ShaderAssetCooker.CookedFormatVersion);
        }
        finally
        {
            m_AssetDatabase.Release(cooked.Handle);
        }
    }

    private RuntimeAssetCookerOutput CookEnvironment(
        RuntimeAssetCookContext context,
        RuntimeAssetCookRequest request)
    {
        EnvironmentTextureAsset asset = EnvironmentTextureAssetLoader.LoadSource(
            m_AssetDatabase,
            request.Guid);
        if (string.Equals(
                request.Variant,
                EnvironmentLightingAssetCooker.CookedVariant,
                StringComparison.Ordinal))
        {
            InvalidateIfForced(context, request.Guid, request.Variant);
            CookedEnvironmentLighting lighting =
                EnvironmentLightingAssetCooker.LoadOrCook(m_AssetDatabase, asset);
            try
            {
                return CreateOutput(
                    request,
                    lighting.Variant,
                    ".environmentlighting",
                    EnvironmentLightingAssetCooker.CookedFormatVersion);
            }
            finally
            {
                m_AssetDatabase.Release(lighting.Handle);
            }
        }

        string variant = asset.Variant.GetCookedVariant();
        ValidateVariant(request, variant);
        InvalidateIfForced(context, request.Guid, variant);
        CookedEnvironmentTexture cooked =
            EnvironmentTextureAssetCooker.LoadOrCook(m_AssetDatabase, asset);
        try
        {
            return CreateOutput(
                request,
                cooked.Variant,
                ".environment",
                EnvironmentTextureAssetCooker.CookedFormatVersion,
                [
                    new RuntimeAssetCookDependencyRequest(
                        request.Guid,
                        request.PackageId,
                        EnvironmentTextureAssetType,
                        EnvironmentLightingAssetCooker.CookedVariant,
                        Required: true)
                ]);
        }
        finally
        {
            m_AssetDatabase.Release(cooked.Handle);
        }
    }

    private RuntimeAssetCookerOutput CreateOutput(
        RuntimeAssetCookRequest request,
        string variant,
        string extension,
        int formatVersion,
        IEnumerable<RuntimeAssetCookDependencyRequest>? dependencies = null)
    {
        CookedAssetRecord artifact = GetCookedArtifact(request.Guid, variant);
        return RuntimeAssetCookerOutput.FromFile(
            request,
            variant,
            BuildOutputRelativePath(request.PackageId, request.Guid, variant, extension),
            artifact.Path,
            formatVersion,
            dependencies);
    }

    private AssetRecord GetSourceAsset(RuntimeAssetCookRequest request)
    {
        if (!m_AssetDatabase.TryGetAsset(request.Guid, out AssetRecord? sourceAsset))
        {
            throw new InvalidOperationException(
                $"[RenderingRuntimeAssetCooker] Asset '{request.Guid:D}' is not indexed.");
        }

        if (!string.Equals(sourceAsset.AssetType, request.AssetType, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(sourceAsset.PackageId, request.PackageId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[RenderingRuntimeAssetCooker] Asset '{request.Guid:D}' is indexed as " +
                $"'{sourceAsset.PackageId}:{sourceAsset.AssetType}', not " +
                $"'{request.PackageId}:{request.AssetType}'.");
        }

        return sourceAsset;
    }

    private AssetRecord GetDependencySource(Guid guid, string assetType, string context)
    {
        if (!m_AssetDatabase.TryGetAsset(guid, out AssetRecord? sourceAsset) ||
            !string.Equals(sourceAsset.AssetType, assetType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[RenderingRuntimeAssetCooker] The {context} references missing " +
                $"{assetType} asset '{guid:D}'.");
        }

        return sourceAsset;
    }

    private CookedAssetRecord GetCookedArtifact(Guid guid, string variant)
    {
        if (!m_AssetDatabase.TryGetCookedArtifact(guid, variant, out CookedAssetRecord? artifact) ||
            !File.Exists(artifact.Path))
        {
            throw new InvalidOperationException(
                $"[RenderingRuntimeAssetCooker] Cooked artifact '{guid:D}:{variant}' was not registered.");
        }

        return artifact;
    }

    private void InvalidateIfForced(
        RuntimeAssetCookContext context,
        Guid guid,
        string variant)
    {
        if (context.ForceRebuild)
        {
            m_AssetDatabase.InvalidateCookedAssets(guid, variant);
        }
    }

    private static void ValidateVariant(RuntimeAssetCookRequest request, string supportedVariant)
    {
        if (request.Variant.Length > 0 &&
            !string.Equals(request.Variant, supportedVariant, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"[RenderingRuntimeAssetCooker] Variant '{request.Variant}' is unsupported for " +
                $"'{request.AssetType}' asset '{request.Guid:D}'; expected '{supportedVariant}'.");
        }
    }

    private static MeshSourceFormat ResolveMeshSourceFormat(string sourcePath)
    {
        return Path.GetExtension(sourcePath).ToLowerInvariant() switch
        {
            ".armesh" => MeshSourceFormat.ArisenTextMesh,
            ".obj" => MeshSourceFormat.WavefrontObj,
            ".gltf" => MeshSourceFormat.GltfJson,
            ".glb" => MeshSourceFormat.GltfBinary,
            string extension => throw new NotSupportedException(
                $"[RenderingRuntimeAssetCooker] Mesh extension '{extension}' is unsupported.")
        };
    }

    private static Texture2DSourceFormat ResolveTextureSourceFormat(string sourcePath)
    {
        return Path.GetExtension(sourcePath).ToLowerInvariant() switch
        {
            ".ppm" => Texture2DSourceFormat.PpmP3,
            ".png" or ".jpg" or ".jpeg" => Texture2DSourceFormat.ImageFile,
            string extension => throw new NotSupportedException(
                $"[RenderingRuntimeAssetCooker] Texture extension '{extension}' is unsupported.")
        };
    }

    private static Texture2DVariantKey ParseTextureVariant(string variant)
    {
        string[] parts = variant.Split('.', StringSplitOptions.None);
        if (parts.Length is < 3 or > 4 ||
            !Enum.TryParse(parts[0], ignoreCase: true, out Texture2DCookedFormat format) ||
            !Enum.IsDefined(format) ||
            !Enum.TryParse(parts[1], ignoreCase: true, out Texture2DColorSpace colorSpace) ||
            !Enum.IsDefined(colorSpace) ||
            parts[2] is not ("mips" or "nomips") ||
            (parts.Length == 4 && parts[3] != "normalmap"))
        {
            throw new InvalidOperationException(
                $"[RenderingRuntimeAssetCooker] Texture variant '{variant}' is invalid.");
        }

        var mipFilter = parts.Length == 4
            ? Texture2DMipFilter.NormalMap
            : Texture2DMipFilter.Color;
        var key = new Texture2DVariantKey(
            format,
            colorSpace,
            parts[2] == "mips",
            mipFilter);
        if (mipFilter == Texture2DMipFilter.NormalMap &&
            colorSpace != Texture2DColorSpace.Linear)
        {
            throw new InvalidOperationException(
                $"[RenderingRuntimeAssetCooker] Texture variant '{variant}' is invalid.");
        }

        return key;
    }

    private static string BuildOutputRelativePath(
        string packageId,
        Guid guid,
        string variant,
        string extension)
    {
        return $"{packageId}/{guid:N}/{variant}{extension}";
    }
}
