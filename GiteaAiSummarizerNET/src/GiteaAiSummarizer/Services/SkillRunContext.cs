namespace GiteaAiSummarizer.Services;

/// <summary>
/// Carries the diff under review from <see cref="SummaryService"/> to <see cref="SkillScriptRunner"/>.
/// </summary>
/// <remarks>
/// Without this the model has to echo the whole diff back as a tool argument — tens of thousands of
/// output tokens per call, and truncated JSON once a provider's output cap is hit. Function invocation
/// runs in the same async flow as <c>agent.RunAsync</c>, so an <see cref="AsyncLocal{T}"/> set before
/// the run is visible to the script runner.
/// </remarks>
internal static class SkillRunContext
{
    private static readonly AsyncLocal<string?> _currentDiff = new();

    public static string? CurrentDiff
    {
        get => _currentDiff.Value;
        set => _currentDiff.Value = value;
    }
}
