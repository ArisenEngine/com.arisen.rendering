using System.Numerics;
using ArisenEngine.Core.RHI;
using ArisenEngine.Rendering.Resources;

namespace ArisenEngine.Rendering;

public readonly record struct StaticMeshCullingStats(
    int SourceItemCount,
    int VisibleItemCount,
    int CulledItemCount);

public static class StaticMeshFrustumCuller
{
    private const float BoundsEpsilon = 1.0e-6f;

    public static bool IsVisible(
        in StaticMeshRenderItem item,
        in MeshBounds meshBounds,
        in Matrix4x4 viewProjection)
    {
        if (!item.IsValid)
        {
            return false;
        }

        var bounds = ResolveLocalBounds(item, meshBounds);
        var localCenter = (bounds.Min + bounds.Max) * 0.5f;
        var localExtents = Vector3.Abs((bounds.Max - bounds.Min) * 0.5f);
        if (!HasUsableBounds(localExtents))
        {
            return true;
        }

        var worldCenter = Vector3.Transform(localCenter, item.LocalToWorld);
        var worldExtents = TransformExtents(localExtents, item.LocalToWorld);

        return !OutsidePlane(CreateLeftPlane(viewProjection), worldCenter, worldExtents) &&
               !OutsidePlane(CreateRightPlane(viewProjection), worldCenter, worldExtents) &&
               !OutsidePlane(CreateBottomPlane(viewProjection), worldCenter, worldExtents) &&
               !OutsidePlane(CreateTopPlane(viewProjection), worldCenter, worldExtents) &&
               !OutsidePlane(CreateNearPlane(viewProjection), worldCenter, worldExtents) &&
               !OutsidePlane(CreateFarPlane(viewProjection), worldCenter, worldExtents);
    }

    private static MeshBounds ResolveLocalBounds(in StaticMeshRenderItem item, in MeshBounds meshBounds)
    {
        if (HasUsableBounds(Vector3.Abs(item.BoundsExtents)))
        {
            var extents = Vector3.Abs(item.BoundsExtents);
            return new MeshBounds(item.BoundsCenter - extents, item.BoundsCenter + extents);
        }

        return meshBounds;
    }

    private static bool HasUsableBounds(Vector3 extents)
    {
        return extents.X > BoundsEpsilon ||
               extents.Y > BoundsEpsilon ||
               extents.Z > BoundsEpsilon;
    }

    private static Vector3 TransformExtents(Vector3 extents, Matrix4x4 transform)
    {
        return new Vector3(
            MathF.Abs(transform.M11) * extents.X +
            MathF.Abs(transform.M21) * extents.Y +
            MathF.Abs(transform.M31) * extents.Z,
            MathF.Abs(transform.M12) * extents.X +
            MathF.Abs(transform.M22) * extents.Y +
            MathF.Abs(transform.M32) * extents.Z,
            MathF.Abs(transform.M13) * extents.X +
            MathF.Abs(transform.M23) * extents.Y +
            MathF.Abs(transform.M33) * extents.Z);
    }

    private static bool OutsidePlane(Vector4 plane, Vector3 center, Vector3 extents)
    {
        float distance =
            plane.X * center.X +
            plane.Y * center.Y +
            plane.Z * center.Z +
            plane.W;
        float radius =
            MathF.Abs(plane.X) * extents.X +
            MathF.Abs(plane.Y) * extents.Y +
            MathF.Abs(plane.Z) * extents.Z;
        return distance + radius < 0.0f;
    }

    private static Vector4 CreateLeftPlane(Matrix4x4 matrix)
    {
        return new Vector4(
            matrix.M14 + matrix.M11,
            matrix.M24 + matrix.M21,
            matrix.M34 + matrix.M31,
            matrix.M44 + matrix.M41);
    }

    private static Vector4 CreateRightPlane(Matrix4x4 matrix)
    {
        return new Vector4(
            matrix.M14 - matrix.M11,
            matrix.M24 - matrix.M21,
            matrix.M34 - matrix.M31,
            matrix.M44 - matrix.M41);
    }

    private static Vector4 CreateBottomPlane(Matrix4x4 matrix)
    {
        return new Vector4(
            matrix.M14 + matrix.M12,
            matrix.M24 + matrix.M22,
            matrix.M34 + matrix.M32,
            matrix.M44 + matrix.M42);
    }

    private static Vector4 CreateTopPlane(Matrix4x4 matrix)
    {
        return new Vector4(
            matrix.M14 - matrix.M12,
            matrix.M24 - matrix.M22,
            matrix.M34 - matrix.M32,
            matrix.M44 - matrix.M42);
    }

    private static Vector4 CreateNearPlane(Matrix4x4 matrix)
    {
        return new Vector4(matrix.M13, matrix.M23, matrix.M33, matrix.M43);
    }

    private static Vector4 CreateFarPlane(Matrix4x4 matrix)
    {
        return new Vector4(
            matrix.M14 - matrix.M13,
            matrix.M24 - matrix.M23,
            matrix.M34 - matrix.M33,
            matrix.M44 - matrix.M43);
    }
}
