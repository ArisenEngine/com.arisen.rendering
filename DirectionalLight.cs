using System.Numerics;
using System.Runtime.InteropServices;

namespace ArisenEngine.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct DirectionalLight
{
    public Vector3 Direction;
    public float Intensity;
    public Vector3 Color;
    public float AmbientIntensity;

    public static DirectionalLight Default => new()
    {
        Direction = Vector3.Normalize(new Vector3(0.35f, 0.65f, -0.68f)),
        Intensity = 1.0f,
        Color = Vector3.One,
        AmbientIntensity = 0.18f
    };

    public bool IsValid => Direction.LengthSquared() > 0.0001f && Intensity > 0.0f;

    public static DirectionalLight Create(Vector3 direction, Vector3 color, float intensity, float ambientIntensity)
    {
        if (direction.LengthSquared() <= 0.0001f)
        {
            direction = Default.Direction;
        }
        else
        {
            direction = Vector3.Normalize(direction);
        }

        return new DirectionalLight
        {
            Direction = direction,
            Intensity = MathF.Max(0.0f, intensity),
            Color = Vector3.Max(Vector3.Zero, color),
            AmbientIntensity = MathF.Max(0.0f, ambientIntensity)
        };
    }
}
