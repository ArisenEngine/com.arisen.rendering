using System.Runtime.CompilerServices;

namespace ArisenEngine.Rendering;

[Flags]
public enum RenderDiagnosticCategory : uint
{
    None = 0,
    Frame = 1u << 0,
    Submission = 1u << 1,
    Graph = 1u << 2,
    Passes = 1u << 3,
    All = Frame | Submission | Graph | Passes
}

/// <summary>
/// Process-start policy for verbose render diagnostics. Failure and lifecycle diagnostics do not
/// use this policy and remain enabled independently.
/// </summary>
public static class RenderDiagnostics
{
    public const string EnvironmentVariableName = "ARISEN_RENDER_DIAGNOSTICS";

    private static readonly RenderDiagnosticCategory s_EnabledCategories =
        ParseCategories(Environment.GetEnvironmentVariable(EnvironmentVariableName));

    public static RenderDiagnosticCategory EnabledCategories => s_EnabledCategories;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsEnabled(RenderDiagnosticCategory category)
    {
        return IsEnabled(s_EnabledCategories, category);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsEnabled(
        RenderDiagnosticCategory enabledCategories,
        RenderDiagnosticCategory category)
    {
        return (enabledCategories & category) != 0;
    }

    internal static RenderDiagnosticCategory ParseCategories(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return RenderDiagnosticCategory.None;
        }

        RenderDiagnosticCategory categories = RenderDiagnosticCategory.None;
        string[] tokens = value.Split(
            [',', ';', '|', ' '],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (string token in tokens)
        {
            categories |= token.ToLowerInvariant() switch
            {
                "none" => RenderDiagnosticCategory.None,
                "frame" => RenderDiagnosticCategory.Frame,
                "submission" => RenderDiagnosticCategory.Submission,
                "graph" => RenderDiagnosticCategory.Graph,
                "pass" or "passes" => RenderDiagnosticCategory.Passes,
                "all" => RenderDiagnosticCategory.All,
                _ => throw new InvalidOperationException(
                    $"Unsupported {EnvironmentVariableName} category '{token}'. " +
                    "Expected a comma-separated subset of: frame, submission, graph, passes, all.")
            };
        }

        return categories;
    }
}
