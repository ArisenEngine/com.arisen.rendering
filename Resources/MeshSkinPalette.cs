using System.Buffers.Binary;
using System.Numerics;

namespace ArisenEngine.Rendering.Resources;

/// <summary>
/// Reads cooked skinned-mesh payloads and evaluates the CPU reference skinning math that the GPU
/// skinning pass mirrors. Joints are stored parents-first, rest locals compose into joint global
/// transforms, and the matrix that deforms a vertex is <c>inverseBind * jointGlobal</c> in the
/// row-vector convention, so a bind-space point is transformed by
/// <c>position * inverseBind * jointGlobal</c>.
/// </summary>
public static class MeshSkinPalette
{
    public static Matrix4x4 LocalRestMatrix(in MeshSkinJoint joint)
    {
        return Matrix4x4.CreateFromQuaternion(joint.LocalRotation) * Matrix4x4.CreateTranslation(joint.LocalTranslation);
    }

    public static void ComputeJointGlobals(MeshSkin skin, Span<Matrix4x4> destination)
    {
        if (destination.Length < skin.Joints.Count)
        {
            throw new ArgumentException("[MeshSkinPalette] Destination span is smaller than the joint count.", nameof(destination));
        }

        for (int jointIndex = 0; jointIndex < skin.Joints.Count; jointIndex++)
        {
            MeshSkinJoint joint = skin.Joints[jointIndex];
            var local = LocalRestMatrix(joint);
            if (joint.ParentIndex < 0)
            {
                destination[jointIndex] = local;
                continue;
            }

            if (joint.ParentIndex >= jointIndex)
            {
                throw new InvalidOperationException(
                    $"[MeshSkinPalette] Joint {jointIndex} declares parent '{joint.ParentIndex}'; joints must be ordered parents-first.");
            }

            destination[jointIndex] = local * destination[joint.ParentIndex];
        }
    }

    /// <summary>
    /// Builds the palette a skinned draw uploads. Joints keep their cooked rest translation and take
    /// the pose's local rotation when one is supplied, so an empty pose reproduces the bind pose.
    /// </summary>
    public static void ComputePalette(
        MeshSkin skin,
        ReadOnlySpan<Quaternion> poseLocalRotations,
        Span<Matrix4x4> jointGlobalsScratch,
        Span<Matrix4x4> destination)
    {
        ComputePalette(skin, poseLocalRotations, ReadOnlySpan<Vector3>.Empty, jointGlobalsScratch, destination);
    }

    /// <summary>
    /// Builds the palette a skinned draw uploads. A joint takes the pose's local rotation and
    /// translation when they are supplied - an empty span keeps the cooked rest value per channel -
    /// so an empty pose reproduces the bind pose and a clip that only moves a root moves it alone.
    /// </summary>
    public static void ComputePalette(
        MeshSkin skin,
        ReadOnlySpan<Quaternion> poseLocalRotations,
        ReadOnlySpan<Vector3> poseLocalTranslations,
        Span<Matrix4x4> jointGlobalsScratch,
        Span<Matrix4x4> destination)
    {
        if (destination.Length < skin.Joints.Count || jointGlobalsScratch.Length < skin.Joints.Count)
        {
            throw new ArgumentException("[MeshSkinPalette] Buffers are smaller than the joint count.");
        }

        if (!poseLocalRotations.IsEmpty && poseLocalRotations.Length != skin.Joints.Count)
        {
            throw new ArgumentException(
                $"[MeshSkinPalette] Pose supplies {poseLocalRotations.Length} rotations for {skin.Joints.Count} joints.");
        }

        if (!poseLocalTranslations.IsEmpty && poseLocalTranslations.Length != skin.Joints.Count)
        {
            throw new ArgumentException(
                $"[MeshSkinPalette] Pose supplies {poseLocalTranslations.Length} translations for {skin.Joints.Count} joints.");
        }

        for (int jointIndex = 0; jointIndex < skin.Joints.Count; jointIndex++)
        {
            MeshSkinJoint joint = skin.Joints[jointIndex];
            var rotation = poseLocalRotations.IsEmpty ? joint.LocalRotation : poseLocalRotations[jointIndex];
            var translation = poseLocalTranslations.IsEmpty ? joint.LocalTranslation : poseLocalTranslations[jointIndex];
            var local = Matrix4x4.CreateFromQuaternion(rotation) * Matrix4x4.CreateTranslation(translation);
            if (joint.ParentIndex < 0)
            {
                jointGlobalsScratch[jointIndex] = local;
                continue;
            }

            if (joint.ParentIndex >= jointIndex)
            {
                throw new InvalidOperationException(
                    $"[MeshSkinPalette] Joint {jointIndex} declares parent '{joint.ParentIndex}'; joints must be ordered parents-first.");
            }

            jointGlobalsScratch[jointIndex] = local * jointGlobalsScratch[joint.ParentIndex];
        }

        for (int jointIndex = 0; jointIndex < skin.Joints.Count; jointIndex++)
        {
            destination[jointIndex] = skin.Joints[jointIndex].InverseBind * jointGlobalsScratch[jointIndex];
        }
    }

