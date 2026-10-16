using System.Numerics;

namespace ArisenEngine.Rendering.Resources;

/// <summary>
/// How a joint's baked track carries its local translation beside its rotation keys. A joint whose
/// clip never moves it keeps the cooked rest translation and stores nothing.
/// </summary>
public enum MeshAnimationTranslationMode
{
    /// <summary>The joint keeps the cooked rest local translation.</summary>
    Rest = 0,

    /// <summary>Every key resolves to the same local translation, stored once on the track.</summary>
    Constant = 1,

    /// <summary>Each key carries its own local translation beside its rotation.</summary>
    PerKey = 2,
}

/// <summary>One joint's baked rotation keys, in the cooked skeleton's joint order.</summary>
public readonly record struct MeshAnimationJointTrack(
    int JointIndex,
    float[] Times,
    Quaternion[] Rotations,
    MeshAnimationTranslationMode TranslationMode = MeshAnimationTranslationMode.Rest,
    Vector3 ConstantTranslation = default,
    Vector3[]? KeyTranslations = null)
{
    public bool IsValid =>
        JointIndex >= 0 &&
        Times is { Length: > 0 } &&
        Rotations is { Length: > 0 } &&
        Times.Length == Rotations.Length &&
        (TranslationMode != MeshAnimationTranslationMode.PerKey ||
         KeyTranslations is { Length: > 0 } &&
         KeyTranslations.Length == Times.Length);
}

/// <summary>
/// One clip of a cooked mesh's animation table: its name, its duration, and a rotation track per
/// animated joint. A joint without a track keeps the cooked rest rotation, so sampling a clip
/// produces a complete pose for the whole skeleton.
/// </summary>
public sealed class MeshAnimationClip
{
    private readonly MeshAnimationJointTrack[] m_Tracks;
    private readonly int[] m_TrackIndexOfJoint;

    internal MeshAnimationClip(
        string name,
        float duration,
        MeshAnimationJointTrack[] tracks,
        int jointCount)
    {
        Name = name;
        Duration = duration;
        m_Tracks = tracks;
        m_TrackIndexOfJoint = new int[jointCount];
        Array.Fill(m_TrackIndexOfJoint, -1);
        for (int trackIndex = 0; trackIndex < tracks.Length; trackIndex++)
        {
            m_TrackIndexOfJoint[tracks[trackIndex].JointIndex] = trackIndex;
        }
    }

    public string Name { get; }

    /// <summary>Seconds; a clip that holds a single key reports zero and its pose is that key.</summary>
    public float Duration { get; }

    public int TrackCount => m_Tracks.Length;

    public int TrackIndexOfJoint(int jointIndex) =>
        (uint)jointIndex < (uint)m_TrackIndexOfJoint.Length ? m_TrackIndexOfJoint[jointIndex] : -1;

    public MeshAnimationJointTrack GetTrack(int trackIndex) => m_Tracks[trackIndex];

    public override string ToString() =>
        $"{Name} ({Duration:0.###}s, {m_Tracks.Length} track(s))";
}

/// <summary>
/// The animation table a cooked mesh carries beside its skeleton. The cook bakes it from the source's
/// clips, so a runtime consumer addresses a clip by name and samples it directly, exactly as
/// <see cref="MeshSkinPalette"/> reads the cooked skeleton.
/// </summary>
public sealed class MeshAnimationTable
{
    private MeshAnimationTable(IReadOnlyList<MeshAnimationClip> clips)
    {
        Clips = clips;
    }

    public static MeshAnimationTable Empty { get; } = new(Array.Empty<MeshAnimationClip>());

    public IReadOnlyList<MeshAnimationClip> Clips { get; }

    public int ClipCount => Clips.Count;

    public bool TryFindClip(string name, out int clipIndex)
    {
        for (int index = 0; index < Clips.Count; index++)
        {
            if (string.Equals(Clips[index].Name, name, StringComparison.Ordinal))
            {
                clipIndex = index;
                return true;
            }
        }

        clipIndex = -1;
        return false;
    }

