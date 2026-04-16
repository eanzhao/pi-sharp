using System.Globalization;
using System.Net.Http.Headers;
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

    public MomChannelStore(string workspaceDirectory, string? slackBotToken = null, HttpClient? httpClient = null)
    {
        WorkspaceDirectory = Path.GetFullPath(workspaceDirectory ?? throw new ArgumentNullException(nameof(workspaceDirectory)));
        _slackBotToken = string.IsNullOrWhiteSpace(slackBotToken) ? null : slackBotToken.Trim();
        _httpClient = httpClient ?? (_slackBotToken is null ? null : new HttpClient());
        _ownsHttpClient = httpClient is null && _httpClient is not null;
        Directory.CreateDirectory(WorkspaceDirectory);
    }

    public string WorkspaceDirectory { get; }

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

    public async Task<MomLoggedMessage> LogIncomingEventAsync(
        SlackIncomingEvent incomingEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(incomingEvent);

        var message = new MomLoggedMessage
        {
            Ts = incomingEvent.Timestamp,
            User = incomingEvent.UserId,
            Text = NormalizeMessageText(incomingEvent),
            Attachments = await DownloadAttachmentsAsync(
                    incomingEvent.ChannelId,
                    incomingEvent.Files,
                    incomingEvent.Timestamp,
                    cancellationToken)
                .ConfigureAwait(false),
            IsBot = false,
        };

        await LogMessageAsync(incomingEvent.ChannelId, message, cancellationToken).ConfigureAwait(false);
        return message;
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

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient?.Dispose();
        }
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
        if (double.TryParse(timestamp, NumberStyles.Float, CultureInfo.InvariantCulture, out var slackTimestamp))
        {
            return DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Floor(slackTimestamp * 1000));
        }

        return DateTimeOffset.UtcNow;
    }
}
