using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;

namespace ArisenEngine.Rendering.Resources;

public enum MeshSourceFormat
{
    ArisenTextMesh,
    WavefrontObj,
    GltfJson,
    GltfBinary
}

public enum MeshIndexFormat
{
    UInt32
}

public readonly record struct MeshVariantKey(MeshIndexFormat IndexFormat)
{
    public static MeshVariantKey Default { get; } = new(MeshIndexFormat.UInt32);

    public string GetCookedVariant()
    {
        return $"staticmesh.{IndexFormat.ToString().ToLowerInvariant()}";
    }
}

public sealed record MeshAsset(
    Guid Guid,
    string Name,
    MeshVariantKey Variant,
    MeshSourceFormat SourceFormat = MeshSourceFormat.ArisenTextMesh);

public readonly record struct MeshBounds(Vector3 Min, Vector3 Max)
{
    public static MeshBounds Empty => new(Vector3.Zero, Vector3.Zero);
}

public readonly record struct MeshSubmesh(
    uint FirstIndex,
    uint IndexCount,
    int VertexOffset,
    uint MaterialSlot);

public readonly record struct CookedMesh(
    MeshAsset Asset,
    string Variant,
    uint VertexCount,
    uint VertexStride,
    uint VertexDataOffset,
    uint VertexDataSize,
    uint IndexCount,
    MeshIndexFormat IndexFormat,
    uint IndexDataOffset,
    uint IndexDataSize,
    MeshBounds Bounds,
    uint SubmeshCount,
    uint SubmeshDataOffset,
    uint SubmeshDataSize,
    CookedAssetHandle Handle)
{
    public bool IsValid => Handle.IsValid;
}

public static class MeshAssetCooker
{
    public const uint StaticMeshVertexStride = 60;
    public const uint SubmeshStride = 16;

    private const string MeshAssetType = "Mesh";
    private const uint GltfBinaryMagic = 0x46546C67;
    private const uint GltfBinaryJsonChunkType = 0x4E4F534A;
    private const uint GltfBinaryBinChunkType = 0x004E4942;
    private const int GltfBinaryHeaderSize = 12;
    private const int GltfBinaryChunkHeaderSize = 8;
    private const int CurrentCookedVersion = 4;
    private const int HeaderSize = 80;
    private static readonly Vector3 s_DefaultNormal = new(0.0f, 0.0f, 1.0f);
    private static readonly Vector4 s_DefaultTangent = new(1.0f, 0.0f, 0.0f, 1.0f);
    private static readonly byte[] s_Magic = Encoding.ASCII.GetBytes("ARISMESH");