    public MeshAnimationClip GetClip(int clipIndex)
    {
        if ((uint)clipIndex >= (uint)Clips.Count)
        {
            throw new ArgumentOutOfRangeException(nameof(clipIndex));
        }

        return Clips[clipIndex];
    }

    /// <summary>
    /// Reads the table a cooked mesh payload carries. A mesh that cooked without clips yields an
    /// empty table rather than a failure: a scene may carry skinned meshes that nothing animates.
    /// </summary>
    public static MeshAnimationTable Read(ReadOnlySpan<byte> payload, in CookedMesh mesh)
    {
        if (!mesh.HasAnimations)
        {
            return Empty;
        }

        if (payload.Length < mesh.AnimationDataOffset + mesh.AnimationDataSize)
        {
            throw new InvalidOperationException(
                "[MeshAnimationTable] Cooked mesh animation payload is truncated.");
        }

        ReadOnlySpan<byte> block = payload.Slice(
            checked((int)mesh.AnimationDataOffset),
            checked((int)mesh.AnimationDataSize));
        int jointCount = checked((int)mesh.JointCount);
        var clips = new List<MeshAnimationClip>(checked((int)mesh.AnimationCount));
        int clipOffset = 0;
        for (int clipIndex = 0; clipIndex < mesh.AnimationCount; clipIndex++)
        {
            if (clipOffset + MeshAssetCooker.AnimationClipHeaderStride > block.Length)
            {
                throw new InvalidOperationException("[MeshAnimationTable] Clip header is outside the payload.");
            }

            string name = ReadName(block.Slice(clipOffset, MeshAssetCooker.AnimationNameBytes));
            float duration = BitConverter.Int32BitsToSingle(
                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                    block.Slice(clipOffset + MeshAssetCooker.AnimationNameBytes, sizeof(int))));
            int clipJointCount = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                block.Slice(clipOffset + MeshAssetCooker.AnimationNameBytes + 4, sizeof(int)));
            int trackCount = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                block.Slice(clipOffset + MeshAssetCooker.AnimationNameBytes + 8, sizeof(int)));
            if (clipJointCount != jointCount || trackCount <= 0 || !float.IsFinite(duration) || duration < 0.0f)
            {
                throw new InvalidOperationException(
                    $"[MeshAnimationTable] Clip '{name}' does not match the mesh's {jointCount}-joint " +
                    $"skeleton or carries an invalid duration.");
            }

            int trackTableOffset = clipOffset + checked((int)MeshAssetCooker.AnimationClipHeaderStride);
            var tracks = new MeshAnimationJointTrack[trackCount];
            for (int trackIndex = 0; trackIndex < trackCount; trackIndex++)
            {
                int entry = trackTableOffset + trackIndex * checked((int)MeshAssetCooker.AnimationTrackStride);
                int jointIndex = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                    block.Slice(entry, sizeof(int)));
                int keyCount = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                    block.Slice(entry + 4, sizeof(int)));
                uint keyOffset = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                    block.Slice(entry + 8, sizeof(uint)));
                int translationModeValue = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                    block.Slice(entry + 12, sizeof(int)));
                if (jointIndex < 0 || jointIndex >= jointCount || keyCount <= 0)
                {
                    throw new InvalidOperationException(
                        $"[MeshAnimationTable] Clip '{name}' track {trackIndex} is outside its skeleton.");
                }

                if (translationModeValue is < (int)MeshAnimationTranslationMode.Rest or > (int)MeshAnimationTranslationMode.PerKey)
                {
                    throw new InvalidOperationException(
                        $"[MeshAnimationTable] Clip '{name}' track {trackIndex} carries an unknown " +
                        $"translation mode '{translationModeValue}'.");
                }

                var translationMode = (MeshAnimationTranslationMode)translationModeValue;
                var constantTranslation = new Vector3(
                    BitConverter.Int32BitsToSingle(
                        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(block.Slice(entry + 16, 4))),
                    BitConverter.Int32BitsToSingle(
                        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(block.Slice(entry + 20, 4))),
                    BitConverter.Int32BitsToSingle(
                        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(block.Slice(entry + 24, 4))));
                uint keyStride = translationMode == MeshAnimationTranslationMode.PerKey
                    ? MeshAssetCooker.AnimationTranslatedKeyStride
                    : MeshAssetCooker.AnimationKeyStride;
                var times = new float[keyCount];
                var rotations = new Quaternion[keyCount];
                Vector3[]? keyTranslations = translationMode == MeshAnimationTranslationMode.PerKey
                    ? new Vector3[keyCount]
                    : null;
                for (int key = 0; key < keyCount; key++)
                {
                    int keyPosition = clipOffset + checked((int)keyOffset) +
                        key * checked((int)keyStride);
                    if (keyPosition + keyStride > block.Length)
                    {
                        throw new InvalidOperationException(
                            $"[MeshAnimationTable] Clip '{name}' keys are outside the payload.");
                    }

                    times[key] = BitConverter.Int32BitsToSingle(
                        System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(block.Slice(keyPosition, 4)));
                    rotations[key] = new Quaternion(
                        BitConverter.Int32BitsToSingle(
                            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(block.Slice(keyPosition + 4, 4))),
                        BitConverter.Int32BitsToSingle(
                            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(block.Slice(keyPosition + 8, 4))),
                        BitConverter.Int32BitsToSingle(
                            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(block.Slice(keyPosition + 12, 4))),
                        BitConverter.Int32BitsToSingle(
                            System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(block.Slice(keyPosition + 16, 4))));
                    if (keyTranslations != null)
                    {
                        keyTranslations[key] = new Vector3(
                            BitConverter.Int32BitsToSingle(
                                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(block.Slice(keyPosition + 20, 4))),
                            BitConverter.Int32BitsToSingle(
                                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(block.Slice(keyPosition + 24, 4))),
                            BitConverter.Int32BitsToSingle(
                                System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(block.Slice(keyPosition + 28, 4))));
                    }
                }

                tracks[trackIndex] = new MeshAnimationJointTrack(
                    jointIndex,
                    times,
                    rotations,
                    translationMode,
                    constantTranslation,
                    keyTranslations);
            }

            clips.Add(new MeshAnimationClip(name, duration, tracks, jointCount));
            uint clipSize = MeshAssetCooker.AnimationClipHeaderStride +
                checked((uint)trackCount * MeshAssetCooker.AnimationTrackStride);
            foreach (MeshAnimationJointTrack track in tracks)
            {
                uint keyStride = track.TranslationMode == MeshAnimationTranslationMode.PerKey
                    ? MeshAssetCooker.AnimationTranslatedKeyStride
                    : MeshAssetCooker.AnimationKeyStride;
                clipSize += checked((uint)track.Times.Length * keyStride);
            }

            clipOffset += checked((int)clipSize);
        }

        return new MeshAnimationTable(clips);
    }

    private static string ReadName(ReadOnlySpan<byte> bytes)
    {
        int length = bytes.IndexOf((byte)0);
        if (length < 0)
        {
            length = bytes.Length;
        }

        return System.Text.Encoding.UTF8.GetString(bytes.Slice(0, length));
    }
}