    public static void ComputeSkinningMatrices(MeshSkin skin, ReadOnlySpan<Matrix4x4> jointGlobals, Span<Matrix4x4> destination)
    {
        if (jointGlobals.Length < skin.Joints.Count || destination.Length < skin.Joints.Count)
        {
            throw new ArgumentException("[MeshSkinPalette] Buffers are smaller than the joint count.");
        }

        for (int jointIndex = 0; jointIndex < skin.Joints.Count; jointIndex++)
        {
            destination[jointIndex] = skin.Joints[jointIndex].InverseBind * jointGlobals[jointIndex];
        }
    }

    public static Vector3 SkinPosition(
        ReadOnlySpan<Matrix4x4> skinningMatrices,
        Vector3 position,
        ReadOnlySpan<int> joints,
        ReadOnlySpan<float> weights)
    {
        if (joints.Length != weights.Length)
        {
            throw new ArgumentException("[MeshSkinPalette] Joint and weight spans must have the same length.");
        }

        var skinned = Vector3.Zero;
        for (int i = 0; i < joints.Length; i++)
        {
            float weight = weights[i];
            if (weight == 0.0f)
            {
                continue;
            }

            skinned += Vector3.Transform(position, skinningMatrices[joints[i]]) * weight;
        }

        return skinned;
    }

    public static MeshSkin ReadCookedSkin(ReadOnlySpan<byte> cookedBytes, in CookedMesh mesh)
    {
        if (!mesh.HasSkin)
        {
            throw new InvalidOperationException("[MeshSkinPalette] Cooked mesh carries no skin payload.");
        }

        var skinData = cookedBytes.Slice(checked((int)mesh.SkinDataOffset), checked((int)mesh.SkinDataSize));
        var joints = new MeshSkinJoint[checked((int)mesh.JointCount)];
        for (int jointIndex = 0; jointIndex < joints.Length; jointIndex++)
        {
            joints[jointIndex] = ReadCookedSkinJoint(skinData, jointIndex);
        }

        return new MeshSkin(joints);
    }

    public static MeshSkinJoint ReadCookedSkinJoint(ReadOnlySpan<byte> skinData, int jointIndex)
    {
        var joint = skinData.Slice(
            checked(jointIndex * (int)MeshAssetCooker.SkinJointStride),
            checked((int)MeshAssetCooker.SkinJointStride));
        return new MeshSkinJoint(
            BinaryPrimitives.ReadInt32LittleEndian(joint.Slice(0, sizeof(int))),
            new Vector3(ReadSingle(joint, 4), ReadSingle(joint, 8), ReadSingle(joint, 12)),
            new Quaternion(ReadSingle(joint, 16), ReadSingle(joint, 20), ReadSingle(joint, 24), ReadSingle(joint, 28)),
            new Matrix4x4(
                ReadSingle(joint, 32),
                ReadSingle(joint, 36),
                ReadSingle(joint, 40),
                ReadSingle(joint, 44),
                ReadSingle(joint, 48),
                ReadSingle(joint, 52),
                ReadSingle(joint, 56),
                ReadSingle(joint, 60),
                ReadSingle(joint, 64),
                ReadSingle(joint, 68),
                ReadSingle(joint, 72),
                ReadSingle(joint, 76),
                ReadSingle(joint, 80),
                ReadSingle(joint, 84),
                ReadSingle(joint, 88),
                ReadSingle(joint, 92)));
    }

    public static Vector3 ReadCookedVertexPosition(ReadOnlySpan<byte> vertexData, uint vertexStride, int vertexIndex)
    {
        var vertex = SliceCookedVertex(vertexData, vertexStride, vertexIndex);
        return new Vector3(ReadSingle(vertex, 0), ReadSingle(vertex, 4), ReadSingle(vertex, 8));
    }

    public static Vector4 ReadCookedVertexWeights(ReadOnlySpan<byte> vertexData, uint vertexStride, int vertexIndex)
    {
        if (vertexStride < MeshAssetCooker.SkinnedMeshVertexStride)
        {
            throw new ArgumentException("[MeshSkinPalette] Cooked vertex stride does not carry skin weights.", nameof(vertexStride));
        }

        var vertex = SliceCookedVertex(vertexData, vertexStride, vertexIndex);
        return new Vector4(ReadSingle(vertex, 64), ReadSingle(vertex, 68), ReadSingle(vertex, 72), ReadSingle(vertex, 76));
    }

    public static void ReadCookedVertexJoints(
        ReadOnlySpan<byte> vertexData,
        uint vertexStride,
        int vertexIndex,
        Span<int> destination)
    {
        if (vertexStride < MeshAssetCooker.SkinnedMeshVertexStride)
        {
            throw new ArgumentException("[MeshSkinPalette] Cooked vertex stride does not carry skin weights.", nameof(vertexStride));
        }

        if (destination.Length < 4)
        {
            throw new ArgumentException("[MeshSkinPalette] Destination span must hold four joint indices.", nameof(destination));
        }

        var vertex = SliceCookedVertex(vertexData, vertexStride, vertexIndex);
        destination[0] = vertex[60];
        destination[1] = vertex[61];
        destination[2] = vertex[62];
        destination[3] = vertex[63];
    }

    private static ReadOnlySpan<byte> SliceCookedVertex(ReadOnlySpan<byte> vertexData, uint vertexStride, int vertexIndex)
    {
        int stride = checked((int)vertexStride);
        return vertexData.Slice(checked(vertexIndex * stride), stride);
    }

    private static float ReadSingle(ReadOnlySpan<byte> bytes, int offset)
    {
        return BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(offset, sizeof(float)));
    }
}
