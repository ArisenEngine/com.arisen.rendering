using System.Numerics;
using System.Runtime.InteropServices;

namespace ArisenEngine.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct PointLight
{
    public Vector3 Position;
    public float Range;
    public Vector3 Color;
    public float Intensity;

    public bool IsValid => Range > 0.0f && Intensity > 0.0f;

    public static PointLight Create(Vector3 position, Vector3 color, float intensity, float range)
    {
        return new PointLight
        {
            Position = position,
            Range = MathF.Max(0.0f, range),
            Color = Vector3.Max(Vector3.Zero, color),
            Intensity = MathF.Max(0.0f, intensity)
        };
    }
}
