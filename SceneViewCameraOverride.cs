using System.Numerics;
using ArisenEngine.Resources.Serialization;
using ArisenKernel.Contracts;

namespace ArisenEngine.Rendering;

public sealed record SceneViewCameraOverride(
    WorldPosition Position,
    Vector3 Rotation,
    float VerticalFieldOfView,
    float NearClip,
    float FarClip)
{
    public bool IsValid =>
        Position.IsFinite &&
        IsFinite(Rotation) &&
        float.IsFinite(VerticalFieldOfView) &&
        VerticalFieldOfView > 0.0f &&
        VerticalFieldOfView < 180.0f &&
        float.IsFinite(NearClip) &&
        NearClip > 0.0f &&
        float.IsFinite(FarClip) &&
        FarClip > NearClip;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}

public static class SurfaceCameraOverrideResolver
{
    public static bool TryResolve(
        SurfaceType surfaceType,
        SceneViewCameraOverride? sceneViewCamera,
        WorldPosition renderOrigin,
        float aspectRatio,
        out Camera camera)
    {
        camera = default;
        if (surfaceType != SurfaceType.SceneView ||
            sceneViewCamera is not { IsValid: true } ||
            !renderOrigin.IsFinite ||
            !float.IsFinite(aspectRatio) ||
            aspectRatio <= 0.0f ||
            !TryToOriginRelative(sceneViewCamera.Position, renderOrigin, out Vector3 position))
        {
            return false;
        }

        camera = new Camera
        {
            FieldOfView = sceneViewCamera.VerticalFieldOfView,
            NearClip = sceneViewCamera.NearClip,
            FarClip = sceneViewCamera.FarClip,
            AspectRatio = aspectRatio,
            ProjectionType = CameraProjectionType.Perspective,
            Position = position,
            Rotation = sceneViewCamera.Rotation
        };
        return true;
    }

    private static bool TryToOriginRelative(
        WorldPosition position,
        WorldPosition origin,
        out Vector3 result)
    {
        double x = position.X - origin.X;
        double y = position.Y - origin.Y;
        double z = position.Z - origin.Z;
        if (!double.IsFinite(x) ||
            !double.IsFinite(y) ||
            !double.IsFinite(z) ||
            Math.Abs(x) > float.MaxValue ||
            Math.Abs(y) > float.MaxValue ||
            Math.Abs(z) > float.MaxValue)
        {
            result = default;
            return false;
        }

        result = new Vector3((float)x, (float)y, (float)z);
        return float.IsFinite(result.X) &&
               float.IsFinite(result.Y) &&
               float.IsFinite(result.Z);
    }
}
