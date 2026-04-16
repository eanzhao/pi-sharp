namespace PiSharp.CodingAgent;

public sealed record ExtensionCommand(
    string Name,
    string Description,
    Func<CodingAgentSession, string, CancellationToken, ValueTask> Handler);
