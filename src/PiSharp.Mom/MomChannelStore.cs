using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiSharp.Mom;

public sealed record MomLoggedAttachment(string Original, string? Local = null);

public sealed record MomLoggedMessage
{
    public string? Date { get; init; }

    public required string Ts { get; init; }

    public required string User { get; init; }

    public string? UserName { get; init; }

    public string? DisplayName { get; init; }

    public required string Text { get; init; }

    public IReadOnlyList<MomLoggedAttachment> Attachments { get; init; } = Array.Empty<MomLoggedAttachment>();

    public bool IsBot { get; init; }
}

public sealed class MomChannelStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public MomChannelStore(string workspaceDirectory)
    {
        WorkspaceDirectory = Path.GetFullPath(workspaceDirectory ?? throw new ArgumentNullException(nameof(workspaceDirectory)));
        Directory.CreateDirectory(WorkspaceDirectory);
    }

    public string WorkspaceDirectory { get; }

    public string GetChannelDirectory(string channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        var path = Path.Combine(WorkspaceDirectory, channelId);
        Directory.CreateDirectory(path);
        Directory.CreateDirectory(Path.Combine(path, MomDefaults.ScratchDirectoryName));
        return path;
    }

    public string GetScratchDirectory(string channelId) =>
        Path.Combine(GetChannelDirectory(channelId), MomDefaults.ScratchDirectoryName);

    public string GetSessionDirectory(string channelId)
    {
        var sessionDirectory = Path.Combine(GetChannelDirectory(channelId), ".pi-sharp", "sessions");
        Directory.CreateDirectory(sessionDirectory);
        return sessionDirectory;
    }

    public string GetLogFilePath(string channelId) =>
        Path.Combine(GetChannelDirectory(channelId), MomDefaults.LogFileName);

    public async Task LogMessageAsync(string channelId, MomLoggedMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        var normalized = message with
        {
            Date = string.IsNullOrWhiteSpace(message.Date)
                ? ParseTimestamp(message.Ts).ToString("O")
                : message.Date,
        };

        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        await File.AppendAllTextAsync(
                GetLogFilePath(channelId),
                json + Environment.NewLine,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public Task LogBotResponseAsync(
        string channelId,
        string text,
        string timestamp,
        CancellationToken cancellationToken = default) =>
        LogMessageAsync(
            channelId,
            new MomLoggedMessage
            {
                Date = DateTimeOffset.UtcNow.ToString("O"),
                Ts = timestamp,
                User = "bot",
                Text = text,
                IsBot = true,
            },
            cancellationToken);

    public string ReadMemory(string channelId)
    {
        var sections = new List<string>();

        var workspaceMemoryPath = Path.Combine(WorkspaceDirectory, MomDefaults.MemoryFileName);
        if (File.Exists(workspaceMemoryPath))
        {
            var text = File.ReadAllText(workspaceMemoryPath).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                sections.Add($"## Workspace Memory{Environment.NewLine}{text}");
            }
        }

        var channelMemoryPath = Path.Combine(GetChannelDirectory(channelId), MomDefaults.MemoryFileName);
        if (File.Exists(channelMemoryPath))
        {
            var text = File.ReadAllText(channelMemoryPath).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                sections.Add($"## Channel Memory{Environment.NewLine}{text}");
            }
        }

        return sections.Count == 0
            ? "(no memory yet)"
            : string.Join(Environment.NewLine + Environment.NewLine, sections);
    }

    private static DateTimeOffset ParseTimestamp(string timestamp)
    {
        if (double.TryParse(timestamp, out var slackTimestamp))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(slackTimestamp * 1000));
        }

        return DateTimeOffset.UtcNow;
    }
}
