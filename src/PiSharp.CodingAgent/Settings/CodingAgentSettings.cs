namespace PiSharp.CodingAgent;

public sealed record CodingAgentSettings
{
    public string? DefaultProvider { get; init; }
    public string? DefaultModel { get; init; }
    public string? DefaultThinkingLevel { get; init; }
    public string? SessionDir { get; init; }
    public string? ShellPath { get; init; }
    public string? SteeringMode { get; init; }
    public string? FollowUpMode { get; init; }
    public string? Theme { get; init; }
    public bool? QuietStartup { get; init; }
    public CompactionSettings? Compaction { get; init; }
    public RetrySettings? Retry { get; init; }

    public static CodingAgentSettings Default { get; } = new()
    {
        Compaction = new CompactionSettings(),
        Retry = new RetrySettings(),
    };

    public CodingAgentSettings MergeWith(CodingAgentSettings? overlay)
    {
        if (overlay is null)
        {
            return this;
        }

        return new CodingAgentSettings
        {
            DefaultProvider = overlay.DefaultProvider ?? DefaultProvider,
            DefaultModel = overlay.DefaultModel ?? DefaultModel,
            DefaultThinkingLevel = overlay.DefaultThinkingLevel ?? DefaultThinkingLevel,
            SessionDir = overlay.SessionDir ?? SessionDir,
            ShellPath = overlay.ShellPath ?? ShellPath,
            SteeringMode = overlay.SteeringMode ?? SteeringMode,
            FollowUpMode = overlay.FollowUpMode ?? FollowUpMode,
            Theme = overlay.Theme ?? Theme,
            QuietStartup = overlay.QuietStartup ?? QuietStartup,
            Compaction = overlay.Compaction ?? Compaction,
            Retry = overlay.Retry ?? Retry,
        };
    }
}

public sealed record RetrySettings(
    bool Enabled = true,
    int MaxRetries = 3,
    int BaseDelayMs = 2_000,
    int MaxDelayMs = 60_000);
