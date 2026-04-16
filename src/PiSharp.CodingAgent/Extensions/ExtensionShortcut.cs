namespace PiSharp.CodingAgent;

public sealed record ExtensionShortcut(
    string Key,
    string Description,
    Func<CodingAgentSession, CancellationToken, ValueTask> Handler);
