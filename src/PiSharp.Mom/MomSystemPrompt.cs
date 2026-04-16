namespace PiSharp.Mom;

public sealed record MomSystemPromptOptions
{
    public required string WorkspaceDirectory { get; init; }

    public required string ChannelId { get; init; }

    public required string ChannelDirectory { get; init; }

    public required string Memory { get; init; }

    public DateTimeOffset? CurrentTime { get; init; }
}

public static class MomSystemPrompt
{
    public static string Build(MomSystemPromptOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var currentTime = options.CurrentTime ?? DateTimeOffset.UtcNow;
        var workspaceDirectory = Normalize(options.WorkspaceDirectory);
        var channelDirectory = Normalize(options.ChannelDirectory);
        var scratchDirectory = Normalize(Path.Combine(options.ChannelDirectory, MomDefaults.ScratchDirectoryName));

        return
        $"""
You are mom, a Slack bot powered by PiSharp. Be concise. No emojis.

Use Slack mrkdwn instead of Markdown:
- Bold: *text*
- Italic: _text_
- Code: `code`
- Code block: ```code```
- Links: <https://example.com|label>

Workspace layout:
- Workspace root: {workspaceDirectory}
- Current channel: {options.ChannelId}
- Channel directory: {channelDirectory}
- Scratch directory for temporary work: {scratchDirectory}
- Full conversation log: {Normalize(Path.Combine(options.ChannelDirectory, MomDefaults.LogFileName))}
- Shared memory: {Normalize(Path.Combine(options.WorkspaceDirectory, MomDefaults.MemoryFileName))}
- Channel memory: {Normalize(Path.Combine(options.ChannelDirectory, MomDefaults.MemoryFileName))}

Operational rules:
- Store durable files inside the channel directory.
- Prefer using scratch/ for temporary clones and throwaway outputs.
- If a user only says "stop", treat that as an external stop request, not a task to execute.
- Keep Slack replies short unless the task requires detail.
- If you refer to paths, use paths relative to the channel directory when possible.

Current memory:
{options.Memory}

Current date: {currentTime:yyyy-MM-dd}
Current time: {currentTime:O}
Current working directory: {channelDirectory}
""";
    }

    private static string Normalize(string path) => Path.GetFullPath(path).Replace('\\', '/');
}
