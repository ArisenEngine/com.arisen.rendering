using System.Numerics;
using System.Runtime.InteropServices;

namespace ArisenEngine.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct SpotLight
{
    public Vector3 Position;
    public float Range;
    public Vector3 Color;
    public float Intensity;
    public Vector3 Direction;
    public float InnerConeCosine;
    public float OuterConeCosine;

    public bool IsValid =>
        Range > 0.0f &&
        Intensity > 0.0f &&
        Direction.LengthSquared() > 0.0001f &&
        InnerConeCosine > OuterConeCosine;

    public static SpotLight Create(
        Vector3 position,
        Vector3 direction,
        Vector3 color,
        float intensity,
        float range,
        float innerConeAngleDegrees,
        float outerConeAngleDegrees)
    {
        if (direction.LengthSquared() <= 0.0001f)
        {
            direction = Vector3.UnitZ;
        }
        else
        {
            direction = Vector3.Normalize(direction);
        }

        float outerAngle = Math.Clamp(outerConeAngleDegrees, 0.1f, 89.0f);
        float innerAngle = Math.Clamp(innerConeAngleDegrees, 0.0f, outerAngle);

        return new SpotLight
        {
            Position = position,
            Range = MathF.Max(0.0f, range),
            Color = Vector3.Max(Vector3.Zero, color),
            Intensity = MathF.Max(0.0f, intensity),
            Direction = direction,
            InnerConeCosine = MathF.Cos(DegreesToRadians(innerAngle)),
            OuterConeCosine = MathF.Cos(DegreesToRadians(outerAngle))
        };
    }

    private static float DegreesToRadians(float degrees)
    {
        return degrees * (MathF.PI / 180.0f);
    }
}
