using ArisenEngine.Core.ECS;

namespace ArisenEngine.Rendering;

public readonly struct PointLightExtractionStats
{
    public PointLightExtractionStats(
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

public static class PointLightSnapshotExtractor
{
    public const int MaxPointLightsPerFrame = 4;

    public static PointLightExtractionStats Extract(
        ReadOnlySpan<PointLightComponent> source,
        ReadOnlySpan<Entity> entities,
        ComponentPool<TransformComponent> transformPool,
        Span<PointLight> destination)
    {
        int acceptedCapacity = Math.Min(destination.Length, MaxPointLightsPerFrame);
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
            destination[acceptedCount] = PointLight.Create(
                transform.Position,
                component.Color,
                component.Intensity,
                component.Range);
            acceptedCount++;
        }

        return new PointLightExtractionStats(
            source.Length,
            enabledCount,
            acceptedCount,
            missingTransformCount);
    }
}
