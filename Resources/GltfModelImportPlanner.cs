using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;

namespace ArisenEngine.Rendering.Resources;

public sealed record GltfModelImportPlan(
    Guid SourceGuid,
    string PackageId,
    IReadOnlyList<GltfGeneratedChildAsset> GeneratedChildren,
    IReadOnlyList<GltfImportedMaterial> Materials,
    IReadOnlyList<GltfImportedImageSource> Images,
    IReadOnlyList<string> Warnings);

public sealed record GltfGeneratedChildAsset(
    string Kind,
    string Key,
    AssetMetadata Metadata);

public enum GltfMaterialAlphaMode
{
    Opaque,
    Mask,
    Blend,
    Unsupported
}

public sealed record GltfImportedMaterial(
    Guid Guid,
    string Name,
    Vector4 BaseColorFactor,
    Vector4 EmissiveFactor,
    float MetallicFactor,
    float RoughnessFactor,
    GltfImportedTextureRef? BaseColorTexture,
    GltfImportedTextureRef? NormalTexture,
    GltfImportedTextureRef? EmissiveTexture,
    GltfImportedTextureRef? MetallicRoughnessTexture,
    GltfImportedTextureRef? OcclusionTexture,
    float OcclusionStrength,
    GltfMaterialAlphaMode AlphaMode,
    float AlphaCutoff);

public sealed record GltfImportedTextureRef(
    int TextureIndex,
    int ImageIndex,
    string? Uri,
    int BufferView,
    string? MimeType,
    MaterialTextureSamplerSettings Sampler,
    bool GenerateMipMaps,
    MaterialTextureTransform Transform);

public sealed record GltfImportedImageSource(
    int ImageIndex,
    string? Uri,
    int BufferView,
    string? MimeType);

public static class GltfModelImportPlanner
{
    private const uint GltfBinaryMagic = 0x46546C67;
    private const uint GltfBinaryJsonChunkType = 0x4E4F534A;
    private const uint GltfBinaryBinChunkType = 0x004E4942;
    private const int GltfBinaryHeaderSize = 12;
    private const int GltfBinaryChunkHeaderSize = 8;

