using ArisenEngine.Core.RHI;
using System.Numerics;

namespace ArisenEngine.Rendering;

public readonly struct TransparentDrawSortKey : IComparable<TransparentDrawSortKey>
{
    public readonly float CameraDepth;
    public readonly uint SourceDrawIndex;

    private TransparentDrawSortKey(float cameraDepth, uint sourceDrawIndex)
    {
        CameraDepth = cameraDepth;
        SourceDrawIndex = sourceDrawIndex;
    }

    public static TransparentDrawSortKey From(
        in MeshDrawCommand draw,
        Matrix4x4 viewMatrix,
        uint sourceDrawIndex)
    {
        var viewPosition = Vector3.Transform(draw.LocalToWorld.Translation, viewMatrix);
        float cameraDepth = -viewPosition.Z;
        if (!float.IsFinite(cameraDepth))
        {
            cameraDepth = 0.0f;
        }

        return new TransparentDrawSortKey(cameraDepth, sourceDrawIndex);
    }

    public int CompareTo(TransparentDrawSortKey other)
    {
        int result = other.CameraDepth.CompareTo(CameraDepth);
        return result != 0
            ? result
            : SourceDrawIndex.CompareTo(other.SourceDrawIndex);
    }
}

public static class TransparentDrawOrdering
{
    public static void SortBackToFront(
        MeshDrawCommand[] draws,
        TransparentDrawSortKey[] sortKeys,
        int count,
        Matrix4x4 viewMatrix)
    {
        ArgumentNullException.ThrowIfNull(draws);
        ArgumentNullException.ThrowIfNull(sortKeys);

        if (count < 0 || count > draws.Length || count > sortKeys.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        for (int i = 0; i < count; i++)
        {
            sortKeys[i] = TransparentDrawSortKey.From(
                draws[i],
                viewMatrix,
                checked((uint)i));
        }

        Array.Sort(sortKeys, draws, 0, count);
    }
}