    public static CookedMesh LoadOrCook(IAssetDatabase assetDatabase, MeshAsset mesh)
    {
        if (assetDatabase == null)
        {
            throw new ArgumentNullException(nameof(assetDatabase));
        }

        if (mesh == null)
        {
            throw new ArgumentNullException(nameof(mesh));
        }

        if (!assetDatabase.TryGetAsset(mesh.Guid, out var sourceAsset))
        {
            throw new InvalidOperationException($"[MeshAssetCooker] Mesh asset '{mesh.Guid}' was not found.");
        }

        if (!string.Equals(sourceAsset.AssetType, MeshAssetType, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"[MeshAssetCooker] Mesh asset '{mesh.Guid}' has asset type '{sourceAsset.AssetType}', expected '{MeshAssetType}'.");
        }

        var variant = mesh.Variant.GetCookedVariant();
        var outputPath = assetDatabase.GetCookedArtifactPath(mesh.Guid, variant, ".mesh");
        var sourceWriteTimeUtc = GetSourceDependencyWriteTimeUtc(sourceAsset.SourcePath, mesh.SourceFormat);

        if (!assetDatabase.TryGetCookedArtifact(mesh.Guid, variant, out _) ||
            !File.Exists(outputPath) ||
            File.GetLastWriteTimeUtc(outputPath) < sourceWriteTimeUtc ||
            !IsCurrentCookedMesh(outputPath))
        {
            CookMesh(sourceAsset, mesh, variant, outputPath);
        }

        var outputInfo = new FileInfo(outputPath);
        if (!outputInfo.Exists || outputInfo.Length <= HeaderSize)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] Mesh asset '{mesh.Guid}' produced no cooked payload.");
        }

        assetDatabase.RegisterCookedArtifact(new CookedAssetRecord(
            mesh.Guid,
            sourceAsset.AssetType,
            variant,
            outputInfo.FullName,
            outputInfo.Length,
            outputInfo.LastWriteTimeUtc));

        if (!assetDatabase.TryLoadCookedAsset(mesh.Guid, variant, MeshAssetType, out var handle))
        {
            throw new InvalidOperationException($"[MeshAssetCooker] Failed to load cooked mesh asset '{mesh.Guid}'.");
        }

        try
        {
            var bytes = assetDatabase.GetCookedAssetBytes(handle);
            var header = ReadHeader(bytes.Span);
            return new CookedMesh(
                mesh,
                variant,
                header.VertexCount,
                header.VertexStride,
                HeaderSize,
                header.VertexDataSize,
                header.IndexCount,
                header.IndexFormat,
                HeaderSize + header.VertexDataSize,
                header.IndexDataSize,
                header.Bounds,
                header.SubmeshCount,
                HeaderSize + header.VertexDataSize + header.IndexDataSize,
                header.SubmeshDataSize,
                handle);
        }
        catch
        {
            assetDatabase.Release(handle);
            throw;
        }
    }

    private static void CookMesh(AssetRecord sourceAsset, MeshAsset mesh, string variant, string outputPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        var source = mesh.SourceFormat switch
        {
            MeshSourceFormat.ArisenTextMesh => ReadArisenTextMesh(sourceAsset.SourcePath),
            MeshSourceFormat.WavefrontObj => ReadWavefrontObj(sourceAsset.SourcePath),
            MeshSourceFormat.GltfJson => ReadGltfJson(sourceAsset.SourcePath),
            MeshSourceFormat.GltfBinary => ReadGltfBinary(sourceAsset.SourcePath),
            _ => throw new NotSupportedException($"Mesh source format '{mesh.SourceFormat}' is not implemented yet.")
        };

        var bounds = ComputeBounds(source.Vertices);
        var submeshes = source.Submeshes.Length > 0
            ? source.Submeshes
            : new[] { new SourceSubmesh(0, checked((uint)source.Indices.Length), 0, 0) };
        var vertexDataSize = checked(source.Vertices.Length * (int)StaticMeshVertexStride);
        var indexDataSize = checked(source.Indices.Length * sizeof(uint));
        var submeshDataSize = checked(submeshes.Length * (int)SubmeshStride);

        using var stream = File.Create(outputPath);
        stream.Write(s_Magic);
        WriteInt32(stream, CurrentCookedVersion);
        WriteInt32(stream, source.Vertices.Length);
        WriteInt32(stream, checked((int)StaticMeshVertexStride));
        WriteInt32(stream, vertexDataSize);
        WriteInt32(stream, source.Indices.Length);
        WriteInt32(stream, (int)mesh.Variant.IndexFormat);
        WriteInt32(stream, indexDataSize);
        WriteInt32(stream, submeshes.Length);
        WriteInt32(stream, submeshDataSize);
        WriteSingle(stream, bounds.Min.X);
        WriteSingle(stream, bounds.Min.Y);
        WriteSingle(stream, bounds.Min.Z);
        WriteSingle(stream, bounds.Max.X);
        WriteSingle(stream, bounds.Max.Y);
        WriteSingle(stream, bounds.Max.Z);
        WriteInt32(stream, 0);
        WriteInt32(stream, 0);
        WriteInt32(stream, 0);

        Span<byte> vertexBytes = stackalloc byte[(int)StaticMeshVertexStride];
        foreach (var vertex in source.Vertices)
        {
            WriteSingle(vertexBytes, 0, vertex.PositionX);
            WriteSingle(vertexBytes, 4, vertex.PositionY);
            WriteSingle(vertexBytes, 8, vertex.PositionZ);
            WriteSingle(vertexBytes, 12, vertex.NormalX);
            WriteSingle(vertexBytes, 16, vertex.NormalY);
            WriteSingle(vertexBytes, 20, vertex.NormalZ);
            WriteSingle(vertexBytes, 24, vertex.TangentX);
            WriteSingle(vertexBytes, 28, vertex.TangentY);
            WriteSingle(vertexBytes, 32, vertex.TangentZ);
            WriteSingle(vertexBytes, 36, vertex.TangentW);
            WriteSingle(vertexBytes, 40, vertex.U);
            WriteSingle(vertexBytes, 44, vertex.V);
            WriteSingle(vertexBytes, 48, vertex.ColorR);
            WriteSingle(vertexBytes, 52, vertex.ColorG);
            WriteSingle(vertexBytes, 56, vertex.ColorB);
            stream.Write(vertexBytes);
        }

        Span<byte> indexBytes = stackalloc byte[sizeof(uint)];
        foreach (var index in source.Indices)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(indexBytes, index);
            stream.Write(indexBytes);
        }

        Span<byte> submeshBytes = stackalloc byte[(int)SubmeshStride];
        foreach (var submesh in submeshes)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(submeshBytes.Slice(0, sizeof(uint)), submesh.FirstIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(submeshBytes.Slice(4, sizeof(uint)), submesh.IndexCount);
            BinaryPrimitives.WriteInt32LittleEndian(submeshBytes.Slice(8, sizeof(int)), submesh.VertexOffset);
            BinaryPrimitives.WriteUInt32LittleEndian(submeshBytes.Slice(12, sizeof(uint)), submesh.MaterialSlot);
            stream.Write(submeshBytes);
        }

        Logger.Log(
            $"[MeshAssetCooker] Cooked mesh asset {mesh.Guid} | Vertices: {source.Vertices.Length} | Indices: {source.Indices.Length} | Submeshes: {submeshes.Length} | Bounds: {bounds.Min}->{bounds.Max} | Variant: {variant} | Output: {outputPath}");
    }

    private static CookedMeshHeader ReadHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes.Slice(0, s_Magic.Length).SequenceEqual(s_Magic))
        {
            throw new InvalidOperationException("[MeshAssetCooker] Cooked mesh header magic is invalid.");
        }

        var version = ReadInt32(bytes, 8);
        if (version != CurrentCookedVersion)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] Cooked mesh version '{version}' is not supported.");
        }

        var vertexCount = ReadInt32(bytes, 12);
        var vertexStride = ReadInt32(bytes, 16);
        var vertexDataSize = ReadInt32(bytes, 20);
        var indexCount = ReadInt32(bytes, 24);
        var indexFormat = (MeshIndexFormat)ReadInt32(bytes, 28);
        var indexDataSize = ReadInt32(bytes, 32);
        var submeshCount = ReadInt32(bytes, 36);
        var submeshDataSize = ReadInt32(bytes, 40);
        var bounds = new MeshBounds(
            new Vector3(
                ReadSingle(bytes, 44),
                ReadSingle(bytes, 48),
                ReadSingle(bytes, 52)),
            new Vector3(
                ReadSingle(bytes, 56),
                ReadSingle(bytes, 60),
                ReadSingle(bytes, 64)));

        if (vertexCount <= 0 ||
            vertexStride <= 0 ||
            vertexDataSize <= 0 ||
            indexCount <= 0 ||
            indexDataSize <= 0 ||
            submeshCount <= 0 ||
            submeshDataSize <= 0)
        {
            throw new InvalidOperationException("[MeshAssetCooker] Cooked mesh header contains invalid counts or payload sizes.");
        }

        if (vertexStride != StaticMeshVertexStride)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] Cooked mesh vertex stride '{vertexStride}' is not supported.");
        }

        if (indexFormat != MeshIndexFormat.UInt32)
        {
            throw new NotSupportedException($"Mesh index format '{indexFormat}' is not supported by the RHI mesh uploader yet.");
        }

        if (submeshDataSize != checked(submeshCount * (int)SubmeshStride))
        {
            throw new InvalidOperationException("[MeshAssetCooker] Cooked mesh submesh payload size does not match submesh count.");
        }

        if (HeaderSize + vertexDataSize + indexDataSize + submeshDataSize > bytes.Length)
        {
            throw new InvalidOperationException("[MeshAssetCooker] Cooked mesh payload is truncated.");
        }

        ValidateSubmeshPayload(
            bytes.Slice(HeaderSize + vertexDataSize + indexDataSize, submeshDataSize),
            checked((uint)indexCount));

        return new CookedMeshHeader(
            checked((uint)vertexCount),
            checked((uint)vertexStride),
            checked((uint)vertexDataSize),
            checked((uint)indexCount),
            indexFormat,
            checked((uint)indexDataSize),
            bounds,
            checked((uint)submeshCount),
            checked((uint)submeshDataSize));
    }

    public static void ReadSubmeshes(ReadOnlySpan<byte> bytes, CookedMesh mesh, Span<MeshSubmesh> destination)
    {
        var submeshCount = checked((int)mesh.SubmeshCount);
        if (destination.Length < submeshCount)
        {
            throw new ArgumentException("[MeshAssetCooker] Destination span is smaller than the cooked submesh count.", nameof(destination));
        }

        var submeshBytes = bytes.Slice(
            checked((int)mesh.SubmeshDataOffset),
            checked((int)mesh.SubmeshDataSize));

        for (int i = 0; i < submeshCount; i++)
        {
            var offset = checked(i * (int)SubmeshStride);
            destination[i] = new MeshSubmesh(
                BinaryPrimitives.ReadUInt32LittleEndian(submeshBytes.Slice(offset, sizeof(uint))),
                BinaryPrimitives.ReadUInt32LittleEndian(submeshBytes.Slice(offset + 4, sizeof(uint))),
                BinaryPrimitives.ReadInt32LittleEndian(submeshBytes.Slice(offset + 8, sizeof(int))),
                BinaryPrimitives.ReadUInt32LittleEndian(submeshBytes.Slice(offset + 12, sizeof(uint))));
        }
    }

    private static bool IsCurrentCookedMesh(string outputPath)
    {
        try
        {
            using var stream = File.OpenRead(outputPath);
            if (stream.Length < 12)
            {
                return false;
            }

            Span<byte> headerPrefix = stackalloc byte[12];
            var read = stream.Read(headerPrefix);
            return read == headerPrefix.Length &&
                   headerPrefix.Slice(0, s_Magic.Length).SequenceEqual(s_Magic) &&
                   ReadInt32(headerPrefix, 8) == CurrentCookedVersion;
        }
        catch
        {
            return false;
        }
    }

    private static void ValidateSubmeshPayload(ReadOnlySpan<byte> submeshBytes, uint indexCount)
    {
        for (int offset = 0; offset < submeshBytes.Length; offset += (int)SubmeshStride)
        {
            var firstIndex = BinaryPrimitives.ReadUInt32LittleEndian(submeshBytes.Slice(offset, sizeof(uint)));
            var submeshIndexCount = BinaryPrimitives.ReadUInt32LittleEndian(submeshBytes.Slice(offset + 4, sizeof(uint)));
            if (submeshIndexCount == 0 ||
                firstIndex >= indexCount ||
                submeshIndexCount > indexCount - firstIndex)
            {
                throw new InvalidOperationException(
                    $"[MeshAssetCooker] Cooked mesh submesh range {firstIndex}+{submeshIndexCount} is outside index count {indexCount}.");
            }
        }
    }

    private static MeshBounds ComputeBounds(SourceVertex[] vertices)
    {
        if (vertices.Length == 0)
        {
            return MeshBounds.Empty;
        }

        var min = new Vector3(vertices[0].PositionX, vertices[0].PositionY, vertices[0].PositionZ);
        var max = min;
        for (int i = 1; i < vertices.Length; i++)
        {
            var position = new Vector3(vertices[i].PositionX, vertices[i].PositionY, vertices[i].PositionZ);
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        return new MeshBounds(min, max);
    }

    private static SourceMesh ReadArisenTextMesh(string sourcePath)
    {
        var vertices = new List<SourceVertex>();
        var indices = new List<uint>();
        var submeshes = new List<SourceSubmesh>();

        foreach (var rawLine in File.ReadLines(sourcePath))
        {
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            if (string.Equals(parts[0], "v", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length != 9 && parts.Length != 12 && parts.Length != 16)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] Mesh vertex line in '{sourcePath}' must contain 8 values (position, uv0, color0), 11 values (position, normal, uv0, color0), or 15 values (position, normal, tangent, uv0, color0).");
                }

                if (parts.Length == 9)
                {
                    vertices.Add(new SourceVertex(
                        ReadFloat(parts[1]),
                        ReadFloat(parts[2]),
                        ReadFloat(parts[3]),
                        s_DefaultNormal.X,
                        s_DefaultNormal.Y,
                        s_DefaultNormal.Z,
                        s_DefaultTangent.X,
                        s_DefaultTangent.Y,
                        s_DefaultTangent.Z,
                        s_DefaultTangent.W,
                        ReadFloat(parts[4]),
                        ReadFloat(parts[5]),
                        ReadFloat(parts[6]),
                        ReadFloat(parts[7]),
                        ReadFloat(parts[8])));
                }
                else if (parts.Length == 12)
                {
                    var normal = NormalizeOrDefault(new Vector3(
                        ReadFloat(parts[4]),
                        ReadFloat(parts[5]),
                        ReadFloat(parts[6])));
                    vertices.Add(new SourceVertex(
                        ReadFloat(parts[1]),
                        ReadFloat(parts[2]),
                        ReadFloat(parts[3]),
                        normal.X,
                        normal.Y,
                        normal.Z,
                        s_DefaultTangent.X,
                        s_DefaultTangent.Y,
                        s_DefaultTangent.Z,
                        s_DefaultTangent.W,
                        ReadFloat(parts[7]),
                        ReadFloat(parts[8]),
                        ReadFloat(parts[9]),
                        ReadFloat(parts[10]),
                        ReadFloat(parts[11])));
                }
                else
                {
                    var normal = NormalizeOrDefault(new Vector3(
                        ReadFloat(parts[4]),
                        ReadFloat(parts[5]),
                        ReadFloat(parts[6])));
                    var tangent = NormalizeTangentOrDefault(new Vector4(
                        ReadFloat(parts[7]),
                        ReadFloat(parts[8]),
                        ReadFloat(parts[9]),
                        ReadFloat(parts[10])));
                    vertices.Add(new SourceVertex(
                        ReadFloat(parts[1]),
                        ReadFloat(parts[2]),
                        ReadFloat(parts[3]),
                        normal.X,
                        normal.Y,
                        normal.Z,
                        tangent.X,
                        tangent.Y,
                        tangent.Z,
                        tangent.W,
                        ReadFloat(parts[11]),
                        ReadFloat(parts[12]),
                        ReadFloat(parts[13]),
                        ReadFloat(parts[14]),
                        ReadFloat(parts[15])));
                }
                continue;
            }

            if (string.Equals(parts[0], "i", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length < 4)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] Mesh index line in '{sourcePath}' must contain at least 3 indices.");
                }

                for (int i = 1; i < parts.Length; i++)
                {
                    if (!uint.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out var index))
                    {
                        throw new InvalidOperationException($"[MeshAssetCooker] Invalid mesh index token '{parts[i]}'.");
                    }

                    indices.Add(index);
                }
                continue;
            }

            if (string.Equals(parts[0], "s", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parts[0], "submesh", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length != 4)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] Mesh submesh line in '{sourcePath}' must contain firstIndex, indexCount, and materialSlot.");
                }

                submeshes.Add(new SourceSubmesh(
                    ReadUInt32(parts[1], "submesh firstIndex"),
                    ReadUInt32(parts[2], "submesh indexCount"),
                    0,
                    ReadUInt32(parts[3], "submesh materialSlot")));
                continue;
            }

            throw new InvalidOperationException($"[MeshAssetCooker] Unknown mesh source token '{parts[0]}' in '{sourcePath}'.");
        }

        if (vertices.Count == 0 || indices.Count == 0)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] Mesh source '{sourcePath}' contains no drawable geometry.");
        }

        foreach (var index in indices)
        {
            if (index >= vertices.Count)
            {
                throw new InvalidOperationException($"[MeshAssetCooker] Mesh source '{sourcePath}' contains index '{index}' outside vertex range '{vertices.Count}'.");
            }
        }

        var sourceSubmeshes = submeshes.Count > 0
            ? submeshes.ToArray()
            : new[] { new SourceSubmesh(0, checked((uint)indices.Count), 0, 0) };
        ValidateSourceSubmeshes(sourcePath, sourceSubmeshes, checked((uint)indices.Count));
        return new SourceMesh(vertices.ToArray(), indices.ToArray(), sourceSubmeshes);
    }

    private static SourceMesh ReadWavefrontObj(string sourcePath)
    {
        var positions = new List<ObjVector3>();
        var texCoords = new List<ObjVector2>();
        var normals = new List<ObjVector3>();
        var vertices = new List<SourceVertex>();
        var indices = new List<uint>();
        var submeshes = new List<SourceSubmesh>();
        var materialSlots = new Dictionary<string, uint>(StringComparer.Ordinal);
        var vertexLookup = new Dictionary<ObjVertexKey, uint>();
        var currentMaterialSlot = 0u;
        var currentSubmeshStart = 0u;

        var lineNumber = 0;
        foreach (var rawLine in File.ReadLines(sourcePath))
        {
            lineNumber++;
            var line = StripComment(rawLine).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            if (string.Equals(parts[0], "v", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length < 4)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] OBJ position at {FormatObjLocation(sourcePath, lineNumber)} must contain 3 numeric values.");
                }

                positions.Add(new ObjVector3(
                    ReadFloat(parts[1]),
                    ReadFloat(parts[2]),
                    ReadFloat(parts[3])));
                continue;
            }

            if (string.Equals(parts[0], "vt", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length < 3)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] OBJ texcoord at {FormatObjLocation(sourcePath, lineNumber)} must contain 2 numeric values.");
                }

                texCoords.Add(new ObjVector2(ReadFloat(parts[1]), ReadFloat(parts[2])));
                continue;
            }

            if (string.Equals(parts[0], "vn", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length < 4)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] OBJ normal at {FormatObjLocation(sourcePath, lineNumber)} must contain 3 numeric values.");
                }

                normals.Add(new ObjVector3(
                    ReadFloat(parts[1]),
                    ReadFloat(parts[2]),
                    ReadFloat(parts[3])));
                continue;
            }

            if (string.Equals(parts[0], "usemtl", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length < 2 || string.IsNullOrWhiteSpace(parts[1]))
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] OBJ usemtl at {FormatObjLocation(sourcePath, lineNumber)} must name a material.");
                }

                FinishObjSubmesh(submeshes, currentSubmeshStart, checked((uint)indices.Count), currentMaterialSlot);
                currentSubmeshStart = checked((uint)indices.Count);

                if (!materialSlots.TryGetValue(parts[1], out currentMaterialSlot))
                {
                    currentMaterialSlot = checked((uint)materialSlots.Count);
                    materialSlots.Add(parts[1], currentMaterialSlot);
                }

                continue;
            }

            if (string.Equals(parts[0], "f", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length < 4)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] OBJ face at {FormatObjLocation(sourcePath, lineNumber)} must contain at least 3 vertices.");
                }

                var faceIndices = new uint[parts.Length - 1];
                for (int i = 1; i < parts.Length; i++)
                {
                    faceIndices[i - 1] = ResolveObjVertex(
                        sourcePath,
                        lineNumber,
                        parts[i],
                        positions,
                        texCoords,
                        normals,
                        vertices,
                        vertexLookup);
                }

                for (int i = 1; i < faceIndices.Length - 1; i++)
                {
                    indices.Add(faceIndices[0]);
                    indices.Add(faceIndices[i]);
                    indices.Add(faceIndices[i + 1]);
                }
            }
        }

        if (vertices.Count == 0 || indices.Count == 0)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] OBJ mesh source '{sourcePath}' contains no drawable geometry.");
        }

        FinishObjSubmesh(submeshes, currentSubmeshStart, checked((uint)indices.Count), currentMaterialSlot);
        var sourceVertices = normals.Count == 0
            ? GenerateSmoothNormals(vertices.ToArray(), indices.ToArray())
            : vertices.ToArray();
        var sourceSubmeshes = submeshes.Count > 0
            ? submeshes.ToArray()
            : new[] { new SourceSubmesh(0, checked((uint)indices.Count), 0, 0) };
        ValidateSourceSubmeshes(sourcePath, sourceSubmeshes, checked((uint)indices.Count));
        return new SourceMesh(sourceVertices, indices.ToArray(), sourceSubmeshes);
    }

    private static SourceMesh ReadGltfJson(string sourcePath)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(sourcePath));
        return ReadGltfDocument(sourcePath, document.RootElement, embeddedBinaryBuffer: null);
    }

    private static SourceMesh ReadGltfBinary(string sourcePath)
    {
        var bytes = File.ReadAllBytes(sourcePath);
        if (bytes.Length < GltfBinaryHeaderSize)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] GLB source '{sourcePath}' is smaller than the GLB header.");
        }

        var magic = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, sizeof(uint)));
        var version = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, sizeof(uint)));
        var declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, sizeof(uint)));
        if (magic != GltfBinaryMagic)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] GLB source '{sourcePath}' has invalid magic.");
        }

        if (version != 2)
        {
            throw new NotSupportedException($"[MeshAssetCooker] GLB source '{sourcePath}' uses version '{version}', expected 2.");
        }

        if (declaredLength > bytes.Length)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] GLB source '{sourcePath}' declares length '{declaredLength}' but file has only '{bytes.Length}' bytes.");
        }

        ReadOnlyMemory<byte> jsonChunk = ReadOnlyMemory<byte>.Empty;
        byte[]? binaryChunk = null;
        var offset = GltfBinaryHeaderSize;
        while (offset < declaredLength)
        {
            if (offset + GltfBinaryChunkHeaderSize > declaredLength)
            {
                throw new InvalidOperationException($"[MeshAssetCooker] GLB source '{sourcePath}' has a truncated chunk header.");
            }

            var rawChunkLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, sizeof(uint)));
            if (rawChunkLength > int.MaxValue)
            {
                throw new InvalidOperationException($"[MeshAssetCooker] GLB source '{sourcePath}' has a chunk larger than the supported importer range.");
            }

            var chunkLength = (int)rawChunkLength;
            var chunkType = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset + sizeof(uint), sizeof(uint)));
            offset += GltfBinaryChunkHeaderSize;
            if (offset + chunkLength > declaredLength)
            {
                throw new InvalidOperationException($"[MeshAssetCooker] GLB source '{sourcePath}' has a truncated chunk payload.");
            }

            var chunk = bytes.AsMemory(offset, chunkLength);
            if (chunkType == GltfBinaryJsonChunkType)
            {
                if (!jsonChunk.IsEmpty)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] GLB source '{sourcePath}' contains multiple JSON chunks.");
                }

                jsonChunk = chunk;
            }
            else if (chunkType == GltfBinaryBinChunkType)
            {
                if (binaryChunk != null)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] GLB source '{sourcePath}' contains multiple BIN chunks.");
                }

                binaryChunk = chunk.ToArray();
            }

            offset += chunkLength;
        }

        if (jsonChunk.IsEmpty)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] GLB source '{sourcePath}' contains no JSON chunk.");
        }

        using var document = JsonDocument.Parse(jsonChunk);
        return ReadGltfDocument(sourcePath, document.RootElement, binaryChunk);
    }

    private static SourceMesh ReadGltfDocument(string sourcePath, JsonElement root, byte[]? embeddedBinaryBuffer)
    {
        if (!root.TryGetProperty("asset", out var asset) ||
            !asset.TryGetProperty("version", out var version) ||
            !version.GetString()!.StartsWith("2.", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' must be glTF 2.x.");
        }

        var buffers = LoadGltfBuffers(sourcePath, root, embeddedBinaryBuffer);
        var bufferViews = ReadGltfBufferViews(sourcePath, root);
        var accessors = ReadGltfAccessors(sourcePath, root);
        var vertices = new List<SourceVertex>();
        var indices = new List<uint>();
        var submeshes = new List<SourceSubmesh>();
        var materialSlots = new Dictionary<int, uint>();
        var hasMissingNormals = false;

        if (!root.TryGetProperty("meshes", out var meshes) || meshes.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' contains no mesh array.");
        }

        if (!TryImportGltfSceneNodes(
                sourcePath,
                root,
                meshes,
                buffers,
                bufferViews,
                accessors,
                vertices,
                indices,
                submeshes,
                materialSlots,
                ref hasMissingNormals))
        {
            foreach (var mesh in meshes.EnumerateArray())
            {
                ImportGltfMeshPrimitives(
                    sourcePath,
                    mesh,
                    buffers,
                    bufferViews,
                    accessors,
                    Matrix4x4.Identity,
                    vertices,
                    indices,
                    submeshes,
                    materialSlots,
                    ref hasMissingNormals);
            }
        }

        if (vertices.Count == 0 || indices.Count == 0)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF mesh source '{sourcePath}' contains no drawable geometry.");
        }

        var sourceVertices = hasMissingNormals
            ? GenerateSmoothNormals(vertices.ToArray(), indices.ToArray())
            : vertices.ToArray();
        var sourceSubmeshes = submeshes.ToArray();
        ValidateSourceSubmeshes(sourcePath, sourceSubmeshes, checked((uint)indices.Count));
        return new SourceMesh(sourceVertices, indices.ToArray(), sourceSubmeshes);
    }

    private static bool TryImportGltfSceneNodes(
        string sourcePath,
        JsonElement root,
        JsonElement meshes,
        byte[][] buffers,
        GltfBufferView[] bufferViews,
        GltfAccessor[] accessors,
        List<SourceVertex> vertices,
        List<uint> indices,
        List<SourceSubmesh> submeshes,
        Dictionary<int, uint> materialSlots,
        ref bool hasMissingNormals)
    {
        if (!root.TryGetProperty("nodes", out var nodes) ||
            nodes.ValueKind != JsonValueKind.Array ||
            !root.TryGetProperty("scenes", out var scenes) ||
            scenes.ValueKind != JsonValueKind.Array ||
            scenes.GetArrayLength() == 0)
        {
            return false;
        }

        var sceneIndex = root.TryGetProperty("scene", out var sceneElement)
            ? sceneElement.GetInt32()
            : 0;
        if (sceneIndex < 0 || sceneIndex >= scenes.GetArrayLength())
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' scene index '{sceneIndex}' is outside scene count '{scenes.GetArrayLength()}'.");
        }

        var scene = scenes[sceneIndex];
        if (!scene.TryGetProperty("nodes", out var rootNodes) || rootNodes.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        var stack = new HashSet<int>();
        foreach (var rootNode in rootNodes.EnumerateArray())
        {
            ImportGltfNode(
                sourcePath,
                nodes,
                meshes,
                buffers,
                bufferViews,
                accessors,
                rootNode.GetInt32(),
                Matrix4x4.Identity,
                stack,
                vertices,
                indices,
                submeshes,
                materialSlots,
                ref hasMissingNormals);
        }

        return true;
    }

    private static void ImportGltfNode(
        string sourcePath,
        JsonElement nodes,
        JsonElement meshes,
        byte[][] buffers,
        GltfBufferView[] bufferViews,
        GltfAccessor[] accessors,
        int nodeIndex,
        Matrix4x4 parentTransform,
        HashSet<int> stack,
        List<SourceVertex> vertices,
        List<uint> indices,
        List<SourceSubmesh> submeshes,
        Dictionary<int, uint> materialSlots,
        ref bool hasMissingNormals)
    {
        if (nodeIndex < 0 || nodeIndex >= nodes.GetArrayLength())
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF node index '{nodeIndex}' in '{sourcePath}' is outside node count '{nodes.GetArrayLength()}'.");
        }

        if (!stack.Add(nodeIndex))
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' contains a node hierarchy cycle at node '{nodeIndex}'.");
        }

        try
        {
            var node = nodes[nodeIndex];
            var nodeTransform = ReadGltfNodeTransform(sourcePath, node, $"nodes[{nodeIndex}]") * parentTransform;
            if (node.TryGetProperty("mesh", out var meshElement))
            {
                var meshIndex = meshElement.GetInt32();
                if (meshIndex < 0 || meshIndex >= meshes.GetArrayLength())
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' nodes[{nodeIndex}].mesh index '{meshIndex}' is outside mesh count '{meshes.GetArrayLength()}'.");
                }

                ImportGltfMeshPrimitives(
                    sourcePath,
                    meshes[meshIndex],
                    buffers,
                    bufferViews,
                    accessors,
                    nodeTransform,
                    vertices,
                    indices,
                    submeshes,
                    materialSlots,
                    ref hasMissingNormals);
            }

            if (node.TryGetProperty("children", out var children))
            {
                if (children.ValueKind != JsonValueKind.Array)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' nodes[{nodeIndex}].children must be an array.");
                }

                foreach (var child in children.EnumerateArray())
                {
                    ImportGltfNode(
                        sourcePath,
                        nodes,
                        meshes,
                        buffers,
                        bufferViews,
                        accessors,
                        child.GetInt32(),
                        nodeTransform,
                        stack,
                        vertices,
                        indices,
                        submeshes,
                        materialSlots,
                        ref hasMissingNormals);
                }
            }
        }
        finally
        {
            stack.Remove(nodeIndex);
        }
    }

    private static void ImportGltfMeshPrimitives(
        string sourcePath,
        JsonElement mesh,
        byte[][] buffers,
        GltfBufferView[] bufferViews,
        GltfAccessor[] accessors,
        Matrix4x4 transform,
        List<SourceVertex> vertices,
        List<uint> indices,
        List<SourceSubmesh> submeshes,
        Dictionary<int, uint> materialSlots,
        ref bool hasMissingNormals)
    {
        if (!mesh.TryGetProperty("primitives", out var primitives) || primitives.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var normalTransform = GetNormalTransform(sourcePath, transform);
        foreach (var primitive in primitives.EnumerateArray())
        {
            var mode = primitive.TryGetProperty("mode", out var modeElement) ? modeElement.GetInt32() : 4;
            if (mode != 4)
            {
                throw new NotSupportedException($"[MeshAssetCooker] glTF source '{sourcePath}' uses primitive mode '{mode}', but the first importer scope supports triangles only.");
            }

            if (!primitive.TryGetProperty("attributes", out var attributes) ||
                !attributes.TryGetProperty("POSITION", out var positionAccessorElement))
            {
                throw new InvalidOperationException($"[MeshAssetCooker] glTF primitive in '{sourcePath}' must contain POSITION.");
            }

            ValidateSupportedGltfStaticMeshAttributes(sourcePath, attributes);

            var positionAccessorIndex = positionAccessorElement.GetInt32();
            var positionAccessor = GetGltfAccessor(sourcePath, accessors, positionAccessorIndex);
            if (positionAccessor.ComponentType != GltfComponentType.Float || positionAccessor.Type != "VEC3")
            {
                throw new NotSupportedException($"[MeshAssetCooker] glTF POSITION in '{sourcePath}' must be FLOAT VEC3.");
            }

            var normalAccessorIndex = TryGetGltfAttributeAccessor(attributes, "NORMAL");
            var tangentAccessorIndex = TryGetGltfAttributeAccessor(attributes, "TANGENT");
            var texCoordAccessorIndex = TryGetGltfAttributeAccessor(attributes, "TEXCOORD_0");
            var colorAccessorIndex = TryGetGltfAttributeAccessor(attributes, "COLOR_0");
            if (normalAccessorIndex >= 0)
            {
                var normalAccessor = GetGltfAccessor(sourcePath, accessors, normalAccessorIndex);
                if (normalAccessor.ComponentType != GltfComponentType.Float || normalAccessor.Type != "VEC3")
                {
                    throw new NotSupportedException($"[MeshAssetCooker] glTF NORMAL in '{sourcePath}' must be FLOAT VEC3.");
                }
            }
            else
            {
                hasMissingNormals = true;
            }

            if (tangentAccessorIndex >= 0)
            {
                var tangentAccessor = GetGltfAccessor(sourcePath, accessors, tangentAccessorIndex);
                if (tangentAccessor.ComponentType != GltfComponentType.Float || tangentAccessor.Type != "VEC4")
                {
                    throw new NotSupportedException($"[MeshAssetCooker] glTF TANGENT in '{sourcePath}' must be FLOAT VEC4.");
                }
            }

            if (texCoordAccessorIndex >= 0)
            {
                var texCoordAccessor = GetGltfAccessor(sourcePath, accessors, texCoordAccessorIndex);
                if (texCoordAccessor.ComponentType != GltfComponentType.Float || texCoordAccessor.Type != "VEC2")
                {
                    throw new NotSupportedException($"[MeshAssetCooker] glTF TEXCOORD_0 in '{sourcePath}' must be FLOAT VEC2.");
                }
            }

            var firstVertex = checked((uint)vertices.Count);
            for (int i = 0; i < positionAccessor.Count; i++)
            {
                var position = Vector3.Transform(
                    ReadGltfVector3(sourcePath, buffers, bufferViews, positionAccessor, i),
                    transform);
                var normal = normalAccessorIndex >= 0
                    ? NormalizeOrDefault(Vector3.TransformNormal(
                        ReadGltfVector3(sourcePath, buffers, bufferViews, accessors[normalAccessorIndex], i),
                        normalTransform))
                    : s_DefaultNormal;
                var tangent = tangentAccessorIndex >= 0
                    ? TransformTangent(ReadGltfVector4(sourcePath, buffers, bufferViews, accessors[tangentAccessorIndex], i), transform)
                    : TransformTangent(s_DefaultTangent, transform);
                var texCoord = texCoordAccessorIndex >= 0
                    ? ReadGltfVector2(sourcePath, buffers, bufferViews, accessors[texCoordAccessorIndex], i)
                    : new Vector2(0.0f, 0.0f);
                var color = colorAccessorIndex >= 0
                    ? ReadGltfColor(sourcePath, buffers, bufferViews, accessors[colorAccessorIndex], i)
                    : new Vector3(1.0f, 1.0f, 1.0f);

                vertices.Add(new SourceVertex(
                    position.X,
                    position.Y,
                    position.Z,
                    normal.X,
                    normal.Y,
                    normal.Z,
                    tangent.X,
                    tangent.Y,
                    tangent.Z,
                    tangent.W,
                    texCoord.X,
                    texCoord.Y,
                    color.X,
                    color.Y,
                    color.Z));
            }

            var firstIndex = checked((uint)indices.Count);
            if (primitive.TryGetProperty("indices", out var indicesAccessorElement))
            {
                var indexAccessor = GetGltfAccessor(sourcePath, accessors, indicesAccessorElement.GetInt32());
                if (indexAccessor.Type != "SCALAR")
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] glTF indices in '{sourcePath}' must be SCALAR.");
                }

                for (int i = 0; i < indexAccessor.Count; i++)
                {
                    indices.Add(checked(firstVertex + ReadGltfIndex(sourcePath, buffers, bufferViews, indexAccessor, i)));
                }
            }
            else
            {
                for (uint i = 0; i < checked((uint)positionAccessor.Count); i++)
                {
                    indices.Add(checked(firstVertex + i));
                }
            }

            var indexCount = checked((uint)indices.Count - firstIndex);
            if (indexCount == 0 || indexCount % 3 != 0)
            {
                throw new InvalidOperationException($"[MeshAssetCooker] glTF primitive in '{sourcePath}' produced a non-triangle index count '{indexCount}'.");
            }

            var materialIndex = primitive.TryGetProperty("material", out var materialElement)
                ? materialElement.GetInt32()
                : -1;
            if (!materialSlots.TryGetValue(materialIndex, out var materialSlot))
            {
                materialSlot = checked((uint)materialSlots.Count);
                materialSlots.Add(materialIndex, materialSlot);
            }

            submeshes.Add(new SourceSubmesh(firstIndex, indexCount, 0, materialSlot));
        }
    }

    private static Matrix4x4 ReadGltfNodeTransform(string sourcePath, JsonElement node, string context)
    {
        var hasMatrix = node.TryGetProperty("matrix", out var matrixElement);
        var hasTranslation = node.TryGetProperty("translation", out var translationElement);
        var hasRotation = node.TryGetProperty("rotation", out var rotationElement);
        var hasScale = node.TryGetProperty("scale", out var scaleElement);
        if (hasMatrix && (hasTranslation || hasRotation || hasScale))
        {
            throw new NotSupportedException($"[MeshAssetCooker] glTF source '{sourcePath}' {context} uses matrix and TRS together.");
        }

        if (hasMatrix)
        {
            var values = ReadGltfFloatArray(sourcePath, matrixElement, 16, $"{context}.matrix");
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
            ? ReadGltfVector3Property(sourcePath, translationElement, $"{context}.translation")
            : Vector3.Zero;
        var rotation = hasRotation
            ? ReadGltfQuaternionProperty(sourcePath, rotationElement, $"{context}.rotation")
            : Quaternion.Identity;
        var scale = hasScale
            ? ReadGltfVector3Property(sourcePath, scaleElement, $"{context}.scale")
            : Vector3.One;

        return Matrix4x4.CreateScale(scale) *
               Matrix4x4.CreateFromQuaternion(rotation) *
               Matrix4x4.CreateTranslation(translation);
    }

    private static Matrix4x4 GetNormalTransform(string sourcePath, Matrix4x4 transform)
    {
        if (!Matrix4x4.Invert(transform, out var inverse))
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' contains a non-invertible node transform.");
        }

        return Matrix4x4.Transpose(inverse);
    }

    private static Vector4 TransformTangent(Vector4 tangent, Matrix4x4 transform)
    {
        var direction = Vector3.TransformNormal(new Vector3(tangent.X, tangent.Y, tangent.Z), transform);
        direction = NormalizeOrDefault(direction);
        return new Vector4(direction, tangent.W < 0.0f ? -1.0f : 1.0f);
    }

    private static Vector3 ReadGltfVector3Property(string sourcePath, JsonElement element, string context)
    {
        var values = ReadGltfFloatArray(sourcePath, element, 3, context);
        return new Vector3(values[0], values[1], values[2]);
    }

    private static Quaternion ReadGltfQuaternionProperty(string sourcePath, JsonElement element, string context)
    {
        var values = ReadGltfFloatArray(sourcePath, element, 4, context);
        var quaternion = new Quaternion(values[0], values[1], values[2], values[3]);
        if (quaternion.LengthSquared() <= float.Epsilon)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' {context} is a zero quaternion.");
        }

        return Quaternion.Normalize(quaternion);
    }

    private static float[] ReadGltfFloatArray(string sourcePath, JsonElement element, int expectedCount, string context)
    {
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() != expectedCount)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' {context} must contain {expectedCount} numeric values.");
        }

        var values = new float[expectedCount];
        var index = 0;
        foreach (var value in element.EnumerateArray())
        {
            values[index++] = value.GetSingle();
        }

        return values;
    }

    private static DateTime GetSourceDependencyWriteTimeUtc(string sourcePath, MeshSourceFormat sourceFormat)
    {
        var lastWriteTimeUtc = File.GetLastWriteTimeUtc(sourcePath);
        if (sourceFormat != MeshSourceFormat.GltfJson)
        {
            return lastWriteTimeUtc;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(sourcePath));
            if (!document.RootElement.TryGetProperty("buffers", out var buffers) || buffers.ValueKind != JsonValueKind.Array)
            {
                return lastWriteTimeUtc;
            }

            foreach (var buffer in buffers.EnumerateArray())
            {
                if (!buffer.TryGetProperty("uri", out var uriElement))
                {
                    continue;
                }

                var uri = uriElement.GetString();
                if (string.IsNullOrWhiteSpace(uri) || uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var bufferPath = ResolveGltfExternalBufferPath(sourcePath, uri);
                if (File.Exists(bufferPath))
                {
                    lastWriteTimeUtc = Max(lastWriteTimeUtc, File.GetLastWriteTimeUtc(bufferPath));
                }
            }
        }
        catch
        {
            return File.GetLastWriteTimeUtc(sourcePath);
        }

        return lastWriteTimeUtc;
    }

    private static DateTime Max(DateTime a, DateTime b)
    {
        return a >= b ? a : b;
    }

    private static byte[][] LoadGltfBuffers(string sourcePath, JsonElement root, byte[]? embeddedBinaryBuffer)
    {
        if (!root.TryGetProperty("buffers", out var buffersElement) || buffersElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' contains no buffers.");
        }

        var buffers = new byte[buffersElement.GetArrayLength()][];
        var index = 0;
        foreach (var buffer in buffersElement.EnumerateArray())
        {
            var context = $"buffers[{index}]";
            if (!buffer.TryGetProperty("uri", out var uriElement))
            {
                if (index != 0)
                {
                    throw new NotSupportedException($"[MeshAssetCooker] glTF source '{sourcePath}' {context} has no uri, but only buffers[0] may use a GLB BIN chunk.");
                }

                buffers[index++] = GetGltfEmbeddedBinaryBuffer(sourcePath, embeddedBinaryBuffer, GetRequiredInt32(sourcePath, buffer, "byteLength", context), context);
                continue;
            }

            var uri = uriElement.GetString();
            if (string.IsNullOrWhiteSpace(uri))
            {
                throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' {context}.uri is empty.");
            }

            buffers[index++] = uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? DecodeGltfDataUri(sourcePath, uri)
                : LoadGltfExternalBuffer(sourcePath, uri, context);
        }

        return buffers;
    }

    private static byte[] GetGltfEmbeddedBinaryBuffer(string sourcePath, byte[]? embeddedBinaryBuffer, int byteLength, string context)
    {
        if (embeddedBinaryBuffer == null)
        {
            throw new NotSupportedException($"[MeshAssetCooker] glTF source '{sourcePath}' {context} has no uri and no GLB BIN chunk was found.");
        }

        if (byteLength < 0)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' {context}.byteLength is invalid.");
        }

        if (byteLength > embeddedBinaryBuffer.Length)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] GLB source '{sourcePath}' {context}.byteLength '{byteLength}' exceeds BIN chunk length '{embeddedBinaryBuffer.Length}'.");
        }

        var buffer = new byte[byteLength];
        Buffer.BlockCopy(embeddedBinaryBuffer, 0, buffer, 0, byteLength);
        return buffer;
    }

    private static GltfBufferView[] ReadGltfBufferViews(string sourcePath, JsonElement root)
    {
        if (!root.TryGetProperty("bufferViews", out var bufferViewsElement) || bufferViewsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' contains no bufferViews.");
        }

        var bufferViews = new GltfBufferView[bufferViewsElement.GetArrayLength()];
        var index = 0;
        foreach (var bufferView in bufferViewsElement.EnumerateArray())
        {
            var context = $"bufferViews[{index}]";
            bufferViews[index++] = new GltfBufferView(
                GetRequiredInt32(sourcePath, bufferView, "buffer", context),
                GetOptionalInt32(bufferView, "byteOffset"),
                GetRequiredInt32(sourcePath, bufferView, "byteLength", context),
                GetOptionalInt32(bufferView, "byteStride"));
        }

        return bufferViews;
    }

    private static GltfAccessor[] ReadGltfAccessors(string sourcePath, JsonElement root)
    {
        if (!root.TryGetProperty("accessors", out var accessorsElement) || accessorsElement.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' contains no accessors.");
        }

        var accessors = new GltfAccessor[accessorsElement.GetArrayLength()];
        var index = 0;
        foreach (var accessor in accessorsElement.EnumerateArray())
        {
            var context = $"accessors[{index}]";
            accessors[index++] = new GltfAccessor(
                GetRequiredInt32(sourcePath, accessor, "bufferView", context),
                GetOptionalInt32(accessor, "byteOffset"),
                (GltfComponentType)GetRequiredInt32(sourcePath, accessor, "componentType", context),
                GetRequiredInt32(sourcePath, accessor, "count", context),
                GetRequiredString(sourcePath, accessor, "type", context),
                GetOptionalBoolean(accessor, "normalized"));
        }

        return accessors;
    }

    private static GltfAccessor GetGltfAccessor(string sourcePath, GltfAccessor[] accessors, int index)
    {
        if (index < 0 || index >= accessors.Length)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF accessor index '{index}' in '{sourcePath}' is outside accessor count '{accessors.Length}'.");
        }

        return accessors[index];
    }

    private static int TryGetGltfAttributeAccessor(JsonElement attributes, string name)
    {
        return attributes.TryGetProperty(name, out var accessorElement)
            ? accessorElement.GetInt32()
            : -1;
    }

    private static void ValidateSupportedGltfStaticMeshAttributes(string sourcePath, JsonElement attributes)
    {
        foreach (var attribute in attributes.EnumerateObject())
        {
            var name = attribute.Name;
            if (string.Equals(name, "POSITION", StringComparison.Ordinal) ||
                string.Equals(name, "NORMAL", StringComparison.Ordinal) ||
                string.Equals(name, "TANGENT", StringComparison.Ordinal) ||
                string.Equals(name, "TEXCOORD_0", StringComparison.Ordinal) ||
                string.Equals(name, "COLOR_0", StringComparison.Ordinal))
            {
                continue;
            }

            if (name.StartsWith("TEXCOORD_", StringComparison.Ordinal))
            {
                throw new NotSupportedException($"[MeshAssetCooker] glTF source '{sourcePath}' attribute '{name}' is not supported. Static mesh import currently accepts only TEXCOORD_0.");
            }

            if (name.StartsWith("COLOR_", StringComparison.Ordinal))
            {
                throw new NotSupportedException($"[MeshAssetCooker] glTF source '{sourcePath}' attribute '{name}' is not supported. Static mesh import currently accepts only COLOR_0.");
            }

            throw new NotSupportedException($"[MeshAssetCooker] glTF source '{sourcePath}' attribute '{name}' is not supported by the static mesh importer.");
        }
    }

    private static Vector3 ReadGltfVector3(
        string sourcePath,
        byte[][] buffers,
        GltfBufferView[] bufferViews,
        GltfAccessor accessor,
        int elementIndex)
    {
        if (accessor.ComponentType != GltfComponentType.Float)
        {
            throw new NotSupportedException($"[MeshAssetCooker] glTF vector accessor in '{sourcePath}' must use FLOAT components.");
        }

        var span = GetGltfElementSpan(sourcePath, buffers, bufferViews, accessor, elementIndex, 3);
        return new Vector3(
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(0, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(4, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(8, sizeof(float))));
    }

    private static Vector4 ReadGltfVector4(
        string sourcePath,
        byte[][] buffers,
        GltfBufferView[] bufferViews,
        GltfAccessor accessor,
        int elementIndex)
    {
        if (accessor.ComponentType != GltfComponentType.Float)
        {
            throw new NotSupportedException($"[MeshAssetCooker] glTF vector accessor in '{sourcePath}' must use FLOAT components.");
        }

        var span = GetGltfElementSpan(sourcePath, buffers, bufferViews, accessor, elementIndex, 4);
        return new Vector4(
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(0, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(4, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(8, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(12, sizeof(float))));
    }

    private static Vector2 ReadGltfVector2(
        string sourcePath,
        byte[][] buffers,
        GltfBufferView[] bufferViews,
        GltfAccessor accessor,
        int elementIndex)
    {
        if (accessor.ComponentType != GltfComponentType.Float)
        {
            throw new NotSupportedException($"[MeshAssetCooker] glTF vec2 accessor in '{sourcePath}' must use FLOAT components.");
        }

        var span = GetGltfElementSpan(sourcePath, buffers, bufferViews, accessor, elementIndex, 2);
        return new Vector2(
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(0, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(4, sizeof(float))));
    }

    private static Vector3 ReadGltfColor(
        string sourcePath,
        byte[][] buffers,
        GltfBufferView[] bufferViews,
        GltfAccessor accessor,
        int elementIndex)
    {
        var componentCount = accessor.Type switch
        {
            "VEC3" => 3,
            "VEC4" => 4,
            _ => throw new NotSupportedException($"[MeshAssetCooker] glTF COLOR_0 in '{sourcePath}' must be VEC3 or VEC4.")
        };
        var span = GetGltfElementSpan(sourcePath, buffers, bufferViews, accessor, elementIndex, componentCount);
        return new Vector3(
            ReadGltfNumericComponent(span, accessor.ComponentType, accessor.Normalized, 0),
            ReadGltfNumericComponent(span, accessor.ComponentType, accessor.Normalized, 1),
            ReadGltfNumericComponent(span, accessor.ComponentType, accessor.Normalized, 2));
    }

    private static uint ReadGltfIndex(
        string sourcePath,
        byte[][] buffers,
        GltfBufferView[] bufferViews,
        GltfAccessor accessor,
        int elementIndex)
    {
        var span = GetGltfElementSpan(sourcePath, buffers, bufferViews, accessor, elementIndex, 1);
        return accessor.ComponentType switch
        {
            GltfComponentType.UnsignedByte => span[0],
            GltfComponentType.UnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(span),
            GltfComponentType.UnsignedInt => BinaryPrimitives.ReadUInt32LittleEndian(span),
            _ => throw new NotSupportedException($"[MeshAssetCooker] glTF indices in '{sourcePath}' must be UNSIGNED_BYTE, UNSIGNED_SHORT, or UNSIGNED_INT.")
        };
    }

    private static ReadOnlySpan<byte> GetGltfElementSpan(
        string sourcePath,
        byte[][] buffers,
        GltfBufferView[] bufferViews,
        GltfAccessor accessor,
        int elementIndex,
        int expectedComponentCount)
    {
        if (elementIndex < 0 || elementIndex >= accessor.Count)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF accessor element '{elementIndex}' in '{sourcePath}' is outside count '{accessor.Count}'.");
        }

        if (accessor.BufferView < 0 || accessor.BufferView >= bufferViews.Length)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF accessor bufferView '{accessor.BufferView}' in '{sourcePath}' is outside bufferView count '{bufferViews.Length}'.");
        }

        var bufferView = bufferViews[accessor.BufferView];
        if (bufferView.Buffer < 0 || bufferView.Buffer >= buffers.Length)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF bufferView buffer '{bufferView.Buffer}' in '{sourcePath}' is outside buffer count '{buffers.Length}'.");
        }

        var componentSize = GetGltfComponentSize(accessor.ComponentType);
        var elementSize = checked(componentSize * expectedComponentCount);
        var accessorComponentCount = GetGltfTypeComponentCount(sourcePath, accessor.Type);
        if (accessorComponentCount < expectedComponentCount)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF accessor type '{accessor.Type}' in '{sourcePath}' is smaller than expected component count '{expectedComponentCount}'.");
        }

        var stride = bufferView.ByteStride > 0
            ? bufferView.ByteStride
            : checked(componentSize * accessorComponentCount);
        if (stride < elementSize)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF accessor stride '{stride}' in '{sourcePath}' is smaller than element size '{elementSize}'.");
        }

        var offset = checked(bufferView.ByteOffset + accessor.ByteOffset + elementIndex * stride);
        if (offset < bufferView.ByteOffset || offset + elementSize > bufferView.ByteOffset + bufferView.ByteLength)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF accessor element '{elementIndex}' in '{sourcePath}' is outside its bufferView range.");
        }

        var buffer = buffers[bufferView.Buffer];
        if (offset + elementSize > buffer.Length)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF accessor element '{elementIndex}' in '{sourcePath}' is outside buffer length '{buffer.Length}'.");
        }

        return buffer.AsSpan(offset, elementSize);
    }

    private static float ReadGltfNumericComponent(
        ReadOnlySpan<byte> span,
        GltfComponentType componentType,
        bool normalized,
        int componentIndex)
    {
        return componentType switch
        {
            GltfComponentType.Float => BinaryPrimitives.ReadSingleLittleEndian(span.Slice(componentIndex * sizeof(float), sizeof(float))),
            GltfComponentType.UnsignedByte => normalized ? span[componentIndex] / 255.0f : span[componentIndex],
            GltfComponentType.UnsignedShort => normalized
                ? BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(componentIndex * sizeof(ushort), sizeof(ushort))) / 65535.0f
                : BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(componentIndex * sizeof(ushort), sizeof(ushort))),
            _ => throw new NotSupportedException($"[MeshAssetCooker] glTF color component type '{componentType}' is not supported.")
        };
    }

    private static int GetGltfComponentSize(GltfComponentType componentType)
    {
        return componentType switch
        {
            GltfComponentType.UnsignedByte => 1,
            GltfComponentType.UnsignedShort => 2,
            GltfComponentType.UnsignedInt => 4,
            GltfComponentType.Float => 4,
            _ => throw new NotSupportedException($"[MeshAssetCooker] glTF component type '{componentType}' is not supported.")
        };
    }

    private static int GetGltfTypeComponentCount(string sourcePath, string type)
    {
        return type switch
        {
            "SCALAR" => 1,
            "VEC2" => 2,
            "VEC3" => 3,
            "VEC4" => 4,
            _ => throw new NotSupportedException($"[MeshAssetCooker] glTF accessor type '{type}' in '{sourcePath}' is not supported by the static mesh importer.")
        };
    }

    private static string ResolveGltfExternalBufferPath(string sourcePath, string uri)
    {
        if (uri.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"[MeshAssetCooker] Remote glTF buffer URI '{uri}' is not supported.");
        }

        var decoded = Uri.UnescapeDataString(uri);
        return Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, decoded.Replace('/', Path.DirectorySeparatorChar)));
    }

    private static byte[] LoadGltfExternalBuffer(string sourcePath, string uri, string context)
    {
        var bufferPath = ResolveGltfExternalBufferPath(sourcePath, uri);
        if (!File.Exists(bufferPath))
        {
            throw new FileNotFoundException(
                $"[MeshAssetCooker] glTF source '{sourcePath}' {context}.uri resolves to missing external buffer '{bufferPath}'.",
                bufferPath);
        }

        return File.ReadAllBytes(bufferPath);
    }

    private static byte[] DecodeGltfDataUri(string sourcePath, string uri)
    {
        var comma = uri.IndexOf(',');
        if (comma < 0 || !uri[..comma].Contains(";base64", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException($"[MeshAssetCooker] glTF data URI in '{sourcePath}' must be base64 encoded.");
        }

        return Convert.FromBase64String(uri[(comma + 1)..]);
    }

    private static int GetRequiredInt32(string sourcePath, JsonElement element, string propertyName, string context)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' is missing required integer property '{context}.{propertyName}'.");
        }

        return property.GetInt32();
    }

    private static int GetOptionalInt32(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property)
            ? property.GetInt32()
            : 0;
    }

    private static bool GetOptionalBoolean(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) && property.GetBoolean();
    }

    private static string GetRequiredString(string sourcePath, JsonElement element, string propertyName, string context)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' is missing required string property '{context}.{propertyName}'.");
        }

        return property.GetString() ?? string.Empty;
    }

    private static uint ResolveObjVertex(
        string sourcePath,
        int lineNumber,
        string token,
        List<ObjVector3> positions,
        List<ObjVector2> texCoords,
        List<ObjVector3> normals,
        List<SourceVertex> vertices,
        Dictionary<ObjVertexKey, uint> vertexLookup)
    {
        var key = ParseObjVertexKey(sourcePath, lineNumber, token, positions.Count, texCoords.Count, normals.Count);
        if (vertexLookup.TryGetValue(key, out var existingIndex))
        {
            return existingIndex;
        }

        var position = positions[key.PositionIndex];
        var texCoord = key.TexCoordIndex >= 0
            ? texCoords[key.TexCoordIndex]
            : new ObjVector2(0.0f, 0.0f);
        var normal = key.NormalIndex >= 0
            ? NormalizeOrDefault(normals[key.NormalIndex].ToVector3())
            : s_DefaultNormal;

        var vertex = new SourceVertex(
            position.X,
            position.Y,
            position.Z,
            normal.X,
            normal.Y,
            normal.Z,
            s_DefaultTangent.X,
            s_DefaultTangent.Y,
            s_DefaultTangent.Z,
            s_DefaultTangent.W,
            texCoord.U,
            texCoord.V,
            1.0f,
            1.0f,
            1.0f);
        var newIndex = checked((uint)vertices.Count);
        vertices.Add(vertex);
        vertexLookup.Add(key, newIndex);
        return newIndex;
    }

    private static ObjVertexKey ParseObjVertexKey(
        string sourcePath,
        int lineNumber,
        string token,
        int positionCount,
        int texCoordCount,
        int normalCount)
    {
        var parts = token.Split('/');
        if (parts.Length == 0 || string.IsNullOrWhiteSpace(parts[0]))
        {
            throw new InvalidOperationException($"[MeshAssetCooker] Invalid OBJ face token '{token}' at {FormatObjLocation(sourcePath, lineNumber)}.");
        }

        var positionIndex = ResolveObjIndex(sourcePath, lineNumber, token, parts[0], positionCount, "position");
        var texCoordIndex = -1;
        if (parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]))
        {
            texCoordIndex = ResolveObjIndex(sourcePath, lineNumber, token, parts[1], texCoordCount, "texcoord");
        }

        var normalIndex = -1;
        if (parts.Length > 2 && !string.IsNullOrWhiteSpace(parts[2]))
        {
            normalIndex = ResolveObjIndex(sourcePath, lineNumber, token, parts[2], normalCount, "normal");
        }

        return new ObjVertexKey(positionIndex, texCoordIndex, normalIndex);
    }

    private static int ResolveObjIndex(string sourcePath, int lineNumber, string token, string value, int count, string label)
    {
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rawIndex) || rawIndex == 0)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] Invalid OBJ {label} index '{value}' in face token '{token}' at {FormatObjLocation(sourcePath, lineNumber)}.");
        }

        var index = rawIndex > 0 ? rawIndex - 1 : count + rawIndex;
        if (index < 0 || index >= count)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] OBJ {label} index '{rawIndex}' in face token '{token}' at {FormatObjLocation(sourcePath, lineNumber)} is outside the available count '{count}'.");
        }

        return index;
    }

    private static string FormatObjLocation(string sourcePath, int lineNumber)
    {
        return $"'{sourcePath}' line {lineNumber}";
    }

    private static void FinishObjSubmesh(
        List<SourceSubmesh> submeshes,
        uint firstIndex,
        uint indexEnd,
        uint materialSlot)
    {
        if (indexEnd <= firstIndex)
        {
            return;
        }

        submeshes.Add(new SourceSubmesh(firstIndex, indexEnd - firstIndex, 0, materialSlot));
    }

    private static void ValidateSourceSubmeshes(string sourcePath, SourceSubmesh[] submeshes, uint indexCount)
    {
        if (submeshes.Length == 0)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] Mesh source '{sourcePath}' contains no submesh ranges.");
        }

        for (int i = 0; i < submeshes.Length; i++)
        {
            var submesh = submeshes[i];
            if (submesh.IndexCount == 0 ||
                submesh.IndexCount % 3 != 0 ||
                submesh.FirstIndex >= indexCount ||
                submesh.IndexCount > indexCount - submesh.FirstIndex)
            {
                throw new InvalidOperationException(
                    $"[MeshAssetCooker] Mesh source '{sourcePath}' contains invalid submesh {i}: firstIndex={submesh.FirstIndex}, indexCount={submesh.IndexCount}, indexBufferCount={indexCount}.");
            }
        }
    }

    private static string StripComment(string line)
    {
        var commentIndex = line.IndexOf('#');
        return commentIndex >= 0 ? line[..commentIndex] : line;
    }

    private static float ReadFloat(string value)
    {
        if (!float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
        {
            throw new InvalidOperationException($"[MeshAssetCooker] Invalid mesh float token '{value}'.");
        }

        return result;
    }

    private static SourceVertex[] GenerateSmoothNormals(SourceVertex[] vertices, uint[] indices)
    {
        var accumNormals = new Vector3[vertices.Length];
        for (int i = 0; i + 2 < indices.Length; i += 3)
        {
            var index0 = checked((int)indices[i]);
            var index1 = checked((int)indices[i + 1]);
            var index2 = checked((int)indices[i + 2]);
            var position0 = vertices[index0].Position;
            var position1 = vertices[index1].Position;
            var position2 = vertices[index2].Position;
            var faceNormal = Vector3.Cross(position1 - position0, position2 - position0);
            if (faceNormal.LengthSquared() <= float.Epsilon)
            {
                continue;
            }

            faceNormal = Vector3.Normalize(faceNormal);
            accumNormals[index0] += faceNormal;
            accumNormals[index1] += faceNormal;
            accumNormals[index2] += faceNormal;
        }

        var result = new SourceVertex[vertices.Length];
        for (int i = 0; i < vertices.Length; i++)
        {
            var normal = NormalizeOrDefault(accumNormals[i]);
            result[i] = vertices[i].WithNormal(normal);
        }

        return result;
    }

    private static Vector3 NormalizeOrDefault(Vector3 normal)
    {
        return normal.LengthSquared() > float.Epsilon
            ? Vector3.Normalize(normal)
            : s_DefaultNormal;
    }

    private static Vector4 NormalizeTangentOrDefault(Vector4 tangent)
    {
        var xyz = new Vector3(tangent.X, tangent.Y, tangent.Z);
        if (xyz.LengthSquared() <= float.Epsilon)
        {
            return s_DefaultTangent;
        }

        xyz = Vector3.Normalize(xyz);
        var handedness = tangent.W < 0.0f ? -1.0f : 1.0f;
        return new Vector4(xyz, handedness);
    }

    private static uint ReadUInt32(string value, string label)
    {
        if (!uint.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var result))
        {
            throw new InvalidOperationException($"[MeshAssetCooker] Invalid mesh {label} token '{value}'.");
        }

        return result;
    }

    private static void WriteSingle(Span<byte> bytes, int offset, float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(offset, sizeof(float)), value);
    }

    private static void WriteSingle(Stream stream, float value)
    {
        Span<byte> bytes = stackalloc byte[sizeof(float)];
        BinaryPrimitives.WriteSingleLittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static void WriteInt32(Stream stream, int value)
    {
        Span<byte> bytes = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
        stream.Write(bytes);
    }

    private static int ReadInt32(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4));
    }

    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset, sizeof(float)));
    }

    private readonly record struct SourceMesh(
        SourceVertex[] Vertices,
        uint[] Indices,
        SourceSubmesh[] Submeshes);

    private readonly record struct SourceSubmesh(
        uint FirstIndex,
        uint IndexCount,
        int VertexOffset,
        uint MaterialSlot);

    private readonly record struct SourceVertex(
        float PositionX,
        float PositionY,
        float PositionZ,
        float NormalX,
        float NormalY,
        float NormalZ,
        float TangentX,
        float TangentY,
        float TangentZ,
        float TangentW,
        float U,
        float V,
        float ColorR,
        float ColorG,
        float ColorB)
    {
        public Vector3 Position => new(PositionX, PositionY, PositionZ);

        public SourceVertex WithNormal(Vector3 normal)
        {
            return new SourceVertex(
                PositionX,
                PositionY,
                PositionZ,
                normal.X,
                normal.Y,
                normal.Z,
                TangentX,
                TangentY,
                TangentZ,
                TangentW,
                U,
                V,
                ColorR,
                ColorG,
                ColorB);
        }
    }

    private readonly record struct ObjVector2(float U, float V);

    private readonly record struct ObjVector3(float X, float Y, float Z)
    {
        public Vector3 ToVector3()
        {
            return new Vector3(X, Y, Z);
        }
    }

    private readonly record struct ObjVertexKey(int PositionIndex, int TexCoordIndex, int NormalIndex);

    private enum GltfComponentType
    {
        UnsignedByte = 5121,
        UnsignedShort = 5123,
        UnsignedInt = 5125,
        Float = 5126
    }

    private readonly record struct GltfBufferView(
        int Buffer,
        int ByteOffset,
        int ByteLength,
        int ByteStride);

    private readonly record struct GltfAccessor(
        int BufferView,
        int ByteOffset,
        GltfComponentType ComponentType,
        int Count,
        string Type,
        bool Normalized);

    private readonly record struct CookedMeshHeader(
        uint VertexCount,
        uint VertexStride,
        uint VertexDataSize,
        uint IndexCount,
        MeshIndexFormat IndexFormat,
        uint IndexDataSize,
        MeshBounds Bounds,
        uint SubmeshCount,
        uint SubmeshDataSize);
}
