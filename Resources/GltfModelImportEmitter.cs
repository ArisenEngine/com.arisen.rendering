using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;
using ArisenEngine.Core.Serialization;

namespace ArisenEngine.Rendering.Resources;

public sealed record GltfModelImportEmissionSettings(
    Guid ShaderGuid,
    string ShaderName,
    string OutputName = "Model",
    bool EmitTextureRefs = true);

public sealed record GltfModelImportEmissionResult(
    IReadOnlyList<string> MaterialPaths,
    IReadOnlyList<string> TexturePaths,
    IReadOnlyList<string> ScenePaths,
    IReadOnlyList<string> MeshPaths,
    IReadOnlyList<string> Warnings);

public static class GltfModelImportEmitter
{
    private const uint GltfBinaryMagic = 0x46546C67;
    private const uint GltfBinaryJsonChunkType = 0x4E4F534A;
    private const uint GltfBinaryBinChunkType = 0x004E4942;
    private const int GltfBinaryHeaderSize = 12;
    private const int GltfBinaryChunkHeaderSize = 8;

    public static GltfModelImportEmissionResult Emit(
        GltfModelImportPlan plan,
        string sourcePath,
        string outputDirectory,
        GltfModelImportEmissionSettings settings)
    {
        if (plan == null)
        {
            throw new ArgumentNullException(nameof(plan));
        }

        if (settings == null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Model import emission requires the glTF source path.", nameof(sourcePath));
        }

        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            throw new ArgumentException("Model import emission requires an output directory.", nameof(outputDirectory));
        }

        if (settings.ShaderGuid == Guid.Empty)
        {
            throw new ArgumentException("Generated materials require a stable shader GUID.", nameof(settings));
        }

        using var _ = Profiler.Zone("GltfModelImportEmitter.Emit");
        var materialPaths = new List<string>();
        var texturePaths = new List<string>();
        var scenePaths = new List<string>();
        var meshPaths = new List<string>();
        var warnings = new List<string>(plan.Warnings);
        var emittedTextureAssets = new Dictionary<int, EmittedTextureAsset>();
        var safeOutputName = SanitizePathSegment(settings.OutputName);
        var materialDirectory = Path.Combine(outputDirectory, safeOutputName, "Materials");
        var textureDirectory = Path.Combine(outputDirectory, safeOutputName, "Textures");
        var meshDirectory = Path.Combine(outputDirectory, safeOutputName, "Meshes");
        var sceneDirectory = Path.Combine(outputDirectory, safeOutputName, "Scenes");
        using var source = LoadGltfSourceData(sourcePath);

        EmitMeshSources(sourcePath, source.Root, source.BinaryChunk, plan, meshDirectory, meshPaths, warnings);
        EmitScenes(source.Root, plan, sceneDirectory, scenePaths, warnings);

        for (int i = 0; i < plan.Materials.Count; i++)
        {
            var material = plan.Materials[i];
            var materialChild = FindChild(plan, "material", $"materials/{i}");
            var emittedTextures = settings.EmitTextureRefs
                ? EmitMaterialTextures(
                    sourcePath,
                    plan,
                    material,
                    textureDirectory,
                    texturePaths,
                    warnings,
                    emittedTextureAssets)
                : Array.Empty<EmittedTextureRef>();

            var materialName = string.IsNullOrWhiteSpace(material.Name)
                ? $"Material_{i}"
                : material.Name;
            var materialPath = Path.Combine(materialDirectory, $"{SanitizePathSegment(materialName)}.arismaterial");
            Directory.CreateDirectory(Path.GetDirectoryName(materialPath)!);
            File.WriteAllText(
                materialPath,
                BuildMaterialSource(material, emittedTextures, settings),
                Encoding.UTF8);
            WriteMetadata(materialPath + ".meta", materialChild.Metadata, plan.SourceGuid);
            materialPaths.Add(materialPath);
        }

