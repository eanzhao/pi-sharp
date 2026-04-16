namespace PiSharp.Mom;

public sealed record MomRuntimeOptions
{
    public required string WorkspaceDirectory { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }

    public string? ApiKey { get; init; }
}
