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

/// <summary>
/// One joint of a skinned mesh's skeleton, ordered parents-first so a palette resolves in a single
/// pass. The local transform is the joint's rest pose relative to its nearest joint ancestor (or to
/// the mesh space for root joints), and the inverse bind maps a bind-pose vertex into that joint's
/// space. In the row-vector convention a palette entry is <c>inverseBind * jointGlobal</c>.
/// </summary>
public readonly record struct MeshSkinJoint(
    int ParentIndex,
    Vector3 LocalTranslation,
    Quaternion LocalRotation,
    Matrix4x4 InverseBind);

public sealed record MeshSkin(IReadOnlyList<MeshSkinJoint> Joints);

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
    uint JointCount,
    uint SkinDataOffset,
    uint SkinDataSize,
    uint AnimationCount,
    uint AnimationDataOffset,
    uint AnimationDataSize,
    CookedAssetHandle Handle)
{
    public bool IsValid => Handle.IsValid;

    public bool HasSkin => JointCount > 0;

    public bool HasAnimations => AnimationCount > 0;
}

public static class MeshAssetCooker
{
    public const uint StaticMeshVertexStride = 60;
    /// <summary>Joint indices (4 x uint8) and weights (4 x float) follow the static attributes.</summary>
    public const uint SkinnedMeshVertexStride = 80;
    public const uint SkinJointStride = 96;
    public const uint SubmeshStride = 16;
    // Version 6 adds the baked animation table beside the skin block: one entry per source clip with
    // the clip's name, its duration, and per-joint rotation tracks remapped into the cooked skeleton's
    // order. Translation and scale channels are not part of it - the pose this payload feeds stores
    // unit-scale joints and, in this slice, rotations only - so a source that animates them is
    // reported and those tracks are left out.
    public const int CookedFormatVersion = 7;

    /// <summary>Per-clip header: a fixed-width name, the clip's duration, and its track table.</summary>
    internal const int AnimationNameBytes = 64;
    internal const uint AnimationClipHeaderStride = AnimationNameBytes + 12;
    internal const uint AnimationTrackStride = 28;
    internal const uint AnimationKeyStride = 20;
    internal const uint AnimationTranslatedKeyStride = 32;

    private const string MeshAssetType = "Mesh";
    private const uint GltfBinaryMagic = 0x46546C67;
    private const uint GltfBinaryJsonChunkType = 0x4E4F534A;
    private const uint GltfBinaryBinChunkType = 0x004E4942;
    private const int GltfBinaryHeaderSize = 12;
    private const int GltfBinaryChunkHeaderSize = 8;
    private const int HeaderSize = 92;
    private static readonly Vector3 s_DefaultNormal = new(0.0f, 0.0f, 1.0f);
    private static readonly Vector4 s_DefaultTangent = new(1.0f, 0.0f, 0.0f, 1.0f);
    private const float SkinWeightTolerance = 1.0e-3f;
    private const float JointScaleTolerance = 1.0e-3f;
    private const float IdentityTransformTolerance = 1.0e-4f;
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

        var variant = mesh.Variant.GetCookedVariant();
        if (!assetDatabase.CanReadSourceAssets)
        {
            return LoadCooked(assetDatabase, mesh, variant);
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

        var sourceWriteTimeUtc = GetSourceDependencyWriteTimeUtc(sourceAsset.SourcePath, mesh.SourceFormat);

        if (!assetDatabase.TryGetCookedArtifact(mesh.Guid, variant, out CookedAssetRecord current) ||
            !File.Exists(current.Path) ||
            File.GetLastWriteTimeUtc(current.Path) < sourceWriteTimeUtc ||
            !IsCurrentCookedMesh(current.Path))
        {
            using CookedArtifactWrite write = assetDatabase.BeginCookedArtifactWrite(
                mesh.Guid,
                variant,
                ".mesh");
            CookMesh(sourceAsset, mesh, variant, write.OutputPath);

            var outputInfo = new FileInfo(write.OutputPath);
            if (!outputInfo.Exists || outputInfo.Length <= HeaderSize)
            {
                throw new InvalidOperationException($"[MeshAssetCooker] Mesh asset '{mesh.Guid}' produced no cooked payload.");
            }

            write.Commit(sourceAsset.AssetType);
        }

        return LoadCooked(assetDatabase, mesh, variant);
    }

    public static bool TryReadCookedBounds(
        IAssetDatabase assetDatabase,
        Guid meshGuid,
        out MeshBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(assetDatabase);

        bounds = default;
        if (meshGuid == Guid.Empty ||
            !assetDatabase.TryLoadCookedAsset(
                meshGuid,
                MeshVariantKey.Default.GetCookedVariant(),
                MeshAssetType,
                out CookedAssetHandle handle))
        {
            return false;
        }

        try
        {
            if (!assetDatabase.TryGetCookedAssetBytes(handle, out ReadOnlyMemory<byte> bytes))
            {
                return false;
            }

            try
            {
                bounds = ReadHeader(bytes.Span).Bounds;
                return IsValidBounds(bounds);
            }
            catch (Exception exception) when (
                exception is InvalidOperationException or
                NotSupportedException or
                OverflowException or
                ArgumentOutOfRangeException)
            {
                bounds = default;
                return false;
            }
        }
        finally
        {
            assetDatabase.Release(handle);
        }
    }

