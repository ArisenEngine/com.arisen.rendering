using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using ArisenEngine.Core.Assets;
using ArisenEngine.Core.Diagnostics;

namespace ArisenEngine.Rendering.Resources;

public enum MeshSourceFormat
{
    ArisenTextMesh
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
    CookedAssetHandle Handle)
{
    public bool IsValid => Handle.IsValid;
}

public static class MeshAssetCooker
{
    public const uint StaticMeshVertexStride = 32;

    private const string MeshAssetType = "Mesh";
    private const int HeaderSize = 48;
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
        var sourceWriteTimeUtc = File.GetLastWriteTimeUtc(sourceAsset.SourcePath);

        if (!File.Exists(outputPath) || File.GetLastWriteTimeUtc(outputPath) < sourceWriteTimeUtc)
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
            _ => throw new NotSupportedException($"Mesh source format '{mesh.SourceFormat}' is not implemented yet.")
        };

        var vertexDataSize = checked(source.Vertices.Length * (int)StaticMeshVertexStride);
        var indexDataSize = checked(source.Indices.Length * sizeof(uint));

        using var stream = File.Create(outputPath);
        stream.Write(s_Magic);
        WriteInt32(stream, 1);
        WriteInt32(stream, source.Vertices.Length);
        WriteInt32(stream, checked((int)StaticMeshVertexStride));
        WriteInt32(stream, vertexDataSize);
        WriteInt32(stream, source.Indices.Length);
        WriteInt32(stream, (int)mesh.Variant.IndexFormat);
        WriteInt32(stream, indexDataSize);
        WriteInt32(stream, 0);
        WriteInt32(stream, 0);
        WriteInt32(stream, 0);

        Span<byte> vertexBytes = stackalloc byte[(int)StaticMeshVertexStride];
        foreach (var vertex in source.Vertices)
        {
            WriteSingle(vertexBytes, 0, vertex.PositionX);
            WriteSingle(vertexBytes, 4, vertex.PositionY);
            WriteSingle(vertexBytes, 8, vertex.PositionZ);
            WriteSingle(vertexBytes, 12, vertex.U);
            WriteSingle(vertexBytes, 16, vertex.V);
            WriteSingle(vertexBytes, 20, vertex.ColorR);
            WriteSingle(vertexBytes, 24, vertex.ColorG);
            WriteSingle(vertexBytes, 28, vertex.ColorB);
            stream.Write(vertexBytes);
        }

        Span<byte> indexBytes = stackalloc byte[sizeof(uint)];
        foreach (var index in source.Indices)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(indexBytes, index);
            stream.Write(indexBytes);
        }

        Logger.Log(
            $"[MeshAssetCooker] Cooked mesh asset {mesh.Guid} | Vertices: {source.Vertices.Length} | Indices: {source.Indices.Length} | Variant: {variant} | Output: {outputPath}");
    }

    private static CookedMeshHeader ReadHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes.Slice(0, s_Magic.Length).SequenceEqual(s_Magic))
        {
            throw new InvalidOperationException("[MeshAssetCooker] Cooked mesh header magic is invalid.");
        }

        var version = ReadInt32(bytes, 8);
        if (version != 1)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] Cooked mesh version '{version}' is not supported.");
        }

        var vertexCount = ReadInt32(bytes, 12);
        var vertexStride = ReadInt32(bytes, 16);
        var vertexDataSize = ReadInt32(bytes, 20);
        var indexCount = ReadInt32(bytes, 24);
        var indexFormat = (MeshIndexFormat)ReadInt32(bytes, 28);
        var indexDataSize = ReadInt32(bytes, 32);

        if (vertexCount <= 0 || vertexStride <= 0 || vertexDataSize <= 0 || indexCount <= 0 || indexDataSize <= 0)
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

        if (HeaderSize + vertexDataSize + indexDataSize > bytes.Length)
        {
            throw new InvalidOperationException("[MeshAssetCooker] Cooked mesh payload is truncated.");
        }

        return new CookedMeshHeader(
            checked((uint)vertexCount),
            checked((uint)vertexStride),
            checked((uint)vertexDataSize),
            checked((uint)indexCount),
            indexFormat,
            checked((uint)indexDataSize));
    }

    private static SourceMesh ReadArisenTextMesh(string sourcePath)
    {
        var vertices = new List<SourceVertex>();
        var indices = new List<uint>();

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
                if (parts.Length != 9)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] Mesh vertex line in '{sourcePath}' must contain 8 numeric values.");
                }

                vertices.Add(new SourceVertex(
                    ReadFloat(parts[1]),
                    ReadFloat(parts[2]),
                    ReadFloat(parts[3]),
                    ReadFloat(parts[4]),
                    ReadFloat(parts[5]),
                    ReadFloat(parts[6]),
                    ReadFloat(parts[7]),
                    ReadFloat(parts[8])));
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

        return new SourceMesh(vertices.ToArray(), indices.ToArray());
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

    private static void WriteSingle(Span<byte> bytes, int offset, float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(bytes.Slice(offset, sizeof(float)), value);
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

    private readonly record struct SourceMesh(SourceVertex[] Vertices, uint[] Indices);

    private readonly record struct SourceVertex(
        float PositionX,
        float PositionY,
        float PositionZ,
        float U,
        float V,
        float ColorR,
        float ColorG,
        float ColorB);

    private readonly record struct CookedMeshHeader(
        uint VertexCount,
        uint VertexStride,
        uint VertexDataSize,
        uint IndexCount,
        MeshIndexFormat IndexFormat,
        uint IndexDataSize);
}