        Profiler.PlotValue("ModelImport.EmittedSceneCount", scenePaths.Count);
        Profiler.PlotValue("ModelImport.EmittedMeshCount", meshPaths.Count);
        Profiler.PlotValue("ModelImport.EmittedMaterialCount", materialPaths.Count);
        Profiler.PlotValue("ModelImport.EmittedTextureCount", texturePaths.Count);
        Profiler.PlotValue("ModelImport.EmissionWarningCount", warnings.Count);
        return new GltfModelImportEmissionResult(materialPaths, texturePaths, scenePaths, meshPaths, warnings);
    }

    private static void EmitMeshSources(
        string sourcePath,
        JsonElement root,
        byte[]? embeddedBinaryBuffer,
        GltfModelImportPlan plan,
        string meshDirectory,
        List<string> meshPaths,
        List<string> warnings)
    {
        if (!root.TryGetProperty("meshes", out var meshes) || meshes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var sourceExtension = Path.GetExtension(sourcePath);
        var emitGlb = string.Equals(sourceExtension, ".glb", StringComparison.OrdinalIgnoreCase);
        var outputExtension = emitGlb ? ".glb" : ".gltf";
        for (int i = 0; i < meshes.GetArrayLength(); i++)
        {
            var meshChild = FindChild(plan, "mesh", $"meshes/{i}");
            var meshPath = Path.Combine(meshDirectory, $"Mesh_{i}{outputExtension}");
            Directory.CreateDirectory(Path.GetDirectoryName(meshPath)!);

            if (emitGlb)
            {
                WriteSingleMeshGlb(meshPath, root, embeddedBinaryBuffer, i);
            }
            else
            {
                WriteSingleMeshGltf(meshPath, root, i);
            }

            CopyExternalGltfDependencies(sourcePath, root, Path.GetDirectoryName(meshPath)!, warnings);
            WriteMetadata(meshPath + ".meta", meshChild.Metadata, plan.SourceGuid);
            meshPaths.Add(meshPath);
        }
    }

    private static void EmitScenes(
        JsonElement root,
        GltfModelImportPlan plan,
        string sceneDirectory,
        List<string> scenePaths,
        List<string> warnings)
    {
        if (!root.TryGetProperty("scenes", out var scenes) || scenes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var nodes = root.TryGetProperty("nodes", out var nodeArray) && nodeArray.ValueKind == JsonValueKind.Array
            ? nodeArray
            : default;
        var meshes = root.TryGetProperty("meshes", out var meshArray) && meshArray.ValueKind == JsonValueKind.Array
            ? meshArray
            : default;

        for (int i = 0; i < scenes.GetArrayLength(); i++)
        {
            var sceneChild = FindChild(plan, "scene", $"scenes/{i}");
            var scene = scenes[i];
            var sceneName = scene.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? $"Scene_{i}"
                : $"Scene_{i}";
            var entities = new List<EmittedSceneEntity>();

            if (nodes.ValueKind == JsonValueKind.Array &&
                meshes.ValueKind == JsonValueKind.Array &&
                scene.TryGetProperty("nodes", out var rootNodes) &&
                rootNodes.ValueKind == JsonValueKind.Array)
            {
                var stack = new HashSet<int>();
                foreach (var rootNode in rootNodes.EnumerateArray())
                {
                    TraverseSceneNode(
                        nodes,
                        meshes,
                        plan,
                        rootNode.GetInt32(),
                        Matrix4x4.Identity,
                        stack,
                        entities,
                        warnings);
                }
            }
            else
            {
                warnings.Add($"scenes/{i} has no supported node hierarchy; generated scene source contains no mesh entities.");
            }

            var scenePath = Path.Combine(sceneDirectory, $"Scene_{i}.arisenscene");
            Directory.CreateDirectory(Path.GetDirectoryName(scenePath)!);
            File.WriteAllText(
                scenePath,
                BuildSceneSource(sceneName, plan.PackageId, entities),
                Encoding.UTF8);
            WriteMetadata(scenePath + ".meta", sceneChild.Metadata, plan.SourceGuid);
            scenePaths.Add(scenePath);
        }
    }

    private static void TraverseSceneNode(
        JsonElement nodes,
        JsonElement meshes,
        GltfModelImportPlan plan,
        int nodeIndex,
        Matrix4x4 parentTransform,
        HashSet<int> stack,
        List<EmittedSceneEntity> entities,
        List<string> warnings)
    {
        if (nodeIndex < 0 || nodeIndex >= nodes.GetArrayLength())
        {
            warnings.Add($"scene node index '{nodeIndex}' is outside node count '{nodes.GetArrayLength()}'; generated scene skipped the node.");
            return;
        }

        if (!stack.Add(nodeIndex))
        {
            warnings.Add($"scene node hierarchy cycle at node '{nodeIndex}'; generated scene skipped the recursive branch.");
            return;
        }

        try
        {
            var node = nodes[nodeIndex];
            var localTransform = ReadNodeTransform(node, $"nodes/{nodeIndex}", warnings);
            var worldTransform = localTransform * parentTransform;
            if (node.TryGetProperty("mesh", out var meshElement))
            {
                var meshIndex = meshElement.GetInt32();
                if (meshIndex >= 0 && meshIndex < meshes.GetArrayLength())
                {
                    EmitSceneMeshEntities(
                        node,
                        nodeIndex,
                        meshes[meshIndex],
                        meshIndex,
                        plan,
                        worldTransform,
                        entities,
                        warnings);
                }
                else
                {
                    warnings.Add($"nodes/{nodeIndex}.mesh index '{meshIndex}' is outside mesh count '{meshes.GetArrayLength()}'; generated scene skipped the mesh renderer.");
                }
            }

            if (node.TryGetProperty("children", out var children))
            {
                if (children.ValueKind != JsonValueKind.Array)
                {
                    warnings.Add($"nodes/{nodeIndex}.children is not an array; generated scene skipped child traversal.");
                    return;
                }

                foreach (var child in children.EnumerateArray())
                {
                    TraverseSceneNode(
                        nodes,
                        meshes,
                        plan,
                        child.GetInt32(),
                        worldTransform,
                        stack,
                        entities,
                        warnings);
                }
            }
        }
        finally
        {
            stack.Remove(nodeIndex);
        }
    }

    private static void EmitSceneMeshEntities(
        JsonElement node,
        int nodeIndex,
        JsonElement mesh,
        int meshIndex,
        GltfModelImportPlan plan,
        Matrix4x4 worldTransform,
        List<EmittedSceneEntity> entities,
        List<string> warnings)
    {
        var meshChild = FindChild(plan, "mesh", $"meshes/{meshIndex}");
        if (!mesh.TryGetProperty("primitives", out var primitives) ||
            primitives.ValueKind != JsonValueKind.Array)
        {
            warnings.Add($"meshes/{meshIndex} has no primitive array; generated scene skipped the mesh renderer.");
            return;
        }

        var primitiveCount = primitives.GetArrayLength();
        if (primitiveCount == 0)
        {
            warnings.Add($"meshes/{meshIndex} has no primitives; generated scene skipped the mesh renderer.");
            return;
        }

        var baseEntityName = ResolveSceneEntityName(node, nodeIndex, meshIndex);
        for (int i = 0; i < primitiveCount; i++)
        {
            var materialGuid = ResolvePrimitiveMaterialGuid(primitives[i], meshIndex, i, plan, warnings);
            var entityName = primitiveCount == 1
                ? baseEntityName
                : $"{baseEntityName}_Primitive_{i}";
            entities.Add(CreateSceneEntity(
                entityName,
                worldTransform,
                meshChild.Metadata.Guid,
                materialGuid,
                i,
                1));
        }
    }

    private static string ResolveSceneEntityName(JsonElement node, int nodeIndex, int meshIndex)
    {
        if (node.TryGetProperty("name", out var nameElement))
        {
            return nameElement.GetString() ?? $"Node_{nodeIndex}";
        }

        return $"Node_{nodeIndex}_Mesh_{meshIndex}";
    }

    private static EmittedSceneEntity CreateSceneEntity(
        string name,
        Matrix4x4 transform,
        Guid meshGuid,
        Guid materialGuid,
        int firstSubmeshIndex,
        int submeshCount)
    {
        if (!Matrix4x4.Decompose(transform, out var scale, out var rotation, out var translation))
        {
            scale = Vector3.One;
            rotation = Quaternion.Identity;
            translation = transform.Translation;
        }

        if (rotation.LengthSquared() > float.Epsilon)
        {
            rotation = Quaternion.Normalize(rotation);
        }
        else
        {
            rotation = Quaternion.Identity;
        }

        return new EmittedSceneEntity(
            name,
            translation,
            rotation,
            scale,
            meshGuid,
            materialGuid,
            firstSubmeshIndex,
            submeshCount);
    }

    private static Guid ResolvePrimitiveMaterialGuid(
        JsonElement primitive,
        int meshIndex,
        int primitiveIndex,
        GltfModelImportPlan plan,
        List<string> warnings)
    {
        if (!primitive.TryGetProperty("material", out var materialElement))
        {
            return Guid.Empty;
        }

        if (materialElement.ValueKind != JsonValueKind.Number)
        {
            warnings.Add($"meshes/{meshIndex}.primitives/{primitiveIndex}.material is not a number; generated scene left the material unset.");
            return Guid.Empty;
        }

        var materialIndex = materialElement.GetInt32();
        if (materialIndex < 0 || materialIndex >= plan.Materials.Count)
        {
            warnings.Add($"meshes/{meshIndex}.primitives/{primitiveIndex}.material index '{materialIndex}' is outside generated material count '{plan.Materials.Count}'; generated scene left the material unset.");
            return Guid.Empty;
        }

        return plan.Materials[materialIndex].Guid;
    }

    private static Matrix4x4 ReadNodeTransform(
        JsonElement node,
        string context,
        List<string> warnings)
    {
        var hasMatrix = node.TryGetProperty("matrix", out var matrixElement);
        var hasTranslation = node.TryGetProperty("translation", out var translationElement);
        var hasRotation = node.TryGetProperty("rotation", out var rotationElement);
        var hasScale = node.TryGetProperty("scale", out var scaleElement);
        if (hasMatrix && (hasTranslation || hasRotation || hasScale))
        {
            warnings.Add($"{context} uses matrix and TRS together; generated scene used the matrix.");
        }

        if (hasMatrix)
        {
            var values = ReadFloatArray(matrixElement, 16, $"{context}.matrix");
            return new Matrix4x4(
                values[0],
                values[1],
                values[2],
                values[3],
                values[4],
                values[5],
                values[6],
                values[7],
                values[8],
                values[9],
                values[10],
                values[11],
                values[12],
                values[13],
                values[14],
                values[15]);
        }

        var translation = hasTranslation
            ? ReadVector3(translationElement, $"{context}.translation")
            : Vector3.Zero;
        var rotation = hasRotation
            ? ReadQuaternion(rotationElement, $"{context}.rotation")
            : Quaternion.Identity;
        var scale = hasScale
            ? ReadVector3(scaleElement, $"{context}.scale")
            : Vector3.One;

        return Matrix4x4.CreateScale(scale) *
               Matrix4x4.CreateFromQuaternion(rotation) *
               Matrix4x4.CreateTranslation(translation);
    }

    private static Vector3 ReadVector3(JsonElement element, string context)
    {
        var values = ReadFloatArray(element, 3, context);
        return new Vector3(values[0], values[1], values[2]);
    }

    private static Quaternion ReadQuaternion(JsonElement element, string context)
    {
        var values = ReadFloatArray(element, 4, context);
        return new Quaternion(values[0], values[1], values[2], values[3]);
    }

    private static float[] ReadFloatArray(JsonElement element, int count, string context)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != count)
        {
            throw new InvalidOperationException($"[GltfModelImportEmitter] {context} must contain {count} numeric values.");
        }

        var values = new float[count];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = element[i].GetSingle();
        }

        return values;
    }

    private static string BuildSceneSource(
        string sceneName,
        string packageId,
        IReadOnlyList<EmittedSceneEntity> entities)
    {
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Name: {EscapeScalar(sceneName)}");
        if (entities.Count == 0)
        {
            builder.AppendLine("Entities: []");
            return builder.ToString();
        }

        builder.AppendLine("Entities:");
        for (int i = 0; i < entities.Count; i++)
        {
            var entity = entities[i];
            builder.AppendLine(CultureInfo.InvariantCulture, $"- Name: {EscapeScalar(entity.Name)}");
            builder.AppendLine("  Transform:");
            AppendVector3(builder, "    Position", entity.Position);
            AppendQuaternion(builder, "    Rotation", entity.Rotation);
            AppendVector3(builder, "    Scale", entity.Scale);
            builder.AppendLine("  MeshRenderer:");
            builder.AppendLine("    Mesh:");
            builder.AppendLine(CultureInfo.InvariantCulture, $"      Guid: {entity.MeshGuid:D}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"      PackageId: {EscapeScalar(packageId)}");
            if (entity.MaterialGuid != Guid.Empty)
            {
                builder.AppendLine("    Material:");
                builder.AppendLine(CultureInfo.InvariantCulture, $"      Guid: {entity.MaterialGuid:D}");
                builder.AppendLine(CultureInfo.InvariantCulture, $"      PackageId: {EscapeScalar(packageId)}");
            }

            builder.AppendLine(CultureInfo.InvariantCulture, $"    FirstSubmeshIndex: {entity.FirstSubmeshIndex}");
            builder.AppendLine(CultureInfo.InvariantCulture, $"    SubmeshCount: {entity.SubmeshCount}");
            builder.AppendLine("    Visible: true");
        }

        return builder.ToString();
    }

    private static void AppendVector3(StringBuilder builder, string label, Vector3 value)
    {
        builder.AppendLine(CultureInfo.InvariantCulture, $"{label}:");
        builder.AppendLine(CultureInfo.InvariantCulture, $"      X: {FormatFloat(value.X)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"      Y: {FormatFloat(value.Y)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"      Z: {FormatFloat(value.Z)}");
    }

    private static void AppendQuaternion(StringBuilder builder, string label, Quaternion value)
    {
        builder.AppendLine(CultureInfo.InvariantCulture, $"{label}:");
        builder.AppendLine(CultureInfo.InvariantCulture, $"      X: {FormatFloat(value.X)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"      Y: {FormatFloat(value.Y)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"      Z: {FormatFloat(value.Z)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"      W: {FormatFloat(value.W)}");
    }

    private static void WriteSingleMeshGltf(string meshPath, JsonElement root, int meshIndex)
    {
        using var stream = File.Create(meshPath);
        using var writer = new Utf8JsonWriter(
            stream,
            new JsonWriterOptions { Indented = true });
        WriteSingleMeshGltfDocument(writer, root, meshIndex);
    }

    private static void WriteSingleMeshGlb(
        string meshPath,
        JsonElement root,
        byte[]? embeddedBinaryBuffer,
        int meshIndex)
    {
        var jsonBytes = BuildSingleMeshGltfBytes(root, meshIndex);
        var paddedJson = PadTo4(jsonBytes, 0x20);
        var paddedBin = embeddedBinaryBuffer is { Length: > 0 }
            ? PadTo4(embeddedBinaryBuffer, 0)
            : Array.Empty<byte>();
        var totalLength = checked(GltfBinaryHeaderSize + GltfBinaryChunkHeaderSize + paddedJson.Length +
            (paddedBin.Length > 0 ? GltfBinaryChunkHeaderSize + paddedBin.Length : 0));

        using var stream = File.Create(meshPath);
        Span<byte> header = stackalloc byte[GltfBinaryHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(0, sizeof(uint)), GltfBinaryMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(4, sizeof(uint)), 2);
        BinaryPrimitives.WriteUInt32LittleEndian(header.Slice(8, sizeof(uint)), checked((uint)totalLength));
        stream.Write(header);

        Span<byte> chunkHeader = stackalloc byte[GltfBinaryChunkHeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.Slice(0, sizeof(uint)), checked((uint)paddedJson.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.Slice(4, sizeof(uint)), GltfBinaryJsonChunkType);
        stream.Write(chunkHeader);
        stream.Write(paddedJson);

        if (paddedBin.Length > 0)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.Slice(0, sizeof(uint)), checked((uint)paddedBin.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(chunkHeader.Slice(4, sizeof(uint)), GltfBinaryBinChunkType);
            stream.Write(chunkHeader);
            stream.Write(paddedBin);
        }
    }

    private static byte[] BuildSingleMeshGltfBytes(JsonElement root, int meshIndex)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteSingleMeshGltfDocument(writer, root, meshIndex);
        }

        return stream.ToArray();
    }

    private static void WriteSingleMeshGltfDocument(Utf8JsonWriter writer, JsonElement root, int meshIndex)
    {
        if (!root.TryGetProperty("meshes", out var meshes) ||
            meshes.ValueKind != JsonValueKind.Array ||
            meshIndex < 0 ||
            meshIndex >= meshes.GetArrayLength())
        {
            throw new InvalidOperationException($"[GltfModelImportEmitter] glTF mesh index '{meshIndex}' is outside the source mesh array.");
        }

        writer.WriteStartObject();
        WriteRequiredJsonProperty(writer, root, "asset");
        WriteOptionalJsonProperty(writer, root, "buffers");
        WriteOptionalJsonProperty(writer, root, "bufferViews");
        WriteOptionalJsonProperty(writer, root, "accessors");
        WriteOptionalJsonProperty(writer, root, "materials");
        WriteOptionalJsonProperty(writer, root, "textures");
        WriteOptionalJsonProperty(writer, root, "images");
        WriteOptionalJsonProperty(writer, root, "samplers");
        WriteOptionalJsonProperty(writer, root, "extensionsUsed");
        WriteOptionalJsonProperty(writer, root, "extensionsRequired");
        writer.WritePropertyName("meshes");
        writer.WriteStartArray();
        meshes[meshIndex].WriteTo(writer);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteRequiredJsonProperty(Utf8JsonWriter writer, JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidOperationException($"[GltfModelImportEmitter] glTF source is missing required '{propertyName}' property.");
        }

        writer.WritePropertyName(propertyName);
        property.WriteTo(writer);
    }

    private static void WriteOptionalJsonProperty(Utf8JsonWriter writer, JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var property))
        {
            return;
        }

        writer.WritePropertyName(propertyName);
        property.WriteTo(writer);
    }

    private static void CopyExternalGltfDependencies(
        string sourcePath,
        JsonElement root,
        string targetDirectory,
        List<string> warnings)
    {
        CopyExternalGltfUris(sourcePath, root, "buffers", targetDirectory, warnings);
        CopyExternalGltfUris(sourcePath, root, "images", targetDirectory, warnings);
    }

    private static void CopyExternalGltfUris(
        string sourcePath,
        JsonElement root,
        string arrayName,
        string targetDirectory,
        List<string> warnings)
    {
        if (!root.TryGetProperty(arrayName, out var array) || array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        for (int i = 0; i < array.GetArrayLength(); i++)
        {
            var element = array[i];
            if (!element.TryGetProperty("uri", out var uriElement) ||
                uriElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            var uri = uriElement.GetString();
            if (string.IsNullOrWhiteSpace(uri) ||
                uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (uri.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{arrayName}[{i}].uri '{uri}' is remote; generated mesh source keeps the URI but does not copy it into the package output.");
                continue;
            }

            var decoded = Uri.UnescapeDataString(uri).Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(decoded))
            {
                warnings.Add($"{arrayName}[{i}].uri '{uri}' is absolute; generated mesh source keeps the URI but does not copy it into the package output.");
                continue;
            }

            var sourceDependencyPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, decoded));
            if (!File.Exists(sourceDependencyPath))
            {
                warnings.Add($"{arrayName}[{i}].uri resolves to missing dependency '{sourceDependencyPath}'; generated mesh source may not cook until the dependency exists.");
                continue;
            }

            var targetRoot = Path.GetFullPath(targetDirectory);
            var targetDependencyPath = Path.GetFullPath(Path.Combine(targetRoot, decoded));
            var targetRootWithSeparator = targetRoot.EndsWith(Path.DirectorySeparatorChar)
                ? targetRoot
                : targetRoot + Path.DirectorySeparatorChar;
            if (!targetDependencyPath.StartsWith(targetRootWithSeparator, StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"{arrayName}[{i}].uri '{uri}' escapes the generated mesh directory; dependency was not copied.");
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetDependencyPath)!);
            if (!string.Equals(sourceDependencyPath, targetDependencyPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourceDependencyPath, targetDependencyPath, overwrite: true);
            }
        }
    }

    private static byte[] PadTo4(byte[] bytes, byte padding)
    {
        var paddedLength = (bytes.Length + 3) & ~3;
        if (paddedLength == bytes.Length)
        {
            return bytes;
        }

        var padded = new byte[paddedLength];
        bytes.CopyTo(padded, 0);
        padded.AsSpan(bytes.Length).Fill(padding);
        return padded;
    }

    private static IReadOnlyList<EmittedTextureRef> EmitMaterialTextures(
        string sourcePath,
        GltfModelImportPlan plan,
        GltfImportedMaterial material,
        string textureDirectory,
        List<string> texturePaths,
        List<string> warnings,
        Dictionary<int, EmittedTextureAsset> emittedTextureAssets)
    {
        var emitted = new List<EmittedTextureRef>(capacity: 5);
        TryEmitTexture(sourcePath, plan, material.BaseColorTexture, MaterialTextureSlots.BaseColor, 0, textureDirectory, texturePaths, warnings, emittedTextureAssets, emitted);
        TryEmitTexture(sourcePath, plan, material.NormalTexture, MaterialTextureSlots.Normal, 1, textureDirectory, texturePaths, warnings, emittedTextureAssets, emitted);
        TryEmitTexture(sourcePath, plan, material.EmissiveTexture, MaterialTextureSlots.Emissive, 2, textureDirectory, texturePaths, warnings, emittedTextureAssets, emitted);
        TryEmitTexture(sourcePath, plan, material.MetallicRoughnessTexture, MaterialTextureSlots.MetallicRoughness, 3, textureDirectory, texturePaths, warnings, emittedTextureAssets, emitted);
        TryEmitTexture(sourcePath, plan, material.OcclusionTexture, MaterialTextureSlots.Occlusion, 4, textureDirectory, texturePaths, warnings, emittedTextureAssets, emitted);
        return emitted;
    }

    private static void TryEmitTexture(
        string sourcePath,
        GltfModelImportPlan plan,
        GltfImportedTextureRef? textureRef,
        string slotName,
        uint slot,
        string textureDirectory,
        List<string> texturePaths,
        List<string> warnings,
        Dictionary<int, EmittedTextureAsset> emittedTextureAssets,
        List<EmittedTextureRef> emitted)
    {
        if (textureRef == null)
        {
            return;
        }

        if (textureRef.ImageIndex < 0)
        {
            warnings.Add($"textures/{textureRef.TextureIndex} does not resolve to an image source; {slotName} texture was not emitted.");
            return;
        }

        var textureChild = FindChild(plan, "texture2d", $"images/{textureRef.ImageIndex}");
        if (emittedTextureAssets.TryGetValue(textureRef.ImageIndex, out var existingTextureAsset))
        {
            emitted.Add(CreateEmittedTextureRef(slotName, slot, textureRef, existingTextureAsset));
            return;
        }

        var textureName = $"{slotName}_{textureRef.ImageIndex}";

        if (!string.IsNullOrWhiteSpace(textureRef.Uri) &&
            textureRef.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
        {
            if (!TryDecodeGltfImageDataUri(textureRef.Uri, out var payload, out var extension, out var dataUriWarning))
            {
                warnings.Add($"images/{textureRef.ImageIndex}.uri data payload was not emitted for {slotName}: {dataUriWarning}");
                return;
            }

            var textureAsset = EmitTexturePayload(
                textureDirectory,
                textureName,
                extension,
                payload,
                textureChild,
                plan,
                texturePaths);
            emittedTextureAssets.Add(textureRef.ImageIndex, textureAsset);
            emitted.Add(CreateEmittedTextureRef(slotName, slot, textureRef, textureAsset));
            return;
        }

        if (!string.IsNullOrWhiteSpace(textureRef.Uri))
        {
            var sourceTexturePath = ResolveExternalTexturePath(sourcePath, textureRef.Uri);
            if (!File.Exists(sourceTexturePath))
            {
                warnings.Add($"images/{textureRef.ImageIndex}.uri resolves to missing texture '{sourceTexturePath}'; {slotName} texture was not emitted.");
                return;
            }

            var extension = Path.GetExtension(sourceTexturePath);
            if (!TryGetTextureSourceFormat(extension, out var sourceFormat))
            {
                warnings.Add($"images/{textureRef.ImageIndex}.uri '{textureRef.Uri}' is '{extension}', but generated texture emission supports .ppm, .png, .jpg, and .jpeg only.");
                return;
            }

            var texturePath = BuildTexturePath(textureDirectory, textureName, extension);
            Directory.CreateDirectory(Path.GetDirectoryName(texturePath)!);
            File.Copy(sourceTexturePath, texturePath, overwrite: true);
            WriteMetadata(texturePath + ".meta", textureChild.Metadata, plan.SourceGuid);
            texturePaths.Add(texturePath);
            var textureAsset = new EmittedTextureAsset(
                textureChild.Metadata.Guid,
                textureName,
                sourceFormat);
            emittedTextureAssets.Add(textureRef.ImageIndex, textureAsset);
            emitted.Add(CreateEmittedTextureRef(slotName, slot, textureRef, textureAsset));
            return;
        }

        if (textureRef.BufferView >= 0)
        {
            if (!TryGetImageExtensionFromMimeType(textureRef.MimeType, out var extension, out var mimeWarning))
            {
                warnings.Add($"images/{textureRef.ImageIndex}.mimeType was not emitted for {slotName}: {mimeWarning}");
                return;
            }

            try
            {
                using var source = LoadGltfSourceData(sourcePath);
                var payload = ExtractGltfBufferViewPayload(sourcePath, source.Root, source.BinaryChunk, textureRef.BufferView);
                var textureAsset = EmitTexturePayload(
                    textureDirectory,
                    textureName,
                    extension,
                    payload,
                    textureChild,
                    plan,
                    texturePaths);
                emittedTextureAssets.Add(textureRef.ImageIndex, textureAsset);
                emitted.Add(CreateEmittedTextureRef(slotName, slot, textureRef, textureAsset));
            }
            catch (Exception ex)
            {
                warnings.Add($"images/{textureRef.ImageIndex}.bufferView '{textureRef.BufferView}' was not emitted for {slotName}: {ex.Message}");
            }
            return;
        }

        warnings.Add($"images/{textureRef.ImageIndex} has no supported uri or bufferView; {slotName} texture was not emitted.");
    }

    private static EmittedTextureAsset EmitTexturePayload(
        string textureDirectory,
        string textureName,
        string extension,
        byte[] payload,
        GltfGeneratedChildAsset textureChild,
        GltfModelImportPlan plan,
        List<string> texturePaths)
    {
        if (!TryGetTextureSourceFormat(extension, out var sourceFormat))
        {
            throw new NotSupportedException($"Generated texture extension '{extension}' is not supported.");
        }

        var texturePath = BuildTexturePath(textureDirectory, textureName, extension);
        Directory.CreateDirectory(Path.GetDirectoryName(texturePath)!);
        File.WriteAllBytes(texturePath, payload);
        WriteMetadata(texturePath + ".meta", textureChild.Metadata, plan.SourceGuid);
        texturePaths.Add(texturePath);
        return new EmittedTextureAsset(textureChild.Metadata.Guid, textureName, sourceFormat);
    }

    private static EmittedTextureRef CreateEmittedTextureRef(
        string slotName,
        uint slot,
        GltfImportedTextureRef textureRef,
        EmittedTextureAsset textureAsset)
    {
        return new EmittedTextureRef(
            slotName,
            slot,
            textureAsset.Guid,
            textureAsset.AssetName,
            textureAsset.SourceFormat,
            ResolveGeneratedTextureColorSpace(slotName),
            textureRef.Sampler,
            textureRef.Transform);
    }

    private static string BuildTexturePath(string textureDirectory, string textureName, string extension)
    {
        return Path.Combine(textureDirectory, $"{SanitizePathSegment(textureName)}{extension.ToLowerInvariant()}");
    }

    private static Texture2DColorSpace ResolveGeneratedTextureColorSpace(string slotName)
    {
        return string.Equals(slotName, MaterialTextureSlots.Normal, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(slotName, MaterialTextureSlots.MetallicRoughness, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(slotName, MaterialTextureSlots.Occlusion, StringComparison.OrdinalIgnoreCase)
            ? Texture2DColorSpace.Linear
            : Texture2DColorSpace.SRgb;
    }

    private static string BuildMaterialSource(
        GltfImportedMaterial material,
        IReadOnlyList<EmittedTextureRef> emittedTextures,
        GltfModelImportEmissionSettings settings)
    {
        var name = string.IsNullOrWhiteSpace(material.Name) ? material.Guid.ToString("N") : material.Name;
        var builder = new StringBuilder();
        builder.AppendLine(CultureInfo.InvariantCulture, $"Name: {EscapeScalar(name)}");
        builder.AppendLine("Shader:");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Guid: {settings.ShaderGuid:D}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Name: {EscapeScalar(settings.ShaderName)}");
        bool usesNormalMap = false;
        for (int textureIndex = 0; textureIndex < emittedTextures.Count; textureIndex++)
        {
            if (string.Equals(
                    emittedTextures[textureIndex].Name,
                    MaterialTextureSlots.Normal,
                    StringComparison.OrdinalIgnoreCase))
            {
                usesNormalMap = true;
                break;
            }
        }

        if (usesNormalMap || material.AlphaMode == GltfMaterialAlphaMode.Mask)
        {
            builder.AppendLine("  Keywords:");
            if (usesNormalMap)
            {
                builder.AppendLine("  - USE_NORMAL_MAP");
            }

            if (material.AlphaMode == GltfMaterialAlphaMode.Mask)
            {
                builder.AppendLine("  - ALPHA_TEST");
            }
        }

        builder.AppendLine("  Variant:");
        builder.AppendLine("    Backend: Vulkan");
        builder.AppendLine("    TargetEnvironment: vulkan1.3");
        builder.AppendLine("    ShaderModel: 6_4");
        builder.AppendLine("    OptimizationLevel: 0");
        builder.AppendLine("    DebugInfo: true");

        if (emittedTextures.Count > 0)
        {
            builder.AppendLine("Texture2DRefs:");
            foreach (var texture in emittedTextures)
            {
                builder.AppendLine(CultureInfo.InvariantCulture, $"- Name: {EscapeScalar(texture.Name)}");
                builder.AppendLine(CultureInfo.InvariantCulture, $"  Slot: {texture.Slot}");
                builder.AppendLine("  Texture:");
                builder.AppendLine(CultureInfo.InvariantCulture, $"    Guid: {texture.Guid:D}");
                builder.AppendLine(CultureInfo.InvariantCulture, $"    Name: {EscapeScalar(texture.AssetName)}");
                builder.AppendLine("    Variant:");
                builder.AppendLine("      Format: R8G8B8A8UNorm");
                builder.AppendLine(CultureInfo.InvariantCulture, $"      ColorSpace: {texture.ColorSpace}");
                builder.AppendLine("      GenerateMipMaps: false");
                builder.AppendLine(CultureInfo.InvariantCulture, $"    SourceFormat: {texture.SourceFormat}");
                builder.AppendLine("  Sampler:");
                builder.AppendLine(CultureInfo.InvariantCulture, $"    MinFilter: {texture.Sampler.MinFilter}");
                builder.AppendLine(CultureInfo.InvariantCulture, $"    MagFilter: {texture.Sampler.MagFilter}");
                builder.AppendLine(CultureInfo.InvariantCulture, $"    MipmapMode: {texture.Sampler.MipmapMode}");
                builder.AppendLine(CultureInfo.InvariantCulture, $"    WrapU: {texture.Sampler.WrapU}");
                builder.AppendLine(CultureInfo.InvariantCulture, $"    WrapV: {texture.Sampler.WrapV}");
                builder.AppendLine("  Transform:");
                builder.AppendLine("    Offset:");
                builder.AppendLine(CultureInfo.InvariantCulture, $"      X: {FormatFloat(texture.Transform.Offset.X)}");
                builder.AppendLine(CultureInfo.InvariantCulture, $"      Y: {FormatFloat(texture.Transform.Offset.Y)}");
                builder.AppendLine("    Scale:");
                builder.AppendLine(CultureInfo.InvariantCulture, $"      X: {FormatFloat(texture.Transform.Scale.X)}");
                builder.AppendLine(CultureInfo.InvariantCulture, $"      Y: {FormatFloat(texture.Transform.Scale.Y)}");
                builder.AppendLine(CultureInfo.InvariantCulture, $"    Rotation: {FormatFloat(texture.Transform.Rotation)}");
                builder.AppendLine(CultureInfo.InvariantCulture, $"    TexCoord: {texture.Transform.TexCoord}");
            }
        }

        builder.AppendLine("ScalarProperties:");
        builder.AppendLine("- Name: MetallicFactor");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Value: {FormatFloat(material.MetallicFactor)}");
        builder.AppendLine("- Name: RoughnessFactor");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Value: {FormatFloat(material.RoughnessFactor)}");
        builder.AppendLine("- Name: OcclusionStrength");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Value: {FormatFloat(material.OcclusionStrength)}");
        builder.AppendLine("- Name: AlphaCutoff");
        builder.AppendLine(CultureInfo.InvariantCulture, $"  Value: {FormatFloat(material.AlphaCutoff)}");
        builder.AppendLine("Vector4Properties:");
        builder.AppendLine("- Name: BaseColorFactor");
        builder.AppendLine("  Value:");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    X: {FormatFloat(material.BaseColorFactor.X)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    Y: {FormatFloat(material.BaseColorFactor.Y)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    Z: {FormatFloat(material.BaseColorFactor.Z)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    W: {FormatFloat(material.BaseColorFactor.W)}");
        builder.AppendLine("- Name: EmissiveFactor");
        builder.AppendLine("  Value:");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    X: {FormatFloat(material.EmissiveFactor.X)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    Y: {FormatFloat(material.EmissiveFactor.Y)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    Z: {FormatFloat(material.EmissiveFactor.Z)}");
        builder.AppendLine(CultureInfo.InvariantCulture, $"    W: {FormatFloat(material.EmissiveFactor.W)}");
        if (material.AlphaMode == GltfMaterialAlphaMode.Blend)
        {
            builder.AppendLine("RenderState:");
            builder.AppendLine("  CullMode: Back");
            builder.AppendLine("  FrontFace: CounterClockwise");
            builder.AppendLine("  Blend:");
            builder.AppendLine("    Enabled: true");
            builder.AppendLine("    SrcColor: SrcAlpha");
            builder.AppendLine("    DstColor: OneMinusSrcAlpha");
            builder.AppendLine("    ColorOp: Add");
        }

        return builder.ToString();
    }

    private static GltfGeneratedChildAsset FindChild(GltfModelImportPlan plan, string kind, string key)
    {
        for (int i = 0; i < plan.GeneratedChildren.Count; i++)
        {
            var child = plan.GeneratedChildren[i];
            if (string.Equals(child.Kind, kind, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(child.Key, key, StringComparison.Ordinal))
            {
                return child;
            }
        }

        throw new InvalidOperationException($"[GltfModelImportEmitter] Import plan is missing generated child '{kind}:{key}'.");
    }

    private static void WriteMetadata(string metaPath, AssetMetadata metadata, Guid expectedSourceGuid)
    {
        if (File.Exists(metaPath))
        {
            var existing = SerializationUtil.Deserialize<AssetMetadata>(metaPath, serializeIfNotExist: false);
            if (existing.Generated == null)
            {
                throw new InvalidOperationException(
                    $"[GltfModelImportEmitter] Refusing to overwrite non-generated metadata '{metaPath}'.");
            }

            var generated = metadata.Generated;
            if (generated == null)
            {
                throw new InvalidOperationException(
                    $"[GltfModelImportEmitter] Refusing to overwrite generated metadata '{metaPath}' with metadata that has no generated provenance.");
            }

            if (existing.Generated.SourceGuid != expectedSourceGuid)
            {
                throw new InvalidOperationException(
                    $"[GltfModelImportEmitter] Refusing to overwrite generated metadata '{metaPath}' from source '{existing.Generated.SourceGuid}'.");
            }

            if (!string.Equals(existing.Generated.ChildKind, generated.ChildKind, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.Generated.ChildKey, generated.ChildKey, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"[GltfModelImportEmitter] Refusing to overwrite generated metadata '{metaPath}' for child '{existing.Generated.ChildKind}:{existing.Generated.ChildKey}' with '{generated.ChildKind}:{generated.ChildKey}'.");
            }
        }

        SerializationUtil.Serialize(metadata, metaPath);
    }

    private static string ResolveExternalTexturePath(string sourcePath, string uri)
    {
        if (uri.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var decoded = Uri.UnescapeDataString(uri);
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, decoded.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static bool TryDecodeGltfImageDataUri(string uri, out byte[] payload, out string extension, out string warning)
    {
        payload = Array.Empty<byte>();
        extension = string.Empty;
        warning = string.Empty;

        var comma = uri.IndexOf(',');
        if (comma < 0)
        {
            warning = "data URI has no payload separator.";
            return false;
        }

        var header = uri[..comma];
        if (!header.Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            warning = "data URI must be base64 encoded.";
            return false;
        }

        var mimeType = header.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            ? header[5..].Split(';', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
            : null;
        if (!TryGetImageExtensionFromMimeType(mimeType, out extension, out warning))
        {
            return false;
        }

        payload = Convert.FromBase64String(uri[(comma + 1)..]);
        return true;
    }

    private static bool TryGetImageExtensionFromMimeType(string? mimeType, out string extension, out string warning)
    {
        extension = string.Empty;
        warning = string.Empty;

        if (string.Equals(mimeType, "image/png", StringComparison.OrdinalIgnoreCase))
        {
            extension = ".png";
            return true;
        }

        if (string.Equals(mimeType, "image/jpeg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(mimeType, "image/jpg", StringComparison.OrdinalIgnoreCase))
        {
            extension = ".jpg";
            return true;
        }

        warning = string.IsNullOrWhiteSpace(mimeType)
            ? "missing image MIME type."
            : $"unsupported image MIME type '{mimeType}'.";
        return false;
    }

    private static GltfSourceData LoadGltfSourceData(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath);
        if (!string.Equals(extension, ".glb", StringComparison.OrdinalIgnoreCase))
        {
            return new GltfSourceData(JsonDocument.Parse(File.ReadAllText(sourcePath)), null);
        }

        var bytes = File.ReadAllBytes(sourcePath);
        if (bytes.Length < GltfBinaryHeaderSize)
        {
            throw new InvalidOperationException($"GLB source '{sourcePath}' is smaller than the GLB header.");
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, sizeof(uint)));
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, sizeof(uint)));
        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, sizeof(uint)));
        if (magic != GltfBinaryMagic)
        {
            throw new InvalidOperationException($"GLB source '{sourcePath}' has invalid magic.");
        }

        if (version != 2)
        {
            throw new NotSupportedException($"GLB source '{sourcePath}' uses version '{version}', expected 2.");
        }

        if (declaredLength > bytes.Length)
        {
            throw new InvalidOperationException($"GLB source '{sourcePath}' declares length '{declaredLength}' but file has only '{bytes.Length}' bytes.");
        }

        ReadOnlyMemory<byte> jsonChunk = ReadOnlyMemory<byte>.Empty;
        byte[]? binaryChunk = null;
        var offset = GltfBinaryHeaderSize;
        while (offset < declaredLength)
        {
            if (offset + GltfBinaryChunkHeaderSize > declaredLength)
            {
                throw new InvalidOperationException($"GLB source '{sourcePath}' has a truncated chunk header.");
            }

            var rawChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)));
            if (rawChunkLength > int.MaxValue)
            {
                throw new InvalidOperationException($"GLB source '{sourcePath}' has a chunk larger than the supported importer range.");
            }

            var chunkLength = (int)rawChunkLength;
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + sizeof(uint), sizeof(uint)));
            offset += GltfBinaryChunkHeaderSize;
            if (offset + chunkLength > declaredLength)
            {
                throw new InvalidOperationException($"GLB source '{sourcePath}' has a truncated chunk payload.");
            }

            if (chunkType == GltfBinaryJsonChunkType)
            {
                jsonChunk = bytes.AsMemory(offset, chunkLength);
            }
            else if (chunkType == GltfBinaryBinChunkType)
            {
                binaryChunk = bytes.AsSpan(offset, chunkLength).ToArray();
            }

            offset += chunkLength;
        }

        if (jsonChunk.IsEmpty)
        {
            throw new InvalidOperationException($"GLB source '{sourcePath}' contains no JSON chunk.");
        }

        return new GltfSourceData(JsonDocument.Parse(jsonChunk), binaryChunk);
    }

    private static byte[] ExtractGltfBufferViewPayload(
        string sourcePath,
        JsonElement root,
        byte[]? embeddedBinaryBuffer,
        int bufferViewIndex)
    {
        var buffers = LoadGltfBuffers(sourcePath, root, embeddedBinaryBuffer);
        var bufferViews = ReadGltfBufferViews(sourcePath, root);
        if (bufferViewIndex < 0 || bufferViewIndex >= bufferViews.Length)
        {
            throw new InvalidOperationException($"glTF bufferView index '{bufferViewIndex}' is outside bufferView count '{bufferViews.Length}'.");
        }

        var bufferView = bufferViews[bufferViewIndex];
        if (bufferView.Buffer < 0 || bufferView.Buffer >= buffers.Length)
        {
            throw new InvalidOperationException($"glTF bufferView '{bufferViewIndex}' buffer '{bufferView.Buffer}' is outside buffer count '{buffers.Length}'.");
        }

        var buffer = buffers[bufferView.Buffer];
        if (bufferView.ByteOffset < 0 ||
            bufferView.ByteLength < 0 ||
            bufferView.ByteOffset + bufferView.ByteLength > buffer.Length)
        {
            throw new InvalidOperationException($"glTF bufferView '{bufferViewIndex}' range is outside buffer length '{buffer.Length}'.");
        }

        var payload = new byte[bufferView.ByteLength];
        Buffer.BlockCopy(buffer, bufferView.ByteOffset, payload, 0, payload.Length);
        return payload;
    }

    private static byte[][] LoadGltfBuffers(string sourcePath, JsonElement root, byte[]? embeddedBinaryBuffer)
    {
        if (!root.TryGetProperty("buffers", out var buffersElement) || buffersElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"glTF source '{sourcePath}' contains no buffers.");
        }

        var buffers = new byte[buffersElement.GetArrayLength()][];
        for (int i = 0; i < buffers.Length; i++)
        {
            var buffer = buffersElement[i];
            var context = $"buffers[{i}]";
            if (!buffer.TryGetProperty("uri", out var uriElement))
            {
                if (i != 0)
                {
                    throw new NotSupportedException($"glTF source '{sourcePath}' {context} has no uri, but only buffers[0] may use a GLB BIN chunk.");
                }

                buffers[i] = GetGltfEmbeddedBinaryBuffer(
                    sourcePath,
                    embeddedBinaryBuffer,
                    GetRequiredInt32(sourcePath, buffer, "byteLength", context),
                    context);
                continue;
            }

            var uri = uriElement.GetString();
            if (string.IsNullOrWhiteSpace(uri))
            {
                throw new InvalidOperationException($"glTF source '{sourcePath}' {context}.uri is empty.");
            }

            buffers[i] = uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? DecodeGltfDataUri(sourcePath, uri)
                : LoadGltfExternalBuffer(sourcePath, uri, context);
        }

        return buffers;
    }

    private static byte[] GetGltfEmbeddedBinaryBuffer(string sourcePath, byte[]? embeddedBinaryBuffer, int byteLength, string context)
    {
        if (embeddedBinaryBuffer == null)
        {
            throw new NotSupportedException($"glTF source '{sourcePath}' {context} has no uri and no GLB BIN chunk was found.");
        }

        if (byteLength < 0 || byteLength > embeddedBinaryBuffer.Length)
        {
            throw new InvalidOperationException($"GLB source '{sourcePath}' {context}.byteLength '{byteLength}' exceeds BIN chunk length '{embeddedBinaryBuffer.Length}'.");
        }

        var buffer = new byte[byteLength];
        Buffer.BlockCopy(embeddedBinaryBuffer, 0, buffer, 0, byteLength);
        return buffer;
    }

    private static GltfBufferView[] ReadGltfBufferViews(string sourcePath, JsonElement root)
    {
        if (!root.TryGetProperty("bufferViews", out var bufferViewsElement) || bufferViewsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"glTF source '{sourcePath}' contains no bufferViews.");
        }

        var bufferViews = new GltfBufferView[bufferViewsElement.GetArrayLength()];
        for (int i = 0; i < bufferViews.Length; i++)
        {
            var bufferView = bufferViewsElement[i];
            var context = $"bufferViews[{i}]";
            bufferViews[i] = new GltfBufferView(
                GetRequiredInt32(sourcePath, bufferView, "buffer", context),
                GetOptionalInt32(bufferView, "byteOffset"),
                GetRequiredInt32(sourcePath, bufferView, "byteLength", context));
        }

        return bufferViews;
    }

    private static byte[] LoadGltfExternalBuffer(string sourcePath, string uri, string context)
    {
        var bufferPath = ResolveExternalTexturePath(sourcePath, uri);
        if (!File.Exists(bufferPath))
        {
            throw new FileNotFoundException(
                $"glTF source '{sourcePath}' {context}.uri resolves to missing external buffer '{bufferPath}'.",
                bufferPath);
        }

        return File.ReadAllBytes(bufferPath);
    }

    private static byte[] DecodeGltfDataUri(string sourcePath, string uri)
    {
        var comma = uri.IndexOf(',');
        if (comma < 0 || !uri[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"glTF data URI in '{sourcePath}' must be base64 encoded.");
        }

        return Convert.FromBase64String(uri[(comma + 1)..]);
    }

    private static int GetRequiredInt32(string sourcePath, JsonElement element, string propertyName, string context)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidOperationException($"glTF source '{sourcePath}' is missing required integer property '{context}.{propertyName}'.");
        }

        return property.GetInt32();
    }

    private static int GetOptionalInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.GetInt32()
            : 0;
    }

    private static string SanitizePathSegment(string value)
    {
        var trimmed = string.IsNullOrWhiteSpace(value) ? "Unnamed" : value.Trim();
        var invalid = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(trimmed.Length);
        foreach (var ch in trimmed)
        {
            builder.Append(invalid.Contains(ch) || ch == '/' || ch == '\\' ? '_' : ch);
        }

        return builder.Length == 0 ? "Unnamed" : builder.ToString();
    }

    private static bool TryGetTextureSourceFormat(string extension, out Texture2DSourceFormat sourceFormat)
    {
        if (string.Equals(extension, ".ppm", StringComparison.OrdinalIgnoreCase))
        {
            sourceFormat = Texture2DSourceFormat.PpmP3;
            return true;
        }

        if (string.Equals(extension, ".png", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".jpg", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(extension, ".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            sourceFormat = Texture2DSourceFormat.ImageFile;
            return true;
        }

        sourceFormat = default;
        return false;
    }

    private static string FormatFloat(float value)
    {
        return value.ToString("0.########", CultureInfo.InvariantCulture);
    }

    private static string EscapeScalar(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "\"\"";
        }

        return value.Contains(':', StringComparison.Ordinal) ||
               value.Contains('#', StringComparison.Ordinal) ||
               value.StartsWith(" ", StringComparison.Ordinal) ||
               value.EndsWith(" ", StringComparison.Ordinal)
            ? $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : value;
    }

    private readonly record struct EmittedSceneEntity(
        string Name,
        Vector3 Position,
        Quaternion Rotation,
        Vector3 Scale,
        Guid MeshGuid,
        Guid MaterialGuid,
        int FirstSubmeshIndex,
        int SubmeshCount);

    private readonly record struct EmittedTextureRef(
        string Name,
        uint Slot,
        Guid Guid,
        string AssetName,
        Texture2DSourceFormat SourceFormat,
        Texture2DColorSpace ColorSpace,
        MaterialTextureSamplerSettings Sampler,
        MaterialTextureTransform Transform);

    private readonly record struct EmittedTextureAsset(
        Guid Guid,
        string AssetName,
        Texture2DSourceFormat SourceFormat);

    private sealed class GltfSourceData : IDisposable
    {
        public GltfSourceData(JsonDocument document, byte[]? binaryChunk)
        {
            Document = document;
            BinaryChunk = binaryChunk;
        }

        public JsonDocument Document { get; }
        public byte[]? BinaryChunk { get; }
        public JsonElement Root => Document.RootElement;

        public void Dispose()
        {
            Document.Dispose();
        }
    }

    private readonly record struct GltfBufferView(
        int Buffer,
        int ByteOffset,
        int ByteLength);
}