    private static CookedMesh LoadCooked(
        IAssetDatabase assetDatabase,
        MeshAsset mesh,
        string variant)
    {
        if (!assetDatabase.TryLoadCookedAsset(mesh.Guid, variant, MeshAssetType, out var handle))
        {
            throw new InvalidOperationException(
                $"[MeshAssetCooker] Cooked mesh asset '{mesh.Guid}' variant '{variant}' is unavailable.");
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
                header.JointCount,
                header.SkinDataOffset,
                header.SkinDataSize,
                header.AnimationCount,
                header.AnimationDataOffset,
                header.AnimationDataSize,
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
        ValidateSkin(source.Vertices, source.Skin);
        bool skinned = source.Skin != null;
        uint vertexStride = skinned ? SkinnedMeshVertexStride : StaticMeshVertexStride;
        uint jointCount = skinned ? checked((uint)source.Skin!.Joints.Count) : 0u;
        var vertexDataSize = checked(source.Vertices.Length * (int)vertexStride);
        var indexDataSize = checked(source.Indices.Length * sizeof(uint));
        var submeshDataSize = checked(submeshes.Length * (int)SubmeshStride);
        uint skinDataSize = jointCount * SkinJointStride;
        uint skinDataOffset = skinDataSize > 0
            ? checked((uint)(HeaderSize + vertexDataSize + indexDataSize + submeshDataSize))
            : 0u;
        SourceAnimation[] animations = source.Animations ?? Array.Empty<SourceAnimation>();
        if (animations.Length > 0 && !skinned)
        {
            throw new InvalidOperationException(
                $"[MeshAssetCooker] Mesh source '{sourceAsset.SourcePath}' carries animations but no " +
                "skeleton; a clip animates the joints a skinned mesh cooks.");
        }

        uint animationDataSize = 0u;
        foreach (SourceAnimation animation in animations)
        {
            uint clipSize = AnimationClipHeaderStride;
            foreach (SourceAnimationTrack track in animation.Tracks)
            {
                uint keyStride = track.TranslationMode == MeshAnimationTranslationMode.PerKey
                    ? AnimationTranslatedKeyStride
                    : AnimationKeyStride;
                clipSize += AnimationTrackStride + checked((uint)track.Times.Length * keyStride);
            }

            animationDataSize += clipSize;
        }

        uint animationDataOffset = animations.Length > 0
            ? checked((uint)(HeaderSize + vertexDataSize + indexDataSize + submeshDataSize + skinDataSize))
            : 0u;

        using var stream = File.Create(outputPath);
        stream.Write(s_Magic);
        WriteInt32(stream, CookedFormatVersion);
        WriteInt32(stream, source.Vertices.Length);
        WriteInt32(stream, checked((int)vertexStride));
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
        WriteInt32(stream, checked((int)jointCount));
        WriteInt32(stream, checked((int)skinDataOffset));
        WriteInt32(stream, checked((int)skinDataSize));
        WriteInt32(stream, animations.Length);
        WriteInt32(stream, checked((int)animationDataOffset));
        WriteInt32(stream, checked((int)animationDataSize));

        Span<byte> vertexBytes = stackalloc byte[(int)SkinnedMeshVertexStride];
        vertexBytes.Clear();
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
            if (skinned)
            {
                vertexBytes[60] = checked((byte)vertex.JointX);
                vertexBytes[61] = checked((byte)vertex.JointY);
                vertexBytes[62] = checked((byte)vertex.JointZ);
                vertexBytes[63] = checked((byte)vertex.JointW);
                WriteSingle(vertexBytes, 64, vertex.WeightX);
                WriteSingle(vertexBytes, 68, vertex.WeightY);
                WriteSingle(vertexBytes, 72, vertex.WeightZ);
                WriteSingle(vertexBytes, 76, vertex.WeightW);
            }

            stream.Write(vertexBytes.Slice(0, checked((int)vertexStride)));
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

        if (skinned)
        {
            Span<byte> jointBytes = stackalloc byte[(int)SkinJointStride];
            foreach (MeshSkinJoint joint in source.Skin!.Joints)
            {
                jointBytes.Clear();
                BinaryPrimitives.WriteInt32LittleEndian(jointBytes.Slice(0, sizeof(int)), joint.ParentIndex);
                WriteSingle(jointBytes, 4, joint.LocalTranslation.X);
                WriteSingle(jointBytes, 8, joint.LocalTranslation.Y);
                WriteSingle(jointBytes, 12, joint.LocalTranslation.Z);
                WriteSingle(jointBytes, 16, joint.LocalRotation.X);
                WriteSingle(jointBytes, 20, joint.LocalRotation.Y);
                WriteSingle(jointBytes, 24, joint.LocalRotation.Z);
                WriteSingle(jointBytes, 28, joint.LocalRotation.W);
                WriteMatrix(jointBytes, 32, joint.InverseBind);
                stream.Write(jointBytes);
            }
        }

        if (animations.Length > 0)
        {
            var nameBytesBuffer = new byte[AnimationNameBytes];
            Span<byte> trackBytes = stackalloc byte[(int)AnimationTrackStride];
            Span<byte> keyBytes = stackalloc byte[(int)AnimationTranslatedKeyStride];
            foreach (SourceAnimation animation in animations)
            {
                Array.Clear(nameBytesBuffer);
                int nameLength = Math.Min(animation.Name.Length, AnimationNameBytes - 1);
                System.Text.Encoding.UTF8.GetBytes(animation.Name.AsSpan(0, nameLength), nameBytesBuffer);
                stream.Write(nameBytesBuffer);
                WriteSingle(stream, animation.Duration);
                WriteInt32(stream, checked((int)jointCount));
                WriteInt32(stream, animation.Tracks.Length);

                uint firstKeyOffset = AnimationClipHeaderStride +
                    checked((uint)animation.Tracks.Length * AnimationTrackStride);
                uint keyOffset = 0u;
                foreach (SourceAnimationTrack track in animation.Tracks)
                {
                    if (track.TranslationMode == MeshAnimationTranslationMode.PerKey &&
                        (track.Translations is null || track.Translations.Length != track.Times.Length))
                    {
                        throw new InvalidOperationException(
                            $"[MeshAssetCooker] Clip '{animation.Name}' track for joint " +
                            $"{track.JointIndex} declares per-key translations without keys.");
                    }

                    trackBytes.Clear();
                    BinaryPrimitives.WriteInt32LittleEndian(trackBytes.Slice(0, sizeof(int)), track.JointIndex);
                    BinaryPrimitives.WriteInt32LittleEndian(trackBytes.Slice(4, sizeof(int)), track.Times.Length);
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        trackBytes.Slice(8, sizeof(uint)),
                        firstKeyOffset + keyOffset);
                    BinaryPrimitives.WriteInt32LittleEndian(
                        trackBytes.Slice(12, sizeof(int)),
                        (int)track.TranslationMode);
                    WriteSingle(trackBytes, 16, track.ConstantTranslation.X);
                    WriteSingle(trackBytes, 20, track.ConstantTranslation.Y);
                    WriteSingle(trackBytes, 24, track.ConstantTranslation.Z);
                    stream.Write(trackBytes);
                    uint keyStride = track.TranslationMode == MeshAnimationTranslationMode.PerKey
                        ? AnimationTranslatedKeyStride
                        : AnimationKeyStride;
                    keyOffset += checked((uint)track.Times.Length * keyStride);
                }

                foreach (SourceAnimationTrack track in animation.Tracks)
                {
                    bool perKeyTranslations = track.TranslationMode == MeshAnimationTranslationMode.PerKey;
                    for (int key = 0; key < track.Times.Length; key++)
                    {
                        keyBytes.Clear();
                        WriteSingle(keyBytes, 0, track.Times[key]);
                        WriteSingle(keyBytes, 4, track.Rotations[key].X);
                        WriteSingle(keyBytes, 8, track.Rotations[key].Y);
                        WriteSingle(keyBytes, 12, track.Rotations[key].Z);
                        WriteSingle(keyBytes, 16, track.Rotations[key].W);
                        if (perKeyTranslations)
                        {
                            Vector3 translation = track.Translations![key];
                            WriteSingle(keyBytes, 20, translation.X);
                            WriteSingle(keyBytes, 24, translation.Y);
                            WriteSingle(keyBytes, 28, translation.Z);
                            stream.Write(keyBytes.Slice(0, (int)AnimationTranslatedKeyStride));
                        }
                        else
                        {
                            stream.Write(keyBytes.Slice(0, (int)AnimationKeyStride));
                        }
                    }
                }
            }
        }

        Logger.Log(
            $"[MeshAssetCooker] Cooked mesh asset {mesh.Guid} | Vertices: {source.Vertices.Length} | Indices: {source.Indices.Length} | Submeshes: {submeshes.Length} | Joints: {jointCount} | Bounds: {bounds.Min}->{bounds.Max} | Variant: {variant} | Output: {outputPath}");
    }

    private static void ValidateSkin(SourceVertex[] vertices, MeshSkin? skin)
    {
        bool anySkinnedVertex = false;
        bool allSkinnedVertices = true;
        foreach (SourceVertex vertex in vertices)
        {
            bool skinned = vertex.WeightX > 0.0f || vertex.WeightY > 0.0f ||
                           vertex.WeightZ > 0.0f || vertex.WeightW > 0.0f;
            anySkinnedVertex |= skinned;
            allSkinnedVertices &= skinned;
        }

        if (skin == null)
        {
            if (anySkinnedVertex)
            {
                throw new InvalidOperationException(
                    "[MeshAssetCooker] Mesh vertices carry skin weights, but the source declares no skeleton.");
            }

            return;
        }

        if (skin.Joints.Count == 0 || skin.Joints.Count > 255)
        {
            throw new InvalidOperationException(
                $"[MeshAssetCooker] Skeleton joint count '{skin.Joints.Count}' is outside 1..255.");
        }

        if (!allSkinnedVertices)
        {
            throw new InvalidOperationException(
                "[MeshAssetCooker] A skinned mesh requires joint indices and weights on every vertex.");
        }

        for (int index = 0; index < skin.Joints.Count; index++)
        {
            MeshSkinJoint joint = skin.Joints[index];
            if (joint.ParentIndex < -1 || joint.ParentIndex >= index)
            {
                throw new InvalidOperationException(
                    $"[MeshAssetCooker] Skeleton joint {index} declares parent '{joint.ParentIndex}'; " +
                    "joints must be ordered parents-first and only reference earlier joints.");
            }
        }

        foreach (SourceVertex vertex in vertices)
        {
            float weightSum = vertex.WeightX + vertex.WeightY + vertex.WeightZ + vertex.WeightW;
            if (!float.IsFinite(weightSum) || MathF.Abs(weightSum - 1.0f) > SkinWeightTolerance)
            {
                throw new InvalidOperationException(
                    $"[MeshAssetCooker] Skinned mesh weights must sum to 1, but a vertex weights sum to '{weightSum}'.");
            }

            foreach (int jointIndex in new[] { vertex.JointX, vertex.JointY, vertex.JointZ, vertex.JointW })
            {
                if (jointIndex >= skin.Joints.Count)
                {
                    throw new InvalidOperationException(
                        $"[MeshAssetCooker] A vertex references joint {jointIndex}, but the skeleton " +
                        $"has {skin.Joints.Count} joints.");
                }
            }
        }
    }

    private static void WriteMatrix(Span<byte> destination, int offset, Matrix4x4 matrix)
    {
        WriteSingle(destination, offset, matrix.M11);
        WriteSingle(destination, offset + 4, matrix.M12);
        WriteSingle(destination, offset + 8, matrix.M13);
        WriteSingle(destination, offset + 12, matrix.M14);
        WriteSingle(destination, offset + 16, matrix.M21);
        WriteSingle(destination, offset + 20, matrix.M22);
        WriteSingle(destination, offset + 24, matrix.M23);
        WriteSingle(destination, offset + 28, matrix.M24);
        WriteSingle(destination, offset + 32, matrix.M31);
        WriteSingle(destination, offset + 36, matrix.M32);
        WriteSingle(destination, offset + 40, matrix.M33);
        WriteSingle(destination, offset + 44, matrix.M34);
        WriteSingle(destination, offset + 48, matrix.M41);
        WriteSingle(destination, offset + 52, matrix.M42);
        WriteSingle(destination, offset + 56, matrix.M43);
        WriteSingle(destination, offset + 60, matrix.M44);
    }

    private static CookedMeshHeader ReadHeader(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < HeaderSize || !bytes.Slice(0, s_Magic.Length).SequenceEqual(s_Magic))
        {
            throw new InvalidOperationException("[MeshAssetCooker] Cooked mesh header magic is invalid.");
        }

        var version = ReadInt32(bytes, 8);
        if (version != CookedFormatVersion)
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
        var jointCount = ReadInt32(bytes, 68);
        var skinDataOffset = ReadInt32(bytes, 72);
        var skinDataSize = ReadInt32(bytes, 76);
        var animationCount = ReadInt32(bytes, 80);
        var animationDataOffset = ReadInt32(bytes, 84);
        var animationDataSize = ReadInt32(bytes, 88);
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