/// <summary>
/// Samples a cooked clip onto a skeleton's joints: a joint with a track takes the track's rotation
/// (and translation) at the sampled time - LINEAR keys blend with a spherical interpolation, and
/// translations with a linear one - while every other joint keeps its cooked rest transform, so one
/// call fills a complete pose for the pose path that consumes it.
/// </summary>
public static class MeshAnimationSampler
{
    public static void SamplePose(
        MeshSkin skin,
        MeshAnimationTable table,
        int clipIndex,
        float time,
        Span<Quaternion> destination)
    {
        SamplePose(skin, table, clipIndex, time, destination, Span<Vector3>.Empty);
    }

    public static void SamplePose(
        MeshSkin skin,
        MeshAnimationTable table,
        int clipIndex,
        float time,
        Span<Quaternion> rotations,
        Span<Vector3> translations)
    {
        ArgumentNullException.ThrowIfNull(skin);
        ArgumentNullException.ThrowIfNull(table);
        if (rotations.Length < skin.Joints.Count)
        {
            throw new ArgumentException(
                $"[MeshAnimationSampler] Destination holds {rotations.Length} rotations for " +
                $"{skin.Joints.Count} joints.",
                nameof(rotations));
        }

        if (!translations.IsEmpty && translations.Length < skin.Joints.Count)
        {
            throw new ArgumentException(
                $"[MeshAnimationSampler] Destination holds {translations.Length} translations for " +
                $"{skin.Joints.Count} joints.",
                nameof(translations));
        }

        MeshAnimationClip clip = table.GetClip(clipIndex);
        float localTime = clip.Duration > 0.0f ? time % clip.Duration : 0.0f;
        if (localTime < 0.0f)
        {
            localTime += clip.Duration;
        }

        for (int jointIndex = 0; jointIndex < skin.Joints.Count; jointIndex++)
        {
            int trackIndex = clip.TrackIndexOfJoint(jointIndex);
            if (trackIndex < 0)
            {
                rotations[jointIndex] = skin.Joints[jointIndex].LocalRotation;
                if (!translations.IsEmpty)
                {
                    translations[jointIndex] = skin.Joints[jointIndex].LocalTranslation;
                }

                continue;
            }

            MeshAnimationJointTrack track = clip.GetTrack(trackIndex);
            rotations[jointIndex] = SampleTrack(track, localTime);
            if (!translations.IsEmpty)
            {
                translations[jointIndex] = SampleTrackTranslation(
                    track,
                    localTime,
                    skin.Joints[jointIndex].LocalTranslation);
            }
        }
    }

