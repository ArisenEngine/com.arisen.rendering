using System.Numerics;
using System.Runtime.InteropServices;
using ArisenEngine.Core.ECS;

namespace ArisenEngine.Rendering;

[StructLayout(LayoutKind.Sequential)]
public struct SceneEnvironment
{
    public const float DefaultExposure = SceneEnvironmentComponent.DefaultExposure;
    public const float MinimumExposure = SceneEnvironmentComponent.MinimumExposure;
    public const float MaximumExposure = SceneEnvironmentComponent.MaximumExposure;

    public Guid EnvironmentTextureGuid;
    public Vector3 SkyColor;
    public Vector3 HorizonColor;
    public Vector3 GroundColor;
    public Vector3 AmbientColor;
    public float SkyIntensity;
    public float AmbientIntensity;
    public float Exposure;

    public bool IsValid => SkyIntensity > 0.0f || AmbientIntensity > 0.0f;

    public static SceneEnvironment Default => Create(
        new Vector3(0.07f, 0.19f, 0.38f),
        new Vector3(0.62f, 0.73f, 0.82f),
        new Vector3(0.035f, 0.045f, 0.07f),
        new Vector3(0.52f, 0.62f, 0.78f),
        0.85f,
        0.32f);

    public static SceneEnvironment Create(
        Vector3 skyColor,
        Vector3 horizonColor,
        Vector3 groundColor,
        Vector3 ambientColor,
        float skyIntensity,
        float ambientIntensity,
        Guid environmentTextureGuid = default,
        float exposure = DefaultExposure)
    {
        return new SceneEnvironment
        {
            EnvironmentTextureGuid = environmentTextureGuid,
            SkyColor = Vector3.Max(Vector3.Zero, skyColor),
            HorizonColor = Vector3.Max(Vector3.Zero, horizonColor),
            GroundColor = Vector3.Max(Vector3.Zero, groundColor),
            AmbientColor = Vector3.Max(Vector3.Zero, ambientColor),
            SkyIntensity = MathF.Max(0.0f, skyIntensity),
            AmbientIntensity = MathF.Max(0.0f, ambientIntensity),
            Exposure = NormalizeExposure(exposure)
        };
    }

    public static float NormalizeExposure(float exposure)
    {
        return SceneEnvironmentComponent.NormalizeExposure(exposure);
    }

    public static SceneEnvironment CreateFlatFallback(
        Vector3 skyColor,
        float ambientIntensity)
    {
        return Create(
            skyColor,
            skyColor,
            skyColor,
            Vector3.One,
            1.0f,
            ambientIntensity);
    }
}
