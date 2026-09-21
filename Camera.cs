using ArisenEngine.Core.Math;
using System.Runtime.InteropServices;

namespace ArisenEngine.Rendering;

public enum CameraProjectionType
{
    Perspective,
    Orthographic
}

[StructLayout(LayoutKind.Sequential)]
public struct Camera
{
    public float FieldOfView;
    public float NearClip;
    public float FarClip;
    public float AspectRatio;
    public float OrthographicSize;
    public CameraProjectionType ProjectionType;

    public Vector3 Position;
    public Vector3 Rotation; // Eulers in degrees

    public Matrix4x4 ProjectionMatrix
    {
        get
        {
            if (ProjectionType == CameraProjectionType.Perspective)
            {
                return Matrix4x4.CreatePerspectiveFieldOfView(Mathf.Deg2Rad * FieldOfView, AspectRatio, NearClip,
                    FarClip);
            }
            else
            {
                float h = OrthographicSize * 2.0f;
                float w = h * AspectRatio;
                return Matrix4x4.CreateOrthographic(w, h, NearClip, FarClip);
            }
        }
    }

    public Matrix4x4 ViewMatrix
    {
        get
        {
            ResolveViewBasis(out Vector3 forward, out Vector3 up);
            Vector3 target = Position + forward;
            return Matrix4x4.CreateLookAt(Position, target, up);
        }
    }

    /// <summary>
    /// View matrix of this camera placed at the origin of its own frame. A pass whose vertex
    /// positions are already relative to the render camera (the view frame) must use this matrix
    /// instead of <see cref="ViewMatrix"/> together with view-frame positions. Unlike the
    /// world-frame view matrix it is pure rotation derived only from <see cref="Rotation"/>, so it
    /// cannot be perturbed by moving the rebaseable world origin. This is the same basis the
    /// directional-shadow fitter already uses to fit cascades around a camera at the origin.
    /// </summary>
    public Matrix4x4 ViewRelativeViewMatrix
    {
        get
        {
            ResolveViewBasis(out Vector3 forward, out Vector3 up);
            return Matrix4x4.CreateLookAt(Vector3.Zero, forward, up);
        }
    }

    private readonly void ResolveViewBasis(out Vector3 forward, out Vector3 up)
    {
        // Simple FPS-style camera for now
        Matrix4x4 rotation = Matrix4x4.CreateFromYawPitchRoll(
            Mathf.Deg2Rad * Rotation.Y,
            Mathf.Deg2Rad * Rotation.X,
            Mathf.Deg2Rad * Rotation.Z
        );

        forward = Vector3.Transform(MathExtensions.Forward, rotation);
        up = Vector3.Transform(MathExtensions.Up, rotation);
    }
}
