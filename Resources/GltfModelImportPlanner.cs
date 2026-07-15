using System.Buffers.Binary;
using System.Numerics;
using System.Text.Json;
using ArisenEngine.Core.Assets;

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

public sealed record GltfImportedMaterial(
    Guid Guid,
    string Name,
    Vector4 BaseColorFactor,
    Vector4 EmissiveFactor,
    float MetallicFactor,
    float RoughnessFactor,
    GltfImportedTextureRef? BaseColorTexture,
    GltfImportedTextureRef? NormalTexture,
    GltfImportedTextureRef? EmissiveTexture);

public sealed record GltfImportedTextureRef(
    int TextureIndex,
    int ImageIndex,
    string? Uri,
    int BufferView = -1,
    string? MimeType = null);

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
        AddTextureChildren(sourceGuid, normalizedPackageId, images, children);
        AddUnsupportedFeatureWarnings(root, warnings);

        return new GltfModelImportPlan(
            sourceGuid,
            normalizedPackageId,
            children,
            materials,
            images,
            warnings);
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

            if (material.TryGetProperty("occlusionTexture", out _))
            {
                warnings.Add($"materials/{i}.occlusionTexture is not imported by the first material slice.");
            }

            if (material.TryGetProperty("alphaMode", out var alphaMode) &&
                !string.Equals(alphaMode.GetString(), "OPAQUE", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"materials/{i}.alphaMode '{alphaMode.GetString()}' is not imported by the first material slice.");
            }

            materials[i] = new GltfImportedMaterial(
                child.Metadata.Guid,
                material.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty,
                baseColorFactor,
                emissiveFactor,
                metallicFactor,
                roughnessFactor,
                ReadTextureRef(root, pbr, "baseColorTexture"),
                ReadTextureRef(root, material, "normalTexture"),
                ReadTextureRef(root, material, "emissiveTexture"));
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
        IReadOnlyList<GltfImportedImageSource> images,
        List<GltfGeneratedChildAsset> children)
    {
        for (int i = 0; i < images.Count; i++)
        {
            children.Add(CreateChild(sourceGuid, packageId, "texture2d", $"images/{i}", "Texture2D", "GltfTextureImporter"));
        }
    }

    private static GltfImportedTextureRef? ReadTextureRef(JsonElement root, JsonElement owner, string propertyName)
    {
        if (owner.ValueKind != JsonValueKind.Object ||
            !owner.TryGetProperty(propertyName, out var textureInfo) ||
            !textureInfo.TryGetProperty("index", out var indexElement))
        {
            return null;
        }

        var textureIndex = indexElement.GetInt32();
        if (!root.TryGetProperty("textures", out var textures) ||
            textures.ValueKind != JsonValueKind.Array ||
            textureIndex < 0 ||
            textureIndex >= textures.GetArrayLength())
        {
            return new GltfImportedTextureRef(textureIndex, -1, null);
        }

        var texture = textures[textureIndex];
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

        return new GltfImportedTextureRef(textureIndex, imageIndex, uri, bufferView, mimeType);
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
