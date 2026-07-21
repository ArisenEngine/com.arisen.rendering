using ArisenEngine.Core.ECS;

namespace ArisenEngine.Rendering;

public readonly struct DirectionalLightExtractionStats
{
    public DirectionalLightExtractionStats(
        int sourceCount,
        int enabledCount,
        int acceptedCount,
        int invalidInputCount)
    {
        SourceCount = sourceCount;
        EnabledCount = enabledCount;
        AcceptedCount = acceptedCount;
        InvalidInputCount = invalidInputCount;
        DroppedCount = enabledCount - acceptedCount;
    }

    public int SourceCount { get; }
    public int EnabledCount { get; }
    public int AcceptedCount { get; }
    public int InvalidInputCount { get; }
    public int DroppedCount { get; }
}

public static class DirectionalLightSnapshotExtractor
{
    // The current StandardLit path consumes one primary directional light.
    public const int MaxDirectionalLightsPerFrame = 1;

    public static DirectionalLightExtractionStats Extract(
        ReadOnlySpan<DirectionalLightComponent> source,
        Span<DirectionalLight> destination)
    {
        int acceptedCapacity = Math.Min(destination.Length, MaxDirectionalLightsPerFrame);
        int enabledCount = 0;
        int acceptedCount = 0;
        int invalidInputCount = 0;

        for (int i = 0; i < source.Length; i++)
        {
            ref readonly var component = ref source[i];
            if (!component.IsEnabled)
            {
                continue;
            }

            enabledCount++;
            if (!IsFinite(component.Direction) ||
                !IsFinite(component.Color) ||
                !float.IsFinite(component.Intensity) ||
                !float.IsFinite(component.AmbientIntensity))
            {
                invalidInputCount++;
                continue;
            }
            if (acceptedCount >= acceptedCapacity)
            {
                continue;
            }

            destination[acceptedCount] = DirectionalLight.Create(
                component.Direction,
                component.Color,
                component.Intensity,
                component.AmbientIntensity);
            acceptedCount++;
        }

        return new DirectionalLightExtractionStats(
            source.Length,
            enabledCount,
            acceptedCount,
            invalidInputCount);
    }

    private static bool IsFinite(System.Numerics.Vector3 value) =>
        float.IsFinite(value.X) &&
        float.IsFinite(value.Y) &&
        float.IsFinite(value.Z);
}