        if (vertexStride != StaticMeshVertexStride && vertexStride != SkinnedMeshVertexStride)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] Cooked mesh vertex stride '{vertexStride}' is not supported.");
        }

        if (jointCount < 0 || jointCount > 255 || skinDataOffset < 0 || skinDataSize < 0)
        {
            throw new InvalidOperationException("[MeshAssetCooker] Cooked mesh skin header contains invalid values.");
        }

        if (jointCount == 0)
        {
            if (vertexStride != StaticMeshVertexStride || skinDataOffset != 0 || skinDataSize != 0)
            {
                throw new InvalidOperationException("[MeshAssetCooker] Cooked mesh declares skin payload fields without joints.");
            }
        }
        else
        {
            if (vertexStride != SkinnedMeshVertexStride ||
                skinDataSize != checked(jointCount * (int)SkinJointStride) ||
                skinDataOffset != HeaderSize + vertexDataSize + indexDataSize + submeshDataSize)
            {
                throw new InvalidOperationException("[MeshAssetCooker] Cooked mesh skin payload layout is inconsistent with its header.");
            }
        }

        if (indexFormat != MeshIndexFormat.UInt32)
        {
            throw new NotSupportedException($"Mesh index format '{indexFormat}' is not supported by the RHI mesh uploader yet.");
        }

        if (submeshDataSize != checked(submeshCount * (int)SubmeshStride))
        {
            throw new InvalidOperationException("[MeshAssetCooker] Cooked mesh submesh payload size does not match submesh count.");
        }

        if (HeaderSize + vertexDataSize + indexDataSize + submeshDataSize + skinDataSize > bytes.Length)
        {
            throw new InvalidOperationException("[MeshAssetCooker] Cooked mesh payload is truncated.");
        }

        if (animationCount < 0 || animationDataOffset < 0 || animationDataSize < 0)
        {
            throw new InvalidOperationException("[MeshAssetCooker] Cooked mesh animation header contains invalid values.");
        }

        if (animationCount == 0)
        {
            if (animationDataOffset != 0 || animationDataSize != 0)
            {
                throw new InvalidOperationException("[MeshAssetCooker] Cooked mesh declares animation payload fields without clips.");
            }
        }
        else
        {
            if (jointCount == 0 ||
                animationDataOffset != HeaderSize + vertexDataSize + indexDataSize + submeshDataSize + skinDataSize ||
                animationDataOffset + animationDataSize > bytes.Length)
            {
                throw new InvalidOperationException("[MeshAssetCooker] Cooked mesh animation payload layout is inconsistent with its header.");
            }
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
            checked((uint)submeshDataSize),
            checked((uint)jointCount),
            checked((uint)skinDataOffset),
            checked((uint)skinDataSize),
            checked((uint)animationCount),
            checked((uint)animationDataOffset),
            checked((uint)animationDataSize));
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
                   ReadInt32(headerPrefix, 8) == CookedFormatVersion;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsValidBounds(MeshBounds bounds)
    {
        return float.IsFinite(bounds.Min.X) &&
               float.IsFinite(bounds.Min.Y) &&
               float.IsFinite(bounds.Min.Z) &&
               float.IsFinite(bounds.Max.X) &&
               float.IsFinite(bounds.Max.Y) &&
               float.IsFinite(bounds.Max.Z) &&
               bounds.Min.X <= bounds.Max.X &&
               bounds.Min.Y <= bounds.Max.Y &&
               bounds.Min.Z <= bounds.Max.Z;
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
        var joints = new List<MeshSkinJoint>();
        var currentJoint0 = 0;
        var currentJoint1 = 0;
        var currentJoint2 = 0;
        var currentJoint3 = 0;
        var currentWeight0 = 0.0f;
        var currentWeight1 = 0.0f;
        var currentWeight2 = 0.0f;
        var currentWeight3 = 0.0f;

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

                Vector3 vertexNormal;
                Vector4 vertexTangent;
                float u;
                float v;
                float colorR;
                float colorG;
                float colorB;
                if (parts.Length == 9)
                {
                    vertexNormal = s_DefaultNormal;
                    vertexTangent = s_DefaultTangent;
                    u = ReadFloat(parts[4]);
                    v = ReadFloat(parts[5]);
                    colorR = ReadFloat(parts[6]);
                    colorG = ReadFloat(parts[7]);
                    colorB = ReadFloat(parts[8]);
                }
                else if (parts.Length == 12)
                {
                    vertexNormal = NormalizeOrDefault(new Vector3(
                        ReadFloat(parts[4]),
                        ReadFloat(parts[5]),
                        ReadFloat(parts[6])));
                    vertexTangent = s_DefaultTangent;
                    u = ReadFloat(parts[7]);
                    v = ReadFloat(parts[8]);
                    colorR = ReadFloat(parts[9]);
                    colorG = ReadFloat(parts[10]);
                    colorB = ReadFloat(parts[11]);
                }
                else
                {
                    vertexNormal = NormalizeOrDefault(new Vector3(
                        ReadFloat(parts[4]),
                        ReadFloat(parts[5]),
                        ReadFloat(parts[6])));
                    vertexTangent = NormalizeTangentOrDefault(new Vector4(
                        ReadFloat(parts[7]),
                        ReadFloat(parts[8]),
                        ReadFloat(parts[9]),
                        ReadFloat(parts[10])));
                    u = ReadFloat(parts[11]);
                    v = ReadFloat(parts[12]);
                    colorR = ReadFloat(parts[13]);
                    colorG = ReadFloat(parts[14]);
                    colorB = ReadFloat(parts[15]);
                }

                vertices.Add(new SourceVertex(
                    ReadFloat(parts[1]),
                    ReadFloat(parts[2]),
                    ReadFloat(parts[3]),
                    vertexNormal.X,
                    vertexNormal.Y,
                    vertexNormal.Z,
                    vertexTangent.X,
                    vertexTangent.Y,
                    vertexTangent.Z,
                    vertexTangent.W,
                    u,
                    v,
                    colorR,
                    colorG,
                    colorB,
                    currentJoint0,
                    currentJoint1,
                    currentJoint2,
                    currentJoint3,
                    currentWeight0,
                    currentWeight1,
                    currentWeight2,
                    currentWeight3));
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

            if (string.Equals(parts[0], "joint", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length != 9)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] Mesh joint line in '{sourcePath}' must contain parentIndex, translation xyz, and rotation xyzw.");
                }

                if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parentIndex))
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] Invalid mesh joint parent token '{parts[1]}' in '{sourcePath}'.");
                }

                joints.Add(new MeshSkinJoint(
                    parentIndex,
                    new Vector3(ReadFloat(parts[2]), ReadFloat(parts[3]), ReadFloat(parts[4])),
                    ReadUnitQuaternion(sourcePath, parts, 5),
                    Matrix4x4.Identity));
                continue;
            }

            if (string.Equals(parts[0], "ibm", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length != 17)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] Mesh ibm line in '{sourcePath}' must contain 16 matrix values.");
                }

                if (joints.Count == 0)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] Mesh ibm line in '{sourcePath}' must follow a joint line.");
                }

                joints[^1] = joints[^1] with { InverseBind = ReadMatrix4x4(parts, 1) };
                continue;
            }

            if (string.Equals(parts[0], "joints", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length != 5)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] Mesh joints line in '{sourcePath}' must contain 4 joint indices.");
                }

                currentJoint0 = ReadJointIndex(sourcePath, parts[1]);
                currentJoint1 = ReadJointIndex(sourcePath, parts[2]);
                currentJoint2 = ReadJointIndex(sourcePath, parts[3]);
                currentJoint3 = ReadJointIndex(sourcePath, parts[4]);
                continue;
            }

            if (string.Equals(parts[0], "weights", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length != 5)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] Mesh weights line in '{sourcePath}' must contain 4 weight values.");
                }

                currentWeight0 = ReadFloat(parts[1]);
                currentWeight1 = ReadFloat(parts[2]);
                currentWeight2 = ReadFloat(parts[3]);
                currentWeight3 = ReadFloat(parts[4]);
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
        var skin = joints.Count > 0 ? new MeshSkin(joints.ToArray()) : null;
        return new SourceMesh(vertices.ToArray(), indices.ToArray(), sourceSubmeshes, skin);
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

        var skinState = new GltfSkinState(sourcePath, root);
        if (TryReadGltfSceneGraph(sourcePath, root, out var nodes, out var sceneRootNodes))
        {
            skinState.EnableSceneGraph(nodes, sceneRootNodes);
            var stack = new HashSet<int>();
            foreach (var rootNodeIndex in sceneRootNodes)
            {
                ImportGltfNode(
                    sourcePath,
                    nodes,
                    meshes,
                    buffers,
                    bufferViews,
                    accessors,
                    rootNodeIndex,
                    Matrix4x4.Identity,
                    stack,
                    skinState,
                    vertices,
                    indices,
                    submeshes,
                    materialSlots,
                    ref hasMissingNormals);
            }
        }
        else
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
                    nodeSkinned: false,
                    skinState: null,
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
        var skin = skinState.HasSkin ? new MeshSkin(skinState.Joints.ToArray()) : null;
        var animations = skin != null
            ? ReadGltfAnimations(sourcePath, root, skinState, buffers, bufferViews, accessors)
            : null;
        return new SourceMesh(sourceVertices, indices.ToArray(), sourceSubmeshes, skin, animations);
    }

    /// <summary>
    /// Bakes the source's clips for the cooked skeleton: a channel's target node maps to a cooked
    /// joint through the skin's slot map, LINEAR rotation and translation channels become key tracks
    /// (a joint animated on both carries one merged timeline plus an explicit translation mode), and
    /// scale channels - which the unit-scale pose slice cannot carry - are counted and reported
    /// instead of being silently lost. A non-LINEAR sampler is rejected because the cook would have
    /// to resample it into keys nobody authored.
    /// </summary>
    private static SourceAnimation[] ReadGltfAnimations(
        string sourcePath,
        JsonElement root,
        GltfSkinState skinState,
        byte[][] buffers,
        GltfBufferView[] bufferViews,
        GltfAccessor[] accessors)
    {
        if (!root.TryGetProperty("animations", out var animations) ||
            animations.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SourceAnimation>();
        }

        var result = new List<SourceAnimation>(animations.GetArrayLength());
        int droppedScaleChannels = 0;
        int droppedForeignChannels = 0;

        // Folding needs each node's rest transform and the nearest joint above it: a channel's keys are
        // local to the node they drive, and the cooked skeleton composes joint-relative locals.
        JsonElement animationNodes = skinState.Nodes;
        int nodeCount = animationNodes.GetArrayLength();
        int[] nodeParents = BuildGltfNodeParentMap(sourcePath, animationNodes);
        var animationJointSlots = new Dictionary<int, int>();
        if (root.TryGetProperty("skins", out var animationSkins) &&
            skinState.SkinIndex >= 0 &&
            skinState.SkinIndex < animationSkins.GetArrayLength() &&
            animationSkins[skinState.SkinIndex].TryGetProperty("joints", out var animationJoints))
        {
            foreach (JsonElement jointElement in animationJoints.EnumerateArray())
            {
                animationJointSlots.TryAdd(jointElement.GetInt32(), animationJointSlots.Count);
            }
        }

        var parentJointOf = new int[nodeCount];
        Array.Fill(parentJointOf, -1);
        var restTranslations = new Vector3[nodeCount];
        var restRotations = new Quaternion[nodeCount];
        for (int nodeIndex = 0; nodeIndex < nodeCount; nodeIndex++)
        {
            Matrix4x4 nodeTransform = ReadGltfNodeTransform(
                sourcePath,
                animationNodes[nodeIndex],
                $"nodes[{nodeIndex}]");
            if (!Matrix4x4.Decompose(nodeTransform, out Vector3 scale, out Quaternion rotation, out Vector3 translation) ||
                MathF.Abs(scale.X - 1.0f) > 1.0e-3f ||
                MathF.Abs(scale.Y - 1.0f) > 1.0e-3f ||
                MathF.Abs(scale.Z - 1.0f) > 1.0e-3f)
            {
                throw new NotSupportedException(
                    $"[MeshAssetCooker] glTF source '{sourcePath}' nodes[{nodeIndex}] does not reduce to " +
                    "translation and unit-scale rotation; a clip cannot fold it into the joint chain.");
            }

            restTranslations[nodeIndex] = translation;
            restRotations[nodeIndex] = rotation;
            int ancestor = nodeParents[nodeIndex];
            while (ancestor >= 0)
            {
                if (animationJointSlots.ContainsKey(ancestor))
                {
                    parentJointOf[nodeIndex] = ancestor;
                    break;
                }

                ancestor = nodeParents[ancestor];
            }
        }
        foreach (var animation in animations.EnumerateArray())
        {
            if (!animation.TryGetProperty("channels", out var channels) ||
                channels.ValueKind != JsonValueKind.Array ||
                !animation.TryGetProperty("samplers", out var samplers) ||
                samplers.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var rotationByNode = new Dictionary<int, (float[] Times, Quaternion[] Rotations)>(channels.GetArrayLength());
            var translationByNode = new Dictionary<int, (float[] Times, Vector3[] Values)>(channels.GetArrayLength());
            float duration = 0.0f;
            foreach (var channel in channels.EnumerateArray())
            {
                if (!channel.TryGetProperty("target", out var target) ||
                    !target.TryGetProperty("node", out var nodeElement) ||
                    !target.TryGetProperty("path", out var pathElement))
                {
                    continue;
                }

                string path = pathElement.GetString() ?? string.Empty;
                if (string.Equals(path, "scale", StringComparison.Ordinal))
                {
                    droppedScaleChannels++;
                    continue;
                }

                bool isRotation = string.Equals(path, "rotation", StringComparison.Ordinal);
                bool isTranslation = string.Equals(path, "translation", StringComparison.Ordinal);
                if (!isRotation && !isTranslation)
                {
                    droppedForeignChannels++;
                    continue;
                }

                int nodeIndex = nodeElement.GetInt32();
                int jointIndex = nodeIndex >= 0 && nodeIndex < skinState.CookedSlotOfNode.Length
                    ? skinState.CookedSlotOfNode[nodeIndex]
                    : -1;
                if (jointIndex < 0)
                {
                    // A channel on a node the character no longer carries (a dropped accessory, or a
                    // node outside the skeleton) animates nothing the cooked mesh can show.
                    droppedForeignChannels++;
                    continue;
                }

                if (!channel.TryGetProperty("sampler", out var samplerElement))
                {
                    continue;
                }

                int samplerIndex = samplerElement.GetInt32();
                if (samplerIndex < 0 || samplerIndex >= samplers.GetArrayLength())
                {
                    throw new InvalidOperationException(
                        $"[MeshAssetCooker] glTF source '{sourcePath}' animation channel references " +
                        $"sampler '{samplerIndex}' outside sampler count '{samplers.GetArrayLength()}'.");
                }

                JsonElement sampler = samplers[samplerIndex];
                if (sampler.TryGetProperty("interpolation", out var interpolationElement) &&
                    interpolationElement.GetString() is { Length: > 0 } interpolation &&
                    !string.Equals(interpolation, "LINEAR", StringComparison.Ordinal))
                {
                    throw new NotSupportedException(
                        $"[MeshAssetCooker] glTF source '{sourcePath}' animation sampler interpolates " +
                        $"'{interpolation}'; the clip cook bakes LINEAR rotation and translation channels only.");
                }

                GltfAccessor times = GetGltfAccessor(sourcePath, accessors, sampler.GetProperty("input").GetInt32());
                GltfAccessor values = GetGltfAccessor(sourcePath, accessors, sampler.GetProperty("output").GetInt32());
                string expectedValueType = isRotation ? "VEC4" : "VEC3";
                if (times.Type != "SCALAR" || times.ComponentType != GltfComponentType.Float ||
                    values.Type != expectedValueType || values.ComponentType != GltfComponentType.Float ||
                    times.Count != values.Count)
                {
                    throw new InvalidOperationException(
                        $"[MeshAssetCooker] glTF source '{sourcePath}' animation sampler must pair FLOAT " +
                        $"SCALAR times with FLOAT {expectedValueType} {path} values of the same count.");
                }

                var keyTimes = new float[times.Count];
                if (isRotation)
                {
                    var keyRotations = new Quaternion[values.Count];
                    for (int key = 0; key < times.Count; key++)
                    {
                        var timeSpan = GetGltfElementSpan(sourcePath, buffers, bufferViews, times, key, 1);
                        keyTimes[key] = BinaryPrimitives.ReadSingleLittleEndian(timeSpan.Slice(0, sizeof(float)));
                        Vector4 rotation = ReadGltfVector4(sourcePath, buffers, bufferViews, values, key);
                        var keyed = new Quaternion(rotation.X, rotation.Y, rotation.Z, rotation.W);
                        float lengthSquared = keyed.LengthSquared();
                        if (!float.IsFinite(lengthSquared) || lengthSquared < 1.0e-8f)
                        {
                            throw new InvalidOperationException(
                                $"[MeshAssetCooker] glTF source '{sourcePath}' animation rotation key {key} " +
                                "is not a non-zero quaternion.");
                        }

                        keyRotations[key] = Quaternion.Normalize(keyed);
                        duration = MathF.Max(duration, keyTimes[key]);
                        if (key > 0 && keyTimes[key] < keyTimes[key - 1])
                        {
                            throw new InvalidOperationException(
                                $"[MeshAssetCooker] glTF source '{sourcePath}' animation key times must " +
                                "increase; the sampler searches them.");
                        }
                    }

                    rotationByNode[nodeIndex] = (keyTimes, keyRotations);
                }
                else
                {
                    var keyValues = new Vector3[values.Count];
                    for (int key = 0; key < times.Count; key++)
                    {
                        var timeSpan = GetGltfElementSpan(sourcePath, buffers, bufferViews, times, key, 1);
                        keyTimes[key] = BinaryPrimitives.ReadSingleLittleEndian(timeSpan.Slice(0, sizeof(float)));
                        Vector3 value = ReadGltfVector3(sourcePath, buffers, bufferViews, values, key);
                        if (!float.IsFinite(value.X) || !float.IsFinite(value.Y) || !float.IsFinite(value.Z))
                        {
                            throw new InvalidOperationException(
                                $"[MeshAssetCooker] glTF source '{sourcePath}' animation translation key " +
                                $"{key} is not finite.");
                        }

                        keyValues[key] = value;
                        duration = MathF.Max(duration, keyTimes[key]);
                        if (key > 0 && keyTimes[key] < keyTimes[key - 1])
                        {
                            throw new InvalidOperationException(
                                $"[MeshAssetCooker] glTF source '{sourcePath}' animation key times must " +
                                "increase; the sampler searches them.");
                        }
                    }

                    translationByNode[nodeIndex] = (keyTimes, keyValues);
                }
            }

            // One track per animated joint, ordered by the cooked joint index so the payload is
            // deterministic across channel orderings. A joint animated on both channels carries one
            // merged key timeline: LINEAR keys interleave exactly, and resampling either channel onto
            // the merged times reproduces the authored curve.
            var animatedJoints = new List<(int JointIndex, int NodeIndex)>(rotationByNode.Count + translationByNode.Count);
            foreach (int node in rotationByNode.Keys)
            {
                animatedJoints.Add((skinState.CookedSlotOfNode[node], node));
            }

            foreach (int node in translationByNode.Keys)
            {
                if (!rotationByNode.ContainsKey(node))
                {
                    animatedJoints.Add((skinState.CookedSlotOfNode[node], node));
                }
            }

            animatedJoints.Sort(static (left, right) => left.JointIndex.CompareTo(right.JointIndex));
            var tracks = new List<SourceAnimationTrack>(animatedJoints.Count);
            var chain = new List<int>(8);
            foreach ((int jointIndex, int jointNode) in animatedJoints)
            {
                float[] times;
                Quaternion[] rotations;
                Vector3[]? translations;
                if (rotationByNode.TryGetValue(jointNode, out var rotationChannel))
                {
                    if (translationByNode.TryGetValue(jointNode, out var translationChannel))
                    {
                        times = MergeKeyTimes(rotationChannel.Times, translationChannel.Times);
                        rotations = ResampleRotationChannel(rotationByNode, jointNode, times, restRotations[jointNode]);
                        translations = ResampleTranslationChannel(translationByNode, jointNode, times, restTranslations[jointNode]);
                    }
                    else
                    {
                        times = rotationChannel.Times;
                        rotations = rotationChannel.Rotations;
                        translations = null;
                    }
                }
                else
                {
                    (float[] Times, Vector3[] Values) translationChannel = translationByNode[jointNode];
                    times = translationChannel.Times;
                    rotations = FillRestRotations(restRotations[jointNode], times.Length);
                    translations = translationChannel.Values;
                }

                var foldedRotations = new Quaternion[times.Length];
                var foldedTranslations = new Vector3[times.Length];
                for (int key = 0; key < times.Length; key++)
                {
                    float time = times[key];
                    chain.Clear();
                    int current = jointNode;
                    while (true)
                    {
                        chain.Add(current);
                        int parent = nodeParents[current];
                        if (parent < 0 ||
                            parent == parentJointOf[jointNode] ||
                            parentJointOf[jointNode] < 0)
                        {
                            break;
                        }

                        current = parent;
                    }

                    Matrix4x4 folded = Matrix4x4.Identity;
                    for (int index = chain.Count - 1; index >= 0; index--)
                    {
                        int node = chain[index];
                        Quaternion rotation = SampleChannelRotation(
                            rotationByNode,
                            node,
                            time,
                            restRotations[node]);
                        Vector3 translation = SampleChannelTranslation(
                            translationByNode,
                            node,
                            time,
                            restTranslations[node]);
                        folded = Matrix4x4.CreateFromQuaternion(rotation) *
                            Matrix4x4.CreateTranslation(translation) *
                            folded;
                    }

                    if (!Matrix4x4.Decompose(
                            folded,
                            out Vector3 foldedScale,
                            out Quaternion foldedRotation,
                            out Vector3 foldedTranslation) ||
                        MathF.Abs(foldedScale.X - 1.0f) > 1.0e-3f ||
                        MathF.Abs(foldedScale.Y - 1.0f) > 1.0e-3f ||
                        MathF.Abs(foldedScale.Z - 1.0f) > 1.0e-3f)
                    {
                        throw new NotSupportedException(
                            $"[MeshAssetCooker] glTF source '{sourcePath}' joint node '{jointNode}' does not " +
                            "reduce to rotation and translation once its key track is folded into the joint chain.");
                    }

                    foldedRotations[key] = foldedRotation;
                    foldedTranslations[key] = foldedTranslation;
                }

                // Translation keys are stored only when the joint actually moves: a track that repeats
                // its rest translation - every joint but the roots, for the shipped rigs - costs
                // nothing, and one constant value covers a joint that rests somewhere else.
                Vector3 restLocal = skinState.Joints[jointIndex].LocalTranslation;
                bool allRest = true;
                bool allEqual = true;
                for (int key = 0; key < foldedTranslations.Length; key++)
                {
                    allEqual &= Vector3.DistanceSquared(foldedTranslations[key], foldedTranslations[0]) <= 1.0e-8f;
                    allRest &= Vector3.DistanceSquared(foldedTranslations[key], restLocal) <= 1.0e-8f;
                }

                MeshAnimationTranslationMode translationMode;
                Vector3 constantTranslation = default;
                Vector3[]? keyTranslations = null;
                if (allRest)
                {
                    translationMode = MeshAnimationTranslationMode.Rest;
                }
                else if (allEqual)
                {
                    translationMode = MeshAnimationTranslationMode.Constant;
                    constantTranslation = foldedTranslations[0];
                }
                else
                {
                    translationMode = MeshAnimationTranslationMode.PerKey;
                    keyTranslations = foldedTranslations;
                }

                tracks.Add(new SourceAnimationTrack(
                    jointIndex,
                    times,
                    foldedRotations,
                    translationMode,
                    constantTranslation,
                    keyTranslations));
            }


            if (tracks.Count == 0)
            {
                continue;
            }

            string name = animation.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new InvalidOperationException(
                    $"[MeshAssetCooker] glTF source '{sourcePath}' declares an animation without a name; " +
                    "a clip is addressed by its name.");
            }

            result.Add(new SourceAnimation(name, duration, tracks.ToArray()));
        }

        if (droppedScaleChannels > 0 || droppedForeignChannels > 0)
        {
            Logger.Warning(
                $"[MeshAssetCooker] Source '{sourcePath}' bakes {result.Count} clip(s); dropped " +
                $"{droppedScaleChannels} scale channel(s) and {droppedForeignChannels} channel(s) " +
                "outside the cooked skeleton. The pose this payload feeds stores unit-scale joints.");
        }

        return result.ToArray();
    }

    /// <summary>
    /// A chain node's rotation at a clip time: the node's own key track when it has one, otherwise its
    /// rest rotation. Folding uses this for the joint being baked and for every helper between it and
    /// its parent joint.
    /// </summary>
    private static Quaternion SampleChannelRotation(
        Dictionary<int, (float[] Times, Quaternion[] Rotations)> channels,
        int node,
        float time,
        Quaternion rest)
    {
        if (!channels.TryGetValue(node, out var channel) || channel.Times.Length == 0)
        {
            return rest;
        }

        ReadOnlySpan<float> times = channel.Times;
        if (time <= times[0])
        {
            return channel.Rotations[0];
        }

        if (time >= times[^1])
        {
            return channel.Rotations[^1];
        }

        int low = 0;
        int high = times.Length - 1;
        while (high - low > 1)
        {
            int middle = (low + high) / 2;
            if (times[middle] <= time)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        float span = times[high] - times[low];
        return span > 0.0f
            ? Quaternion.Slerp(channel.Rotations[low], channel.Rotations[high], (time - times[low]) / span)
            : channel.Rotations[low];
    }

    /// <summary>
    /// A chain node's local translation at a clip time: the node's own translation keys when it has
    /// them, otherwise its rest translation. The folded key's translation is what lets a root joint's
    /// motion - a walk's hip bob, a jump's arc - survive the cook.
    /// </summary>
    private static Vector3 SampleChannelTranslation(
        Dictionary<int, (float[] Times, Vector3[] Values)> channels,
        int node,
        float time,
        Vector3 rest)
    {
        if (!channels.TryGetValue(node, out var channel) || channel.Times.Length == 0)
        {
            return rest;
        }

        ReadOnlySpan<float> times = channel.Times;
        if (time <= times[0])
        {
            return channel.Values[0];
        }

        if (time >= times[^1])
        {
            return channel.Values[^1];
        }

        int low = 0;
        int high = times.Length - 1;
        while (high - low > 1)
        {
            int middle = (low + high) / 2;
            if (times[middle] <= time)
            {
                low = middle;
            }
            else
            {
                high = middle;
            }
        }

        float span = times[high] - times[low];
        return span > 0.0f
            ? Vector3.Lerp(channel.Values[low], channel.Values[high], (time - times[low]) / span)
            : channel.Values[low];
    }

    /// <summary>
    /// The merged key timeline of two LINEAR channels: both timelines are ascending, equal times
    /// collapse, and every source key survives, so resampling either channel onto the merged times
    /// reproduces the authored curve exactly.
    /// </summary>
    private static float[] MergeKeyTimes(float[] first, float[] second)
    {
        const float Epsilon = 1.0e-5f;
        var merged = new List<float>(first.Length + second.Length);
        int firstIndex = 0;
        int secondIndex = 0;
        while (firstIndex < first.Length || secondIndex < second.Length)
        {
            float next;
            if (firstIndex < first.Length && secondIndex < second.Length)
            {
                if (MathF.Abs(first[firstIndex] - second[secondIndex]) <= Epsilon)
                {
                    next = first[firstIndex];
                    firstIndex++;
                    secondIndex++;
                }
                else if (first[firstIndex] < second[secondIndex])
                {
                    next = first[firstIndex++];
                }
                else
                {
                    next = second[secondIndex++];
                }
            }
            else if (firstIndex < first.Length)
            {
                next = first[firstIndex++];
            }
            else
            {
                next = second[secondIndex++];
            }

            if (merged.Count == 0 || next - merged[^1] > Epsilon)
            {
                merged.Add(next);
            }
        }

        return merged.ToArray();
    }

    private static Quaternion[] ResampleRotationChannel(
        Dictionary<int, (float[] Times, Quaternion[] Rotations)> channels,
        int node,
        float[] times,
        Quaternion rest)
    {
        var sampled = new Quaternion[times.Length];
        for (int key = 0; key < times.Length; key++)
        {
            sampled[key] = SampleChannelRotation(channels, node, times[key], rest);
        }

        return sampled;
    }

    private static Vector3[] ResampleTranslationChannel(
        Dictionary<int, (float[] Times, Vector3[] Values)> channels,
        int node,
        float[] times,
        Vector3 rest)
    {
        var sampled = new Vector3[times.Length];
        for (int key = 0; key < times.Length; key++)
        {
            sampled[key] = SampleChannelTranslation(channels, node, times[key], rest);
        }

        return sampled;
    }

    private static Quaternion[] FillRestRotations(Quaternion rest, int count)
    {
        var rotations = new Quaternion[count];
        Array.Fill(rotations, rest);
        return rotations;
    }

    private static bool TryReadGltfSceneGraph(
        string sourcePath,
        JsonElement root,
        out JsonElement nodes,
        out int[] sceneRootNodes)
    {
        nodes = default;
        sceneRootNodes = Array.Empty<int>();
        if (!root.TryGetProperty("nodes", out nodes) ||
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

        var roots = new List<int>(rootNodes.GetArrayLength());
        foreach (var rootNode in rootNodes.EnumerateArray())
        {
            roots.Add(rootNode.GetInt32());
        }

        sceneRootNodes = roots.ToArray();
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
        GltfSkinState skinState,
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
            bool nodeSkinned = node.TryGetProperty("skin", out var skinElement);
            if (nodeSkinned && node.TryGetProperty("mesh", out _))
            {
                skinState.Activate(nodeIndex, skinElement.GetInt32(), buffers, bufferViews, accessors);
            }

            if (node.TryGetProperty("mesh", out var meshElement))
            {
                var meshIndex = meshElement.GetInt32();
                if (meshIndex < 0 || meshIndex >= meshes.GetArrayLength())
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' nodes[{nodeIndex}].mesh index '{meshIndex}' is outside mesh count '{meshes.GetArrayLength()}'.");
                }

                if (!nodeSkinned)
                {
                    skinState.DeclareStaticMeshNode(nodeIndex);
                }

                ImportGltfMeshPrimitives(
                    sourcePath,
                    meshes[meshIndex],
                    buffers,
                    bufferViews,
                    accessors,
                    nodeTransform,
                    nodeSkinned,
                    skinState,
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
                        skinState,
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
        bool nodeSkinned,
        GltfSkinState? skinState,
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
            var jointsAccessorIndex = TryGetGltfAttributeAccessor(attributes, "JOINTS_0");
            var weightsAccessorIndex = TryGetGltfAttributeAccessor(attributes, "WEIGHTS_0");
            bool primitiveSkinned = jointsAccessorIndex >= 0;
            if (primitiveSkinned != (weightsAccessorIndex >= 0))
            {
                throw new InvalidOperationException($"[MeshAssetCooker] glTF primitive in '{sourcePath}' must declare JOINTS_0 and WEIGHTS_0 together.");
            }

            if (primitiveSkinned && (!nodeSkinned || skinState is null))
            {
                throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' primitive carries JOINTS_0/WEIGHTS_0 but its instancing node declares no skin.");
            }

            if (!primitiveSkinned && nodeSkinned)
            {
                throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' node declares a skin but its mesh primitive lacks JOINTS_0/WEIGHTS_0.");
            }

            GltfAccessor jointsAccessor = default;
            GltfAccessor weightsAccessor = default;
            if (primitiveSkinned)
            {
                jointsAccessor = GetGltfAccessor(sourcePath, accessors, jointsAccessorIndex);
                if (jointsAccessor.Type != "VEC4" ||
                    jointsAccessor.ComponentType is not (GltfComponentType.UnsignedByte or GltfComponentType.UnsignedShort))
                {
                    throw new NotSupportedException($"[MeshAssetCooker] glTF JOINTS_0 in '{sourcePath}' must be UNSIGNED_BYTE or UNSIGNED_SHORT VEC4.");
                }

                weightsAccessor = GetGltfAccessor(sourcePath, accessors, weightsAccessorIndex);
                if (weightsAccessor.Type != "VEC4" ||
                    weightsAccessor.ComponentType is not (GltfComponentType.Float or GltfComponentType.UnsignedByte or GltfComponentType.UnsignedShort))
                {
                    throw new NotSupportedException($"[MeshAssetCooker] glTF WEIGHTS_0 in '{sourcePath}' must be FLOAT, UNSIGNED_BYTE, or UNSIGNED_SHORT VEC4.");
                }

                if (weightsAccessor.ComponentType != GltfComponentType.Float && !weightsAccessor.Normalized)
                {
                    throw new NotSupportedException($"[MeshAssetCooker] glTF WEIGHTS_0 in '{sourcePath}' must be normalized when it uses integer components.");
                }

                if (jointsAccessor.Count != positionAccessor.Count || weightsAccessor.Count != positionAccessor.Count)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' JOINTS_0/WEIGHTS_0 counts must match the POSITION count '{positionAccessor.Count}'.");
                }
            }
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

                int joint0 = 0;
                int joint1 = 0;
                int joint2 = 0;
                int joint3 = 0;
                float weight0 = 0.0f;
                float weight1 = 0.0f;
                float weight2 = 0.0f;
                float weight3 = 0.0f;
                if (primitiveSkinned)
                {
                    var weights = ReadGltfWeights(sourcePath, buffers, bufferViews, weightsAccessor, i);
                    float weightSum = weights.X + weights.Y + weights.Z + weights.W;
                    if (weights.X < 0.0f || weights.Y < 0.0f || weights.Z < 0.0f || weights.W < 0.0f ||
                        !float.IsFinite(weightSum) ||
                        MathF.Abs(weightSum - 1.0f) > SkinWeightTolerance)
                    {
                        throw new InvalidOperationException(
                            $"[MeshAssetCooker] glTF source '{sourcePath}' skinned vertex '{i}' has invalid weights " +
                            $"({weights.X}, {weights.Y}, {weights.Z}, {weights.W}).");
                    }

                    joint0 = ResolveGltfJointIndex(sourcePath, skinState!, jointsAccessor, buffers, bufferViews, i, 0);
                    joint1 = ResolveGltfJointIndex(sourcePath, skinState!, jointsAccessor, buffers, bufferViews, i, 1);
                    joint2 = ResolveGltfJointIndex(sourcePath, skinState!, jointsAccessor, buffers, bufferViews, i, 2);
                    joint3 = ResolveGltfJointIndex(sourcePath, skinState!, jointsAccessor, buffers, bufferViews, i, 3);
                    weight0 = weights.X / weightSum;
                    weight1 = weights.Y / weightSum;
                    weight2 = weights.Z / weightSum;
                    weight3 = weights.W / weightSum;
                }

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
                    color.Z,
                    joint0,
                    joint1,
                    joint2,
                    joint3,
                    weight0,
                    weight1,
                    weight2,
                    weight3));
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

    /// <summary>
    /// Accumulates the single skin the first skinning slice imports. The cooked skeleton is ordered
    /// parents-first and vertex joint slots are remapped into that same order, so a palette can be
    /// resolved in one pass.
    /// </summary>
    private sealed class GltfSkinState
    {
        public GltfSkinState(string sourcePath, JsonElement root)
        {
            SourcePath = sourcePath;
            Root = root;
        }

        public string SourcePath { get; }

        public JsonElement Root { get; }

        public JsonElement Nodes { get; private set; }

        public int[] SceneRootNodes { get; private set; } = Array.Empty<int>();

        public int NodeIndex { get; private set; } = -1;

        public int SkinIndex { get; private set; } = -1;

        public int StaticMeshNodeIndex { get; private set; } = -1;

        public bool HasSkin => SkinIndex >= 0;

        public List<MeshSkinJoint> Joints { get; } = new();

        public int[] VertexJointRemap { get; private set; } = Array.Empty<int>();

        /// <summary>
        /// The cooked joint index each source node landed on, or -1 for a node that is not a joint.
        /// A clip's channels target nodes, so this is what maps them into the cooked skeleton.
        /// </summary>
        public int[] CookedSlotOfNode { get; private set; } = Array.Empty<int>();

        public void EnableSceneGraph(JsonElement nodes, int[] sceneRootNodes)
        {
            Nodes = nodes;
            SceneRootNodes = sceneRootNodes;
        }

        public void DeclareStaticMeshNode(int nodeIndex)
        {
            if (HasSkin)
            {
                throw MixesStaticAndSkinned(nodeIndex, NodeIndex);
            }

            StaticMeshNodeIndex = nodeIndex;
        }

        public void Activate(
            int nodeIndex,
            int skinIndex,
            byte[][] buffers,
            GltfBufferView[] bufferViews,
            GltfAccessor[] accessors)
        {
            if (StaticMeshNodeIndex >= 0)
            {
                throw MixesStaticAndSkinned(StaticMeshNodeIndex, nodeIndex);
            }

            if (HasSkin)
            {
                if (SkinIndex != skinIndex)
                {
                    throw new NotSupportedException(
                        $"[MeshAssetCooker] glTF source '{SourcePath}' uses skins '{SkinIndex}' and '{skinIndex}'; " +
                        "the first skinning slice imports a single skin per source.");
                }

                if (NodeIndex != nodeIndex)
                {
                    throw new NotSupportedException(
                        $"[MeshAssetCooker] glTF source '{SourcePath}' instances skin '{skinIndex}' on nodes " +
                        $"'{NodeIndex}' and '{nodeIndex}'; the first skinning slice imports a single skinned node per source.");
                }

                return;
            }

            ImportGltfSkin(SourcePath, this, nodeIndex, skinIndex, buffers, bufferViews, accessors);
            NodeIndex = nodeIndex;
            SkinIndex = skinIndex;
        }

        public void SetSkin(List<MeshSkinJoint> joints, int[] vertexJointRemap, int[] cookedSlotOfNode)
        {
            Joints.Clear();
            Joints.AddRange(joints);
            VertexJointRemap = vertexJointRemap;
            CookedSlotOfNode = cookedSlotOfNode;
        }

        private NotSupportedException MixesStaticAndSkinned(int staticNodeIndex, int skinnedNodeIndex)
        {
            return new NotSupportedException(
                $"[MeshAssetCooker] glTF source '{SourcePath}' mixes static mesh node '{staticNodeIndex}' with " +
                $"skinned node '{skinnedNodeIndex}'; the first skinning slice imports either a fully static or a fully skinned source.");
        }
    }

    private static void ImportGltfSkin(
        string sourcePath,
        GltfSkinState state,
        int nodeIndex,
        int skinIndex,
        byte[][] buffers,
        GltfBufferView[] bufferViews,
        GltfAccessor[] accessors)
    {
        JsonElement root = state.Root;
        JsonElement nodes = state.Nodes;
        if (!root.TryGetProperty("skins", out var skins) || skins.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' nodes[{nodeIndex}].skin references skin '{skinIndex}', but the document declares no skins array.");
        }

        if (skinIndex < 0 || skinIndex >= skins.GetArrayLength())
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' nodes[{nodeIndex}].skin index '{skinIndex}' is outside skin count '{skins.GetArrayLength()}'.");
        }

        var skin = skins[skinIndex];
        if (!skin.TryGetProperty("joints", out var jointElements) || jointElements.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' skins[{skinIndex}].joints must be an array.");
        }

        int jointCount = jointElements.GetArrayLength();
        if (jointCount == 0 || jointCount > 255)
        {
            throw new NotSupportedException($"[MeshAssetCooker] glTF source '{sourcePath}' skins[{skinIndex}] declares '{jointCount}' joints; the first skinning slice supports 1..255.");
        }

        var jointNodes = new int[jointCount];
        var jointSlots = new Dictionary<int, int>(jointCount);
        for (int slot = 0; slot < jointCount; slot++)
        {
            int jointNode = jointElements[slot].GetInt32();
            if (jointNode < 0 || jointNode >= nodes.GetArrayLength())
            {
                throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' skins[{skinIndex}].joints[{slot}] index '{jointNode}' is outside node count '{nodes.GetArrayLength()}'.");
            }

            if (!jointSlots.TryAdd(jointNode, slot))
            {
                throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' skins[{skinIndex}] references node '{jointNode}' more than once.");
            }

            jointNodes[slot] = jointNode;
        }

        var parents = BuildGltfNodeParentMap(sourcePath, nodes);
        var sceneRoots = new HashSet<int>(state.SceneRootNodes);
        var globals = new Matrix4x4[jointCount];
        var depths = new int[jointCount];
        var buckets = new List<int>[jointCount];
        for (int slot = 0; slot < jointCount; slot++)
        {
            globals[slot] = GetGltfJointGlobalTransform(
                sourcePath,
                nodes,
                parents,
                sceneRoots,
                jointSlots,
                jointNodes[slot]);
            depths[slot] = CountGltfJointAncestors(sourcePath, parents, jointSlots, jointNodes[slot], nodes.GetArrayLength());
            (buckets[depths[slot]] ??= new List<int>()).Add(slot);
        }

        var orderedSlots = new List<int>(jointCount);
        foreach (var bucket in buckets)
        {
            if (bucket != null)
            {
                orderedSlots.AddRange(bucket);
            }
        }

        var cookedOfSlot = new int[jointCount];
        for (int cookedIndex = 0; cookedIndex < orderedSlots.Count; cookedIndex++)
        {
            cookedOfSlot[orderedSlots[cookedIndex]] = cookedIndex;
        }

        GltfAccessor? inverseBindAccessor = null;
        if (skin.TryGetProperty("inverseBindMatrices", out var inverseBindElement))
        {
            inverseBindAccessor = GetGltfAccessor(sourcePath, accessors, inverseBindElement.GetInt32());
            if (inverseBindAccessor.Value.Type != "MAT4" || inverseBindAccessor.Value.ComponentType != GltfComponentType.Float)
            {
                throw new NotSupportedException($"[MeshAssetCooker] glTF source '{sourcePath}' skins[{skinIndex}].inverseBindMatrices must be FLOAT MAT4.");
            }

            if (inverseBindAccessor.Value.Count < jointCount)
            {
                throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' skins[{skinIndex}].inverseBindMatrices has '{inverseBindAccessor.Value.Count}' entries for '{jointCount}' joints.");
            }
        }

        var joints = new List<MeshSkinJoint>(jointCount);
        foreach (int slot in orderedSlots)
        {
            int parentSlot = FindGltfNearestJointAncestor(sourcePath, parents, jointSlots, jointNodes[slot], nodes.GetArrayLength());
            Matrix4x4 localRest = parentSlot >= 0
                ? globals[slot] * InvertGltfMatrix(sourcePath, globals[parentSlot], $"skins[{skinIndex}] joint '{jointNodes[slot]}'")
                : globals[slot];
            if (!Matrix4x4.Decompose(localRest, out var scale, out var rotation, out var translation) ||
                MathF.Abs(scale.X - 1.0f) > JointScaleTolerance ||
                MathF.Abs(scale.Y - 1.0f) > JointScaleTolerance ||
                MathF.Abs(scale.Z - 1.0f) > JointScaleTolerance)
            {
                throw new NotSupportedException(
                    $"[MeshAssetCooker] glTF source '{sourcePath}' skins[{skinIndex}] joint node '{jointNodes[slot]}' does not reduce to " +
                    "translation, rotation, and unit scale; the first skinning slice stores unit-scale joints only.");
            }

            Matrix4x4 inverseBind = inverseBindAccessor.HasValue
                ? ReadGltfMatrix4(sourcePath, buffers, bufferViews, inverseBindAccessor.Value, slot, $"skins[{skinIndex}].inverseBindMatrices[{slot}]")
                : Matrix4x4.Identity;

            joints.Add(new MeshSkinJoint(
                parentSlot >= 0 ? cookedOfSlot[parentSlot] : -1,
                translation,
                rotation,
                inverseBind));
        }

        var cookedSlotOfNode = new int[nodes.GetArrayLength()];
        Array.Fill(cookedSlotOfNode, -1);
        for (int slot = 0; slot < jointCount; slot++)
        {
            cookedSlotOfNode[jointNodes[slot]] = cookedOfSlot[slot];
        }

        state.SetSkin(joints, cookedOfSlot, cookedSlotOfNode);
    }

    private static int[] BuildGltfNodeParentMap(string sourcePath, JsonElement nodes)
    {
        var parents = new int[nodes.GetArrayLength()];
        Array.Fill(parents, -1);
        for (int nodeIndex = 0; nodeIndex < parents.Length; nodeIndex++)
        {
            if (!nodes[nodeIndex].TryGetProperty("children", out var children))
            {
                continue;
            }

            if (children.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' nodes[{nodeIndex}].children must be an array.");
            }

            foreach (var child in children.EnumerateArray())
            {
                int childIndex = child.GetInt32();
                if (childIndex < 0 || childIndex >= parents.Length)
                {
                    throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' nodes[{nodeIndex}].children index '{childIndex}' is outside node count '{parents.Length}'.");
                }

                if (parents[childIndex] != -1)
                {
                    throw new NotSupportedException($"[MeshAssetCooker] glTF source '{sourcePath}' node '{childIndex}' is a child of multiple parents; the skinning importer requires a node tree.");
                }

                parents[childIndex] = nodeIndex;
            }
        }

        return parents;
    }

    private static Matrix4x4 GetGltfJointGlobalTransform(
        string sourcePath,
        JsonElement nodes,
        int[] parents,
        HashSet<int> sceneRoots,
        Dictionary<int, int> jointSlots,
        int nodeIndex)
    {
        var chain = new List<int>();
        int current = nodeIndex;
        while (current >= 0)
        {
            chain.Add(current);
            if (chain.Count > parents.Length)
            {
                throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' contains a node hierarchy cycle above node '{nodeIndex}'.");
            }

            current = parents[current];
        }

        int top = chain[^1];
        if (!sceneRoots.Contains(top))
        {
            throw new NotSupportedException(
                $"[MeshAssetCooker] glTF source '{sourcePath}' joint node '{nodeIndex}' is rooted at node '{top}', which is not a scene root; " +
                "the skinning importer requires the skeleton to live under the imported scene.");
        }

        var global = Matrix4x4.Identity;
        // A skin defines its joints in the skin's own space: the inverse bind matrices already carry
        // every bind transform, so the chain is composed from its joint root. Composing the
        // non-joint ancestors above it (a COLLADA export's Z-up wrapper, an armature node) would
        // apply their transform a second time and stretch the mesh - while leaving them out is what
        // reproduces the vertices the source authored. The scene places the entity instead.
        int anchor = chain.Count - 1;
        while (anchor > 0 && !jointSlots.ContainsKey(chain[anchor]))
        {
            anchor--;
        }

        for (int i = anchor; i >= 0; i--)
        {
            global = ReadGltfNodeTransform(sourcePath, nodes[chain[i]], $"nodes[{chain[i]}]") * global;
        }

        return global;
    }

    private static int CountGltfJointAncestors(
        string sourcePath,
        int[] parents,
        Dictionary<int, int> jointSlots,
        int nodeIndex,
        int nodeCount)
    {
        int depth = 0;
        int current = parents[nodeIndex];
        int guard = 0;
        while (current >= 0)
        {
            if (jointSlots.ContainsKey(current))
            {
                depth++;
            }

            current = parents[current];
            if (++guard > nodeCount)
            {
                throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' contains a node hierarchy cycle above node '{nodeIndex}'.");
            }
        }

        return depth;
    }

    private static int FindGltfNearestJointAncestor(
        string sourcePath,
        int[] parents,
        Dictionary<int, int> jointSlots,
        int nodeIndex,
        int nodeCount)
    {
        int current = parents[nodeIndex];
        int guard = 0;
        while (current >= 0)
        {
            if (jointSlots.TryGetValue(current, out int slot))
            {
                return slot;
            }

            current = parents[current];
            if (++guard > nodeCount)
            {
                throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' contains a node hierarchy cycle above node '{nodeIndex}'.");
            }
        }

        return -1;
    }

    private static Matrix4x4 InvertGltfMatrix(string sourcePath, Matrix4x4 matrix, string context)
    {
        if (!Matrix4x4.Invert(matrix, out var inverse))
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' {context} has a non-invertible transform.");
        }

        return inverse;
    }

    private static Matrix4x4 ReadGltfMatrix4(
        string sourcePath,
        byte[][] buffers,
        GltfBufferView[] bufferViews,
        GltfAccessor accessor,
        int elementIndex,
        string context)
    {
        if (accessor.ComponentType != GltfComponentType.Float || accessor.Type != "MAT4")
        {
            throw new NotSupportedException($"[MeshAssetCooker] glTF {context} in '{sourcePath}' must be FLOAT MAT4.");
        }

        var span = GetGltfElementSpan(sourcePath, buffers, bufferViews, accessor, elementIndex, 16);
        // Read in file order: this engine composes its own transforms with System.Numerics'
        // row-vector matrices, and `MeshCooker_CookedSkeletonReproducesTheBindPose` pins that the
        // value read this way is exactly the inverse of the composed rest global for a real asset.
        return new Matrix4x4(
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(0, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(4, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(8, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(12, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(16, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(20, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(24, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(28, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(32, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(36, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(40, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(44, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(48, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(52, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(56, sizeof(float))),
            BinaryPrimitives.ReadSingleLittleEndian(span.Slice(60, sizeof(float))));
    }

    private static int ResolveGltfJointIndex(
        string sourcePath,
        GltfSkinState skinState,
        GltfAccessor accessor,
        byte[][] buffers,
        GltfBufferView[] bufferViews,
        int elementIndex,
        int componentIndex)
    {
        var span = GetGltfElementSpan(sourcePath, buffers, bufferViews, accessor, elementIndex, 4);
        int slot = accessor.ComponentType switch
        {
            GltfComponentType.UnsignedByte => span[componentIndex],
            GltfComponentType.UnsignedShort => BinaryPrimitives.ReadUInt16LittleEndian(span.Slice(componentIndex * sizeof(ushort), sizeof(ushort))),
            _ => throw new NotSupportedException($"[MeshAssetCooker] glTF JOINTS_0 in '{sourcePath}' must be UNSIGNED_BYTE or UNSIGNED_SHORT.")
        };

        if (slot >= skinState.VertexJointRemap.Length)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] glTF source '{sourcePath}' JOINTS_0 value '{slot}' is outside skin joint count '{skinState.VertexJointRemap.Length}'.");
        }

        return skinState.VertexJointRemap[slot];
    }

    private static Vector4 ReadGltfWeights(
        string sourcePath,
        byte[][] buffers,
        GltfBufferView[] bufferViews,
        GltfAccessor accessor,
        int elementIndex)
    {
        var span = GetGltfElementSpan(sourcePath, buffers, bufferViews, accessor, elementIndex, 4);
        return new Vector4(
            ReadGltfNumericComponent(span, accessor.ComponentType, accessor.Normalized, 0),
            ReadGltfNumericComponent(span, accessor.ComponentType, accessor.Normalized, 1),
            ReadGltfNumericComponent(span, accessor.ComponentType, accessor.Normalized, 2),
            ReadGltfNumericComponent(span, accessor.ComponentType, accessor.Normalized, 3));
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
            // Read in file order for now: transposing only this path was measured to flip the
            // skeleton's translation, so node matrices, the inverse bind matrices, and the palette's
            // composition have to move to one convention together. The bind-pose invariant test in
            // RenderingAssetPipelineTests is red until that lands, and it is the arbiter.
            return new Matrix4x4(
                values[0],
                values[4],
                values[8],
                values[12],
                values[1],
                values[5],
                values[9],
                values[13],
                values[2],
                values[6],
                values[10],
                values[14],
                values[3],
                values[7],
                values[11],
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
                string.Equals(name, "COLOR_0", StringComparison.Ordinal) ||
                string.Equals(name, "JOINTS_0", StringComparison.Ordinal) ||
                string.Equals(name, "WEIGHTS_0", StringComparison.Ordinal))
            {
                continue;
            }

            if (name.StartsWith("JOINTS_", StringComparison.Ordinal))
            {
                throw new NotSupportedException($"[MeshAssetCooker] glTF source '{sourcePath}' attribute '{name}' is not supported. The skinning importer accepts only JOINTS_0.");
            }

            if (name.StartsWith("WEIGHTS_", StringComparison.Ordinal))
            {
                throw new NotSupportedException($"[MeshAssetCooker] glTF source '{sourcePath}' attribute '{name}' is not supported. The skinning importer accepts only WEIGHTS_0.");
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
            "MAT4" => 16,
            _ => throw new NotSupportedException($"[MeshAssetCooker] glTF accessor type '{type}' in '{sourcePath}' is not supported by the mesh importer.")
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

    private static int ReadJointIndex(string sourcePath, string value)
    {
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index > 255)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] Invalid mesh joint index token '{value}' in '{sourcePath}'.");
        }

        return index;
    }

    private static Quaternion ReadUnitQuaternion(string sourcePath, string[] parts, int offset)
    {
        var rotation = new Quaternion(
            ReadFloat(parts[offset]),
            ReadFloat(parts[offset + 1]),
            ReadFloat(parts[offset + 2]),
            ReadFloat(parts[offset + 3]));
        var lengthSquared = rotation.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared < 1.0e-8f)
        {
            throw new InvalidOperationException($"[MeshAssetCooker] Mesh joint rotation in '{sourcePath}' must be a non-zero quaternion.");
        }

        return Quaternion.Normalize(rotation);
    }

    private static Matrix4x4 ReadMatrix4x4(string[] parts, int offset)
    {
        return new Matrix4x4(
            ReadFloat(parts[offset]),
            ReadFloat(parts[offset + 1]),
            ReadFloat(parts[offset + 2]),
            ReadFloat(parts[offset + 3]),
            ReadFloat(parts[offset + 4]),
            ReadFloat(parts[offset + 5]),
            ReadFloat(parts[offset + 6]),
            ReadFloat(parts[offset + 7]),
            ReadFloat(parts[offset + 8]),
            ReadFloat(parts[offset + 9]),
            ReadFloat(parts[offset + 10]),
            ReadFloat(parts[offset + 11]),
            ReadFloat(parts[offset + 12]),
            ReadFloat(parts[offset + 13]),
            ReadFloat(parts[offset + 14]),
            ReadFloat(parts[offset + 15]));
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
        SourceSubmesh[] Submeshes,
        MeshSkin? Skin = null,
        SourceAnimation[]? Animations = null);

    /// <summary>
    /// One baked clip: its name, its duration, and one rotation track per animated joint in the
    /// cooked skeleton's order, each optionally carrying the joint's local translation. A joint
    /// without a track keeps the cooked rest transform.
    /// </summary>
    private sealed record SourceAnimation(
        string Name,
        float Duration,
        SourceAnimationTrack[] Tracks);

    private sealed record SourceAnimationTrack(
        int JointIndex,
        float[] Times,
        Quaternion[] Rotations,
        MeshAnimationTranslationMode TranslationMode = MeshAnimationTranslationMode.Rest,
        Vector3 ConstantTranslation = default,
        Vector3[]? Translations = null);

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
        float ColorB,
        int JointX = 0,
        int JointY = 0,
        int JointZ = 0,
        int JointW = 0,
        float WeightX = 0.0f,
        float WeightY = 0.0f,
        float WeightZ = 0.0f,
        float WeightW = 0.0f)
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
                ColorB,
                JointX,
                JointY,
                JointZ,
                JointW,
                WeightX,
                WeightY,
                WeightZ,
                WeightW);
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
        uint SubmeshDataSize,
        uint JointCount,
        uint SkinDataOffset,
        uint SkinDataSize,
        uint AnimationCount,
        uint AnimationDataOffset,
        uint AnimationDataSize);
}
