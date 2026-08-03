namespace ArisenEngine.Rendering;

internal static class RenderFrameFailureAggregator
{
    public static Exception Append(
        Exception? existing,
        string cleanupStage,
        Exception cleanupFailure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(cleanupStage);
        ArgumentNullException.ThrowIfNull(cleanupFailure);

        var attributedFailure = new InvalidOperationException(
            $"Render frame {cleanupStage} failed.",
            cleanupFailure);
        return existing == null
            ? attributedFailure
            : new AggregateException(
                "Render frame execution and cleanup both failed.",
                existing,
                attributedFailure);
    }
}
