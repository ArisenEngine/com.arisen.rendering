using ArisenEngine.Core.ECS;
using ArisenEngine.Core.Math;

namespace ArisenEngine.Rendering;

public readonly struct SpotLightExtractionStats
{
    public SpotLightExtractionStats(
        int sourceCount,
        int enabledCount,
        int acceptedCount,
        int missingTransformCount)
    {
        SourceCount = sourceCount;
        EnabledCount = enabledCount;
        AcceptedCount = acceptedCount;
        MissingTransformCount = missingTransformCount;
        DroppedCount = enabledCount - acceptedCount;
    }

    public int SourceCount { get; }
    public int EnabledCount { get; }
    public int AcceptedCount { get; }
    public int MissingTransformCount { get; }
    public int DroppedCount { get; }
}

public static class SpotLightSnapshotExtractor
{
    public const int MaxSpotLightsPerFrame = 4;

    public static SpotLightExtractionStats Extract(
        ReadOnlySpan<SpotLightComponent> source,
        ReadOnlySpan<Entity> entities,
        ComponentPool<TransformComponent> transformPool,
        Span<SpotLight> destination)
    {
        int acceptedCapacity = Math.Min(destination.Length, MaxSpotLightsPerFrame);
        int sourceCount = Math.Min(source.Length, entities.Length);
        int enabledCount = 0;
        int acceptedCount = 0;
        int missingTransformCount = 0;

        for (int i = 0; i < sourceCount; i++)
        {
            ref readonly var component = ref source[i];
            if (!component.IsEnabled)
            {
                continue;
            }

            enabledCount++;
            if (!transformPool.Has(entities[i]))
            {
                missingTransformCount++;
                continue;
            }

            if (acceptedCount >= acceptedCapacity)
            {
                continue;
            }

            ref var transform = ref transformPool.GetRef(entities[i]);
            destination[acceptedCount] = SpotLight.Create(
                transform.Position,
                transform.Rotation.ForwardVector(),
                component.Color,
                component.Intensity,
                component.Range,
                component.InnerConeAngleDegrees,
                component.OuterConeAngleDegrees);
            acceptedCount++;
        }

        return new SpotLightExtractionStats(
            source.Length,
            enabledCount,
            acceptedCount,
            missingTransformCount);
    }
}
