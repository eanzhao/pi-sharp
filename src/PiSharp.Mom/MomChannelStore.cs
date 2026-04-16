using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiSharp.Mom;

public sealed record MomLoggedAttachment(string Original, string? Local = null);
public sealed record MomLogWriteResult(MomLoggedMessage Message, bool IsDuplicate = false);

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

public sealed class MomChannelStore : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };
    private readonly HttpClient? _httpClient;
    private readonly bool _ownsHttpClient;
    private readonly string? _slackBotToken;
    private readonly MomSlackWorkspaceIndex? _workspaceIndex;

    public MomChannelStore(
        string workspaceDirectory,
        string? slackBotToken = null,
        HttpClient? httpClient = null,
        MomSlackWorkspaceIndex? workspaceIndex = null)
    {
        WorkspaceDirectory = Path.GetFullPath(workspaceDirectory ?? throw new ArgumentNullException(nameof(workspaceDirectory)));
        _slackBotToken = string.IsNullOrWhiteSpace(slackBotToken) ? null : slackBotToken.Trim();
        _httpClient = httpClient ?? (_slackBotToken is null ? null : new HttpClient());
        _ownsHttpClient = httpClient is null && _httpClient is not null;
        _workspaceIndex = workspaceIndex;
        Directory.CreateDirectory(WorkspaceDirectory);
    }

    public string WorkspaceDirectory { get; }

    public MomSlackWorkspaceIndex? WorkspaceIndex => _workspaceIndex;

    public string NormalizeMessageText(SlackIncomingEvent incomingEvent) =>
        MomTurnProcessor.NormalizePrompt(incomingEvent);

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

    public string GetEventsDirectory()
    {
        var path = Path.Combine(WorkspaceDirectory, MomDefaults.EventsDirectoryName);
        Directory.CreateDirectory(path);
        return path;
    }

    public string GetLogFilePath(string channelId) =>
        Path.Combine(GetChannelDirectory(channelId), MomDefaults.LogFileName);

    public string GetAttachmentsDirectory(string channelId)
    {
        var path = Path.Combine(GetChannelDirectory(channelId), "attachments");
        Directory.CreateDirectory(path);
        return path;
    }

    public async Task<bool> LogMessageAsync(string channelId, MomLoggedMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (ReadLoggedMessage(channelId, message.Ts) is not null)
        {
            return false;
        }

        var normalized = message with
        {
            Date = string.IsNullOrWhiteSpace(message.Date)
                ? ParseTimestamp(message.Ts).ToString("O")
                : message.Date,
        };

        var logPath = GetLogFilePath(channelId);
        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        var prefix = NeedsLeadingNewLine(logPath) ? Environment.NewLine : string.Empty;
        await File.AppendAllTextAsync(
                logPath,
                prefix + json + Environment.NewLine,
                cancellationToken)
            .ConfigureAwait(false);
        return true;
    }

    public async Task<MomLogWriteResult> LogIncomingEventAsync(
        SlackIncomingEvent incomingEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incomingEvent);

        var existing = ReadLoggedMessage(incomingEvent.ChannelId, incomingEvent.Timestamp);
        if (existing is not null)
        {
            return new MomLogWriteResult(existing, IsDuplicate: true);
        }

        var message = new MomLoggedMessage
        {
            Ts = incomingEvent.Timestamp,
            User = incomingEvent.UserId,
            UserName = ResolveUserName(incomingEvent.UserId),
            DisplayName = ResolveDisplayName(incomingEvent.UserId),
            Text = NormalizeMessageText(incomingEvent),
            Attachments = await DownloadAttachmentsAsync(
                    incomingEvent.ChannelId,
                    incomingEvent.Files,
                    incomingEvent.Timestamp,
                    cancellationToken)
                .ConfigureAwait(false),
            IsBot = false,
        };

        var logged = await LogMessageAsync(incomingEvent.ChannelId, message, cancellationToken).ConfigureAwait(false);
        if (!logged)
        {
            return new MomLogWriteResult(ReadLoggedMessage(incomingEvent.ChannelId, incomingEvent.Timestamp) ?? message, IsDuplicate: true);
        }

        return new MomLogWriteResult(message);
    }

    public async Task<MomLogWriteResult> LogSlackMessageAsync(
        string channelId,
        string userId,
        string text,
        string timestamp,
        IReadOnlyList<SlackFileReference>? files,
        bool isBot,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(userId);
        ArgumentException.ThrowIfNullOrWhiteSpace(timestamp);

        var existing = ReadLoggedMessage(channelId, timestamp);
        if (existing is not null)
        {
            return new MomLogWriteResult(existing, IsDuplicate: true);
        }

        var message = new MomLoggedMessage
        {
            Ts = timestamp,
            User = userId,
            UserName = isBot ? null : ResolveUserName(userId),
            DisplayName = isBot ? null : ResolveDisplayName(userId),
            Text = text,
            Attachments = await DownloadAttachmentsAsync(channelId, files, timestamp, cancellationToken).ConfigureAwait(false),
            IsBot = isBot,
        };

        var logged = await LogMessageAsync(channelId, message, cancellationToken).ConfigureAwait(false);
        if (!logged)
        {
            return new MomLogWriteResult(ReadLoggedMessage(channelId, timestamp) ?? message, IsDuplicate: true);
        }

        return new MomLogWriteResult(message);
    }

    public Task LogBotResponseAsync(
        string channelId,
        string text,
        string timestamp,
        CancellationToken cancellationToken = default) =>
        LogBotResponseCoreAsync(channelId, text, timestamp, cancellationToken);

    public IEnumerable<string> EnumerateLoggedChannels()
    {
        if (!Directory.Exists(WorkspaceDirectory))
        {
            yield break;
        }

        foreach (var directory in Directory.EnumerateDirectories(WorkspaceDirectory))
        {
            var channelId = Path.GetFileName(directory);
            if (string.Equals(channelId, MomDefaults.EventsDirectoryName, StringComparison.Ordinal))
            {
                continue;
            }

            if (File.Exists(Path.Combine(directory, MomDefaults.LogFileName)))
            {
                yield return channelId;
            }
        }
    }

    public string? GetLatestLoggedTimestamp(string channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        var logPath = GetLogFilePath(channelId);
        if (!File.Exists(logPath))
        {
            return null;
        }

        string? latestTimestamp = null;
        double latestValue = double.MinValue;

        foreach (var line in File.ReadLines(logPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            MomLoggedMessage? entry;
            try
            {
                entry = JsonSerializer.Deserialize<MomLoggedMessage>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is null || !TryParseSlackTimestamp(entry.Ts, out var timestampValue))
            {
                continue;
            }

            if (timestampValue > latestValue)
            {
                latestValue = timestampValue;
                latestTimestamp = entry.Ts;
            }
        }

        return latestTimestamp;
    }

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

    public MomLoggedMessage? ReadLoggedMessage(string channelId, string timestamp)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(timestamp);

        var logPath = GetLogFilePath(channelId);
        if (!File.Exists(logPath))
        {
            return null;
        }

        foreach (var line in File.ReadLines(logPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            MomLoggedMessage? entry;
            try
            {
                entry = JsonSerializer.Deserialize<MomLoggedMessage>(line, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (entry is not null && string.Equals(entry.Ts, timestamp, StringComparison.Ordinal))
            {
                return entry;
            }
        }

        return null;
    }

    public string GetChannelLabel(string channelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        return _workspaceIndex?.FindChannel(channelId)?.Name ?? channelId;
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient?.Dispose();
        }
    }

    private async Task LogBotResponseCoreAsync(
        string channelId,
        string text,
        string timestamp,
        CancellationToken cancellationToken)
    {
        await LogMessageAsync(
                channelId,
                new MomLoggedMessage
                {
                    Date = DateTimeOffset.UtcNow.ToString("O"),
                    Ts = timestamp,
                    User = "bot",
                    Text = text,
                    IsBot = true,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<MomLoggedAttachment>> DownloadAttachmentsAsync(
        string channelId,
        IReadOnlyList<SlackFileReference>? files,
        string timestamp,
        CancellationToken cancellationToken)
    {
        if (files is null || files.Count == 0)
        {
            return Array.Empty<MomLoggedAttachment>();
        }

        var attachments = new List<MomLoggedAttachment>(files.Count);

        foreach (var file in files)
        {
            var originalName = string.IsNullOrWhiteSpace(file.Name) ? "attachment" : file.Name.Trim();
            var downloadUrl = file.PrivateDownloadUrl ?? file.PrivateUrl;

            if (_httpClient is null || string.IsNullOrWhiteSpace(_slackBotToken) || string.IsNullOrWhiteSpace(downloadUrl))
            {
                attachments.Add(new MomLoggedAttachment(originalName));
                continue;
            }

            try
            {
                var relativePath = await DownloadAttachmentAsync(channelId, originalName, downloadUrl, timestamp, cancellationToken)
                    .ConfigureAwait(false);
                attachments.Add(new MomLoggedAttachment(originalName, relativePath));
            }
            catch
            {
                attachments.Add(new MomLoggedAttachment(originalName));
            }
        }

        return attachments;
    }

    private async Task<string> DownloadAttachmentAsync(
        string channelId,
        string originalName,
        string downloadUrl,
        string timestamp,
        CancellationToken cancellationToken)
    {
        var fileName = GenerateAttachmentFileName(originalName, timestamp);
        var relativePath = Path.Combine("attachments", fileName).Replace('\\', '/');
        var fullPath = Path.Combine(GetAttachmentsDirectory(channelId), fileName);

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _slackBotToken);

        using var response = await _httpClient!.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var output = File.Create(fullPath);
        await input.CopyToAsync(output, cancellationToken).ConfigureAwait(false);

        return relativePath;
    }

    private static string GenerateAttachmentFileName(string originalName, string timestamp)
    {
        var sanitizedName = string.Concat(
            originalName.Select(static character =>
                char.IsLetterOrDigit(character) || character is '.' or '_' or '-'
                    ? character
                    : '_'));

        if (string.IsNullOrWhiteSpace(sanitizedName))
        {
            sanitizedName = "attachment";
        }

        var milliseconds = ParseTimestamp(timestamp).ToUnixTimeMilliseconds();
        return $"{milliseconds}_{sanitizedName}";
    }

    private static DateTimeOffset ParseTimestamp(string timestamp)
    {
        if (TryParseSlackTimestamp(timestamp, out var slackTimestamp))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(slackTimestamp * 1000));
        }

        return DateTimeOffset.UtcNow;
    }

    private static bool TryParseSlackTimestamp(string timestamp, out double value) =>
        double.TryParse(timestamp, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static bool NeedsLeadingNewLine(string logPath)
    {
        if (!File.Exists(logPath))
        {
            return false;
        }

        using var stream = File.Open(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length == 0)
        {
            return false;
        }

        stream.Seek(-1, SeekOrigin.End);
        var lastByte = stream.ReadByte();
        return lastByte != '\n';
    }

    private string? ResolveUserName(string userId) =>
        _workspaceIndex?.FindUser(userId)?.UserName;

    private string? ResolveDisplayName(string userId) =>
        _workspaceIndex?.FindUser(userId)?.DisplayName;
}
