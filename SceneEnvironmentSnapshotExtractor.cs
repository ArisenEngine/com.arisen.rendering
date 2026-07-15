using ArisenEngine.Core.ECS;

namespace ArisenEngine.Rendering;

public readonly struct SceneEnvironmentExtractionStats
{
    public SceneEnvironmentExtractionStats(
        int sourceCount,
        int enabledCount,
        int acceptedCount)
    {
        SourceCount = sourceCount;
        EnabledCount = enabledCount;
        AcceptedCount = acceptedCount;
        DroppedCount = enabledCount - acceptedCount;
    }

    public int SourceCount { get; }
    public int EnabledCount { get; }
    public int AcceptedCount { get; }
    public int DroppedCount { get; }
}

public static class SceneEnvironmentSnapshotExtractor
{
    public const int MaxEnvironmentsPerFrame = 1;

    public static SceneEnvironmentExtractionStats Extract(
        ReadOnlySpan<SceneEnvironmentComponent> source,
        out SceneEnvironment environment)
    {
        environment = default;
        int enabledCount = 0;
        int acceptedCount = 0;

        for (int i = 0; i < source.Length; i++)
        {
            ref readonly var component = ref source[i];
            if (!component.IsEnabled)
            {
                continue;
            }

            enabledCount++;
            if (acceptedCount >= MaxEnvironmentsPerFrame)
            {
                continue;
            }

            environment = SceneEnvironment.Create(
                component.SkyColor,
                component.HorizonColor,
                component.GroundColor,
                component.AmbientColor,
                component.SkyIntensity,
                component.AmbientIntensity);
            acceptedCount++;
        }

        return new SceneEnvironmentExtractionStats(
            source.Length,
            enabledCount,
            acceptedCount);
    }
}
