namespace PiSharp.Mom;

public sealed record MomSystemPromptOptions
{
    public required string WorkspaceDirectory { get; init; }

    public required string ChannelId { get; init; }

    public string? ChannelName { get; init; }

    public required string ChannelDirectory { get; init; }

    public required string Memory { get; init; }

    public IReadOnlyList<SlackUserInfo> Users { get; init; } = Array.Empty<SlackUserInfo>();

    public IReadOnlyList<SlackChannelInfo> Channels { get; init; } = Array.Empty<SlackChannelInfo>();

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
        var attachmentsDirectory = Normalize(Path.Combine(options.ChannelDirectory, "attachments"));
        var scratchDirectory = Normalize(Path.Combine(options.ChannelDirectory, MomDefaults.ScratchDirectoryName));
        var eventsDirectory = Normalize(Path.Combine(options.WorkspaceDirectory, MomDefaults.EventsDirectoryName));
        var immediateExample = $"{{\"type\":\"immediate\",\"channelId\":\"{options.ChannelId}\",\"text\":\"New activity detected\"}}";
        var oneShotExample = $"{{\"type\":\"one-shot\",\"channelId\":\"{options.ChannelId}\",\"text\":\"Remind me later\",\"at\":\"2026-04-16T18:00:00+08:00\"}}";
        var periodicExample = $"{{\"type\":\"periodic\",\"channelId\":\"{options.ChannelId}\",\"text\":\"Check inbox\",\"schedule\":\"0 9 * * 1-5\",\"timezone\":\"Asia/Singapore\"}}";
        var currentChannelLabel = string.IsNullOrWhiteSpace(options.ChannelName)
            ? options.ChannelId
            : $"{options.ChannelId} ({options.ChannelName})";
        var channelMappings = options.Channels.Count == 0
            ? "(no channel metadata loaded)"
            : string.Join(
                Environment.NewLine,
                options.Channels.Select(static channel =>
                    $"{channel.Id}\t{(channel.Name.StartsWith("DM:", StringComparison.Ordinal) ? channel.Name : $"#{channel.Name}")}"));
        var userMappings = options.Users.Count == 0
            ? "(no user metadata loaded)"
            : string.Join(
                Environment.NewLine,
                options.Users.Select(static user => $"{user.Id}\t@{user.UserName}\t{user.DisplayName}"));

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
- Current channel: {currentChannelLabel}
- Channel directory: {channelDirectory}
- Attachments directory for user-shared files: {attachmentsDirectory}
- Scratch directory for temporary work: {scratchDirectory}
- Full conversation log: {Normalize(Path.Combine(options.ChannelDirectory, MomDefaults.LogFileName))}
- Shared memory: {Normalize(Path.Combine(options.WorkspaceDirectory, MomDefaults.MemoryFileName))}
- Channel memory: {Normalize(Path.Combine(options.ChannelDirectory, MomDefaults.MemoryFileName))}

Slack IDs:
Channels:
{channelMappings}

Users:
{userMappings}

Operational rules:
- Store durable files inside the channel directory.
- Prefer using scratch/ for temporary clones and throwaway outputs.
- If a user only says "stop", treat that as an external stop request, not a task to execute.
- Keep Slack replies short unless the task requires detail.
- If you refer to paths, use paths relative to the channel directory when possible.

Events:
- Events live in {eventsDirectory}.
- Immediate event example: {immediateExample}
- One-shot event example: {oneShotExample}
- Periodic event example: {periodicExample}
- Immediate and one-shot events auto-delete after they trigger. Periodic events stay until deleted.
- When a periodic event has nothing useful to report, respond with exactly [SILENT].
- Manage events with ls/cat/rm inside the events directory.

Current memory:
{options.Memory}

Current date: {currentTime:yyyy-MM-dd}
Current time: {currentTime:O}
Current working directory: {channelDirectory}
""";
    }

    private static string Normalize(string path) => Path.GetFullPath(path).Replace('\\', '/');
}