    private static Quaternion SampleTrack(in MeshAnimationJointTrack track, float time)
    {
        ReadOnlySpan<float> times = track.Times;
        if (time <= times[0])
        {
            return track.Rotations[0];
        }

        if (time >= times[^1])
        {
            return track.Rotations[^1];
        }

        FindKeyWindow(times, time, out int low, out int high, out float blend);
        return Quaternion.Slerp(track.Rotations[low], track.Rotations[high], blend);
    }

    private static Vector3 SampleTrackTranslation(in MeshAnimationJointTrack track, float time, Vector3 rest)
    {
        switch (track.TranslationMode)
        {
            case MeshAnimationTranslationMode.Constant:
                return track.ConstantTranslation;
            case MeshAnimationTranslationMode.PerKey:
                if (track.KeyTranslations is not { } translations ||
                    translations.Length != track.Times.Length)
                {
                    throw new InvalidOperationException(
                        $"[MeshAnimationSampler] Joint {track.JointIndex} declares per-key translations " +
                        "without keys.");
                }

                ReadOnlySpan<float> times = track.Times;
                if (time <= times[0])
                {
                    return translations[0];
                }

                if (time >= times[^1])
                {
                    return translations[^1];
                }

                FindKeyWindow(times, time, out int low, out int high, out float blend);
                return Vector3.Lerp(translations[low], translations[high], blend);
            default:
                return rest;
        }
    }

    private static void FindKeyWindow(
        ReadOnlySpan<float> times,
        float time,
        out int low,
        out int high,
        out float blend)
    {
        low = 0;
        high = times.Length - 1;
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
        blend = span > 0.0f ? (time - times[low]) / span : 0.0f;
    }
}