    public static GltfModelImportPlan CreatePlan(string sourcePath, Guid sourceGuid, string packageId)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Model import planning requires a source path.", nameof(sourcePath));
        }

        if (sourceGuid == Guid.Empty)
        {
            throw new ArgumentException("Model import planning requires a stable source GUID.", nameof(sourceGuid));
        }

        using var _ = Profiler.Zone("GltfModelImportPlanner.CreatePlan");
        var normalizedPackageId = NormalizePackageId(packageId);
        using var document = LoadGltfDocument(sourcePath);
        var root = document.RootElement;
        if (!root.TryGetProperty("asset", out var asset) ||
            !asset.TryGetProperty("version", out var version) ||
            !version.GetString()!.StartsWith("2.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"[GltfModelImportPlanner] glTF source '{sourcePath}' must be glTF 2.x.");
        }

        var children = new List<GltfGeneratedChildAsset>();
        var warnings = new List<string>();
        AddSceneChildren(root, sourceGuid, normalizedPackageId, children);
        AddMeshChildren(root, sourceGuid, normalizedPackageId, children);
        var images = ReadImageSources(sourcePath, root);
        var materials = ReadMaterials(sourcePath, root, sourceGuid, normalizedPackageId, children, warnings);
        AddTextureChildren(sourceGuid, normalizedPackageId, materials, children);
        AddUnsupportedFeatureWarnings(root, warnings);

        var plan = new GltfModelImportPlan(
            sourceGuid,
            normalizedPackageId,
            children,
            materials,
            images,
            warnings);
        Profiler.PlotValue("ModelImport.PlannedChildCount", plan.GeneratedChildren.Count);
        Profiler.PlotValue("ModelImport.PlannedMaterialCount", plan.Materials.Count);
        Profiler.PlotValue("ModelImport.PlannedImageCount", plan.Images.Count);
        Profiler.PlotValue("ModelImport.PlanningWarningCount", plan.Warnings.Count);
        return plan;
    }

    private static JsonDocument LoadGltfDocument(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        if (string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase))
        {
            return LoadGltfBinaryDocument(sourcePath);
        }

        return JsonDocument.Parse(File.ReadAllText(sourcePath));
    }

    private static JsonDocument LoadGltfBinaryDocument(string sourcePath)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        if (bytes.Length < GltfBinaryHeaderSize)
        {
            throw new InvalidOperationException($"[GltfModelImportPlanner] GLB source '{sourcePath}' is smaller than the GLB header.");
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, sizeof(uint)));
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, sizeof(uint)));
        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, sizeof(uint)));
        if (magic != GltfBinaryMagic)
        {
            throw new InvalidOperationException($"[GltfModelImportPlanner] GLB source '{sourcePath}' has invalid magic.");
        }

        if (version != 2)
        {
            throw new NotSupportedException($"[GltfModelImportPlanner] GLB source '{sourcePath}' uses version '{version}', expected 2.");
        }

        if (declaredLength > bytes.Length)
        {
            throw new InvalidOperationException($"[GltfModelImportPlanner] GLB source '{sourcePath}' declares length '{declaredLength}' but file has only '{bytes.Length}' bytes.");
        }

        ReadOnlyMemory<byte> jsonChunk = ReadOnlyMemory<byte>.Empty;
        var offset = GltfBinaryHeaderSize;
        while (offset < declaredLength)
        {
            if (offset + GltfBinaryChunkHeaderSize > declaredLength)
            {
                throw new InvalidOperationException($"[GltfModelImportPlanner] GLB source '{sourcePath}' has a truncated chunk header.");
            }

            var rawChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)));
            if (rawChunkLength > int.MaxValue)
            {
                throw new InvalidOperationException($"[GltfModelImportPlanner] GLB source '{sourcePath}' has a chunk larger than the supported importer range.");
            }

            var chunkLength = (int)rawChunkLength;
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + sizeof(uint), sizeof(uint)));
            offset += GltfBinaryChunkHeaderSize;
            if (offset + chunkLength > declaredLength)
            {
                throw new InvalidOperationException($"[GltfModelImportPlanner] GLB source '{sourcePath}' has a truncated chunk payload.");
            }

            if (chunkType == GltfBinaryJsonChunkType)
            {
                if (!jsonChunk.IsEmpty)
                {
                    throw new InvalidOperationException($"[GltfModelImportPlanner] GLB source '{sourcePath}' contains multiple JSON chunks.");
                }

                jsonChunk = bytes.AsMemory(offset, chunkLength);
            }
            offset += chunkLength;
        }

        if (jsonChunk.IsEmpty)
        {
            throw new InvalidOperationException($"[GltfModelImportPlanner] GLB source '{sourcePath}' contains no JSON chunk.");
        }

        return JsonDocument.Parse(jsonChunk);

    }

    private static void AddSceneChildren(JsonElement root, Guid sourceGuid, string packageId, List<GltfGeneratedChildAsset> children)
    {
        if (!root.TryGetProperty("scenes", out var scenes) || scenes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        for (int i = 0; i < scenes.GetArrayLength(); i++)
        {
            children.Add(CreateChild(sourceGuid, packageId, "scene", $"scenes/{i}", "Scene", "GltfSceneImporter"));
        }
    }

    private static void AddMeshChildren(JsonElement root, Guid sourceGuid, string packageId, List<GltfGeneratedChildAsset> children)
    {
        if (!root.TryGetProperty("meshes", out var meshes) || meshes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        for (int i = 0; i < meshes.GetArrayLength(); i++)
        {
            children.Add(CreateChild(sourceGuid, packageId, "mesh", $"meshes/{i}", "Mesh", "GltfMeshImporter"));
        }
    }

    private static IReadOnlyList<GltfImportedMaterial> ReadMaterials(
        string sourcePath,
        JsonElement root,
        Guid sourceGuid,
        string packageId,
        List<GltfGeneratedChildAsset> children,
        List<string> warnings)
    {
        if (!root.TryGetProperty("materials", out var materialsElement) ||
            materialsElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<GltfImportedMaterial>();
        }

        var materials = new GltfImportedMaterial[materialsElement.GetArrayLength()];
        for (int i = 0; i < materials.Length; i++)
        {
            var material = materialsElement[i];
            var key = $"materials/{i}";
            var child = CreateChild(sourceGuid, packageId, "material", key, "Material", "GltfMaterialImporter");
            children.Add(child);

            var pbr = material.TryGetProperty("pbrMetallicRoughness", out var pbrElement)
                ? pbrElement
                : default;
            var baseColorFactor = pbr.ValueKind == JsonValueKind.Object && pbr.TryGetProperty("baseColorFactor", out var baseColorElement)
                ? ReadVector4(sourcePath, baseColorElement, $"{key}.pbrMetallicRoughness.baseColorFactor")
                : Vector4.One;
            var metallicFactor = pbr.ValueKind == JsonValueKind.Object && pbr.TryGetProperty("metallicFactor", out var metallicElement)
                ? metallicElement.GetSingle()
                : 1.0f;
            var roughnessFactor = pbr.ValueKind == JsonValueKind.Object && pbr.TryGetProperty("roughnessFactor", out var roughnessElement)
                ? roughnessElement.GetSingle()
                : 1.0f;
            var emissiveColor = material.TryGetProperty("emissiveFactor", out var emissiveElement)
                ? ReadVector3(sourcePath, emissiveElement, $"{key}.emissiveFactor")
                : Vector3.Zero;
            var emissiveStrength = ReadEmissiveStrength(material);
            var emissiveFactor = new Vector4(emissiveColor, emissiveStrength);
            var alphaMode = ReadAlphaMode(material, key, warnings);
            var alphaCutoff = material.TryGetProperty("alphaCutoff", out var alphaCutoffElement)
                ? alphaCutoffElement.GetSingle()
                : MaterialPbrDefaults.AlphaCutoff;
            var occlusionStrength = ReadOcclusionStrength(material);

            materials[i] = new GltfImportedMaterial(
                child.Metadata.Guid,
                material.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                baseColorFactor,
                emissiveFactor,
                metallicFactor,
                roughnessFactor,
                ReadTextureRef(sourcePath, root, pbr, "baseColorTexture", $"{key}.pbrMetallicRoughness", warnings),
                ReadTextureRef(sourcePath, root, material, "normalTexture", key, warnings),
                ReadTextureRef(sourcePath, root, material, "emissiveTexture", key, warnings),
                ReadTextureRef(sourcePath, root, pbr, "metallicRoughnessTexture", $"{key}.pbrMetallicRoughness", warnings),
                ReadTextureRef(sourcePath, root, material, "occlusionTexture", key, warnings),
                occlusionStrength,
                alphaMode,
                alphaCutoff);
        }

        return materials;
    }

    private static IReadOnlyList<GltfImportedImageSource> ReadImageSources(
        string sourcePath,
        JsonElement root)
    {
        if (!root.TryGetProperty("images", out var images) || images.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<GltfImportedImageSource>();
        }

        var sources = new GltfImportedImageSource[images.GetArrayLength()];
        for (int i = 0; i < sources.Length; i++)
        {
            var image = images[i];
            if (!image.TryGetProperty("uri", out var uriElement) && !image.TryGetProperty("bufferView", out _))
            {
                throw new InvalidOperationException($"[GltfModelImportPlanner] glTF image '{sourcePath}' images[{i}] must have uri or bufferView.");
            }

            var uri = uriElement.ValueKind == JsonValueKind.String
                ? uriElement.GetString()
                : null;
            var bufferView = image.TryGetProperty("bufferView", out var bufferViewElement)
                ? bufferViewElement.GetInt32()
                : -1;
            var mimeType = image.TryGetProperty("mimeType", out var mimeTypeElement)
                ? mimeTypeElement.GetString()
                : null;

            sources[i] = new GltfImportedImageSource(i, uri, bufferView, mimeType);
        }

        return sources;
    }

    private static void AddTextureChildren(
        Guid sourceGuid,
        string packageId,
        IReadOnlyList<GltfImportedMaterial> materials,
        List<GltfGeneratedChildAsset> children)
    {
        var imageIndices = new SortedSet<int>();
        for (int i = 0; i < materials.Count; i++)
        {
            AddReferencedImageIndex(imageIndices, materials[i].BaseColorTexture);
            AddReferencedImageIndex(imageIndices, materials[i].NormalTexture);
            AddReferencedImageIndex(imageIndices, materials[i].EmissiveTexture);
            AddReferencedImageIndex(imageIndices, materials[i].MetallicRoughnessTexture);
            AddReferencedImageIndex(imageIndices, materials[i].OcclusionTexture);
        }

        foreach (var imageIndex in imageIndices)
        {
            children.Add(CreateChild(sourceGuid, packageId, "texture2d", $"images/{imageIndex}", "Texture2D", "GltfTextureImporter"));
        }
    }

    private static void AddReferencedImageIndex(SortedSet<int> imageIndices, GltfImportedTextureRef? textureRef)
    {
        if (textureRef != null && textureRef.ImageIndex >= 0)
        {
            imageIndices.Add(textureRef.ImageIndex);
        }
    }

    private static GltfImportedTextureRef? ReadTextureRef(
        string sourcePath,
        JsonElement root,
        JsonElement owner,
        string propertyName,
        string ownerContext,
        List<string> warnings)
    {
        if (owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty(propertyName, out var textureInfo) ||
            !textureInfo.TryGetProperty("index", out var indexElement))
        {
            return null;
        }

        var textureIndex = indexElement.GetInt32();
        var context = $"{ownerContext}.{propertyName}";
        var transform = ReadTextureTransform(sourcePath, textureInfo, context, warnings);
        if (!root.TryGetProperty("textures", out var textures) ||
            textures.ValueKind != JsonValueKind.Array ||
            textureIndex < 0 ||
            textureIndex >= textures.GetArrayLength())
        {
            return new GltfImportedTextureRef(
                textureIndex,
                -1,
                null,
                -1,
                null,
                MaterialTextureSamplerSettings.Default,
                true,
                transform);
        }

        var texture = textures[textureIndex];
        var sampler = ReadTextureSamplerSettings(
            root,
            texture,
            context,
            warnings,
            out bool generateMipMaps);
        var imageIndex = texture.TryGetProperty("source", out var sourceElement)
            ? sourceElement.GetInt32()
            : -1;
        string? uri = null;
        var bufferView = -1;
        string? mimeType = null;
        if (imageIndex >= 0 &&
            root.TryGetProperty("images", out var images) &&
            images.ValueKind == JsonValueKind.Array &&
            imageIndex < images.GetArrayLength())
        {
            var image = images[imageIndex];
            if (image.TryGetProperty("uri", out var uriElement))
            {
                uri = uriElement.GetString();
            }

            bufferView = image.TryGetProperty("bufferView", out var bufferViewElement)
                ? bufferViewElement.GetInt32()
                : -1;
            mimeType = image.TryGetProperty("mimeType", out var mimeTypeElement)
                ? mimeTypeElement.GetString()
                : null;
        }

        return new GltfImportedTextureRef(
            textureIndex,
            imageIndex,
            uri,
            bufferView,
            mimeType,
            sampler,
            generateMipMaps,
            transform);
    }

    private static MaterialTextureSamplerSettings ReadTextureSamplerSettings(
        JsonElement root,
        JsonElement texture,
        string context,
        List<string> warnings,
        out bool generateMipMaps)
    {
        var defaults = MaterialTextureSamplerSettings.Default;
        generateMipMaps = true;
        if (!texture.TryGetProperty("sampler", out var samplerIndexElement))
        {
            return defaults;
        }

        if (!samplerIndexElement.TryGetInt32(out var samplerIndex) ||
            samplerIndex < 0 ||
            !root.TryGetProperty("samplers", out var samplers) ||
            samplers.ValueKind != JsonValueKind.Array ||
            samplerIndex >= samplers.GetArrayLength())
        {
            warnings.Add($"{context} references invalid sampler '{samplerIndexElement}'; default sampler settings are used.");
            return defaults;
        }

        var sampler = samplers[samplerIndex];
        var minFilter = defaults.MinFilter;
        var magFilter = defaults.MagFilter;
        var mipmapMode = defaults.MipmapMode;
        var wrapU = defaults.WrapU;
        var wrapV = defaults.WrapV;

        if (sampler.TryGetProperty("minFilter", out var minFilterElement))
        {
            var value = minFilterElement.GetInt32();
            if (!TryMapMinFilter(
                    value,
                    out minFilter,
                    out mipmapMode,
                    out generateMipMaps))
            {
                warnings.Add($"{context} sampler minFilter '{value}' is unsupported; the default minification filter is used.");
                minFilter = defaults.MinFilter;
                mipmapMode = defaults.MipmapMode;
                generateMipMaps = true;
            }
        }

        if (sampler.TryGetProperty("magFilter", out var magFilterElement))
        {
            var value = magFilterElement.GetInt32();
            if (!TryMapMagFilter(value, out magFilter))
            {
                warnings.Add($"{context} sampler magFilter '{value}' is unsupported; the default magnification filter is used.");
                magFilter = defaults.MagFilter;
            }
        }

        if (sampler.TryGetProperty("wrapS", out var wrapSElement))
        {
            var value = wrapSElement.GetInt32();
            if (!TryMapWrapMode(value, out wrapU))
            {
                warnings.Add($"{context} sampler wrapS '{value}' is unsupported; the default U wrap mode is used.");
                wrapU = defaults.WrapU;
            }
        }

        if (sampler.TryGetProperty("wrapT", out var wrapTElement))
        {
            var value = wrapTElement.GetInt32();
            if (!TryMapWrapMode(value, out wrapV))
            {
                warnings.Add($"{context} sampler wrapT '{value}' is unsupported; the default V wrap mode is used.");
                wrapV = defaults.WrapV;
            }
        }

        return new MaterialTextureSamplerSettings(minFilter, magFilter, mipmapMode, wrapU, wrapV);
    }

    private static MaterialTextureTransform ReadTextureTransform(
        string sourcePath,
        JsonElement textureInfo,
        string context,
        List<string> warnings)
    {
        var offset = Vector2.Zero;
        var scale = Vector2.One;
        var rotation = 0.0f;
        var texCoord = ReadTextureCoordinate(textureInfo, "texCoord", 0, sourcePath, context);

        if (textureInfo.TryGetProperty("extensions", out var extensions) &&
            extensions.ValueKind == JsonValueKind.Object &&
            extensions.TryGetProperty("KHR_texture_transform", out var transform) &&
            transform.ValueKind == JsonValueKind.Object)
        {
            if (transform.TryGetProperty("offset", out var offsetElement))
            {
                offset = ReadVector2(sourcePath, offsetElement, $"{context}.extensions.KHR_texture_transform.offset");
            }

            if (transform.TryGetProperty("scale", out var scaleElement))
            {
                scale = ReadVector2(sourcePath, scaleElement, $"{context}.extensions.KHR_texture_transform.scale");
            }

            if (transform.TryGetProperty("rotation", out var rotationElement))
            {
                rotation = rotationElement.GetSingle();
            }

            texCoord = ReadTextureCoordinate(
                transform,
                "texCoord",
                texCoord,
                sourcePath,
                $"{context}.extensions.KHR_texture_transform");
        }

        if (texCoord > 0)
        {
            warnings.Add($"{context} uses TEXCOORD_{texCoord}; metadata is preserved, but the current static mesh material path only samples TEXCOORD_0.");
        }

        return new MaterialTextureTransform(offset, scale, rotation, texCoord);
    }

    private static uint ReadTextureCoordinate(
        JsonElement owner,
        string propertyName,
        uint defaultValue,
        string sourcePath,
        string context)
    {
        if (!owner.TryGetProperty(propertyName, out var texCoordElement))
        {
            return defaultValue;
        }

        if (!texCoordElement.TryGetInt32(out var texCoord) || texCoord < 0)
        {
            throw new InvalidOperationException(
                $"[GltfModelImportPlanner] glTF source '{sourcePath}' {context}.{propertyName} must be a non-negative integer.");
        }

        return checked((uint)texCoord);
    }

    private static bool TryMapMinFilter(
        int value,
        out MaterialTextureFilter filter,
        out MaterialTextureMipmapMode mipmapMode,
        out bool generateMipMaps)
    {
        switch (value)
        {
            case 9728:
                filter = MaterialTextureFilter.Nearest;
                mipmapMode = MaterialTextureMipmapMode.Nearest;
                generateMipMaps = false;
                return true;
            case 9729:
                filter = MaterialTextureFilter.Linear;
                mipmapMode = MaterialTextureMipmapMode.Nearest;
                generateMipMaps = false;
                return true;
            case 9984:
                filter = MaterialTextureFilter.Nearest;
                mipmapMode = MaterialTextureMipmapMode.Nearest;
                generateMipMaps = true;
                return true;
            case 9985:
                filter = MaterialTextureFilter.Linear;
                mipmapMode = MaterialTextureMipmapMode.Nearest;
                generateMipMaps = true;
                return true;
            case 9986:
                filter = MaterialTextureFilter.Nearest;
                mipmapMode = MaterialTextureMipmapMode.Linear;
                generateMipMaps = true;
                return true;
            case 9987:
                filter = MaterialTextureFilter.Linear;
                mipmapMode = MaterialTextureMipmapMode.Linear;
                generateMipMaps = true;
                return true;
            default:
                filter = default;
                mipmapMode = default;
                generateMipMaps = default;
                return false;
        }
    }

    private static bool TryMapMagFilter(int value, out MaterialTextureFilter filter)
    {
        switch (value)
        {
            case 9728:
                filter = MaterialTextureFilter.Nearest;
                return true;
            case 9729:
                filter = MaterialTextureFilter.Linear;
                return true;
            default:
                filter = default;
                return false;
        }
    }

    private static bool TryMapWrapMode(int value, out MaterialTextureWrapMode wrapMode)
    {
        switch (value)
        {
            case 10497:
                wrapMode = MaterialTextureWrapMode.Repeat;
                return true;
            case 33648:
                wrapMode = MaterialTextureWrapMode.MirroredRepeat;
                return true;
            case 33071:
                wrapMode = MaterialTextureWrapMode.ClampToEdge;
                return true;
            default:
                wrapMode = default;
                return false;
        }
    }

    private static void AddUnsupportedFeatureWarnings(JsonElement root, List<string> warnings)
    {
        if (root.TryGetProperty("skins", out var skins) && skins.ValueKind == JsonValueKind.Array && skins.GetArrayLength() > 0)
        {
            warnings.Add("skins are not imported by the first model slice.");
        }

        if (root.TryGetProperty("animations", out var animations) &&
            animations.ValueKind == JsonValueKind.Array &&
            animations.GetArrayLength() > 0)
        {
            warnings.Add("animations are not imported by the first model slice.");
        }

        if (root.TryGetProperty("meshes", out var meshes) && meshes.ValueKind == JsonValueKind.Array)
        {
            for (int meshIndex = 0; meshIndex < meshes.GetArrayLength(); meshIndex++)
            {
                if (!meshes[meshIndex].TryGetProperty("primitives", out var primitives) ||
                    primitives.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                for (int primitiveIndex = 0; primitiveIndex < primitives.GetArrayLength(); primitiveIndex++)
                {
                    if (primitives[primitiveIndex].TryGetProperty("targets", out _))
                    {
                        warnings.Add($"meshes/{meshIndex}/primitives/{primitiveIndex} morph targets are not imported by the first model slice.");
                    }
                }
            }
        }
    }

    private static GltfMaterialAlphaMode ReadAlphaMode(
        JsonElement material,
        string context,
        List<string> warnings)
    {
        if (!material.TryGetProperty("alphaMode", out var alphaModeElement))
        {
            return GltfMaterialAlphaMode.Opaque;
        }

        var alphaMode = alphaModeElement.GetString();
        if (string.Equals(alphaMode, "OPAQUE", StringComparison.OrdinalIgnoreCase))
        {
            return GltfMaterialAlphaMode.Opaque;
        }

        if (string.Equals(alphaMode, "MASK", StringComparison.OrdinalIgnoreCase))
        {
            return GltfMaterialAlphaMode.Mask;
        }

        if (string.Equals(alphaMode, "BLEND", StringComparison.OrdinalIgnoreCase))
        {
            return GltfMaterialAlphaMode.Blend;
        }

        warnings.Add($"{context}.alphaMode '{alphaMode}' is unsupported; the generated material is emitted as opaque.");
        return GltfMaterialAlphaMode.Unsupported;
    }

    private static float ReadOcclusionStrength(JsonElement material)
    {
        if (!material.TryGetProperty("occlusionTexture", out var occlusionTexture) ||
            occlusionTexture.ValueKind != JsonValueKind.Object ||
            !occlusionTexture.TryGetProperty("strength", out var strength))
        {
            return MaterialPbrDefaults.OcclusionStrength;
        }

        return strength.GetSingle();
    }

    private static Vector2 ReadVector2(string sourcePath, JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 2)
        {
            throw new InvalidOperationException($"[GltfModelImportPlanner] glTF source '{sourcePath}' {context} must contain two numeric values.");
        }

        return new Vector2(
            element[0].GetSingle(),
            element[1].GetSingle());
    }

    private static Vector4 ReadVector4(string sourcePath, JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 4)
        {
            throw new InvalidOperationException($"[GltfModelImportPlanner] glTF source '{sourcePath}' {context} must contain four numeric values.");
        }

        return new Vector4(
            element[0].GetSingle(),
            element[1].GetSingle(),
            element[2].GetSingle(),
            element[3].GetSingle());
    }

    private static Vector3 ReadVector3(string sourcePath, JsonElement element, string context)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != 3)
        {
            throw new InvalidOperationException($"[GltfModelImportPlanner] glTF source '{sourcePath}' {context} must contain three numeric values.");
        }

        return new Vector3(
            element[0].GetSingle(),
            element[1].GetSingle(),
            element[2].GetSingle());
    }

    private static float ReadEmissiveStrength(JsonElement material)
    {
        if (!material.TryGetProperty("extensions", out var extensions) ||
            extensions.ValueKind != JsonValueKind.Object ||
            !extensions.TryGetProperty("KHR_materials_emissive_strength", out var emissiveStrengthExtension) ||
            emissiveStrengthExtension.ValueKind != JsonValueKind.Object ||
            !emissiveStrengthExtension.TryGetProperty("emissiveStrength", out var strength))
        {
            return 1.0f;
        }

        return Math.Max(0.0f, strength.GetSingle());
    }

    private static GltfGeneratedChildAsset CreateChild(
        Guid sourceGuid,
        string packageId,
        string kind,
        string key,
        string assetType,
        string importer)
    {
        return new GltfGeneratedChildAsset(
            kind,
            key,
            GeneratedAssetIdentity.CreateChildMetadata(sourceGuid, packageId, kind, key, assetType, importer));
    }

    private static string NormalizePackageId(string packageId)
    {
        if (string.IsNullOrWhiteSpace(packageId))
        {
            throw new ArgumentException("Model import planning requires a package id.", nameof(packageId));
        }

        return packageId.Trim().Replace('\\', '/').ToLowerInvariant();
    }
}
