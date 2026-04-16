using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace PiSharp.Mom;

public sealed class SlackWebApiClient : ISlackMessagingClient, IDisposable
{
    private static readonly Uri BaseUri = new("https://slack.com/api/");
    private readonly HttpClient _httpClient;
    private readonly string _botToken;
    private bool _disposed;

    public SlackWebApiClient(string botToken, HttpClient? httpClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);

        _botToken = botToken;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<SlackAuthInfo> AuthenticateAsync(CancellationToken cancellationToken = default)
    {
        var root = await PostApiAsync("auth.test", new { }, _botToken, cancellationToken).ConfigureAwait(false);
        return new SlackAuthInfo(GetRequiredString(root, "user_id"));
    }

    public async Task<string> PostMessageAsync(
        string channelId,
        string text,
        string? threadTimestamp = null,
        CancellationToken cancellationToken = default)
    {
        var root = await PostApiAsync(
                "chat.postMessage",
                new
                {
                    channel = channelId,
                    text,
                    thread_ts = threadTimestamp,
                },
                _botToken,
                cancellationToken)
            .ConfigureAwait(false);

        return GetRequiredString(root, "ts");
    }

    public async Task UpdateMessageAsync(
        string channelId,
        string timestamp,
        string text,
        CancellationToken cancellationToken = default)
    {
        await PostApiAsync(
                "chat.update",
                new
                {
                    channel = channelId,
                    ts = timestamp,
                    text,
                },
                _botToken,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task DeleteMessageAsync(
        string channelId,
        string timestamp,
        CancellationToken cancellationToken = default)
    {
        await PostApiAsync(
                "chat.delete",
                new
                {
                    channel = channelId,
                    ts = timestamp,
                },
                _botToken,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task UploadFileAsync(
        string channelId,
        string filePath,
        string? title = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);

        var fullPath = Path.GetFullPath(filePath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"File not found: {fullPath}", fullPath);
        }

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(channelId), "channel_id");
        form.Add(new StringContent(Path.GetFileName(fullPath)), "filename");

        var effectiveTitle = string.IsNullOrWhiteSpace(title) ? Path.GetFileName(fullPath) : title.Trim();
        form.Add(new StringContent(effectiveTitle), "title");

        await using var stream = File.OpenRead(fullPath);
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", Path.GetFileName(fullPath));

        await PostApiMultipartAsync("files.uploadV2", form, _botToken, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<SlackConversationHistoryPage> GetConversationHistoryAsync(
        string channelId,
        string? oldest = null,
        string? cursor = null,
        int limit = 200,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(channelId);

        var root = await PostApiAsync(
                "conversations.history",
                new
                {
                    channel = channelId,
                    oldest,
                    inclusive = false,
                    limit,
                    cursor,
                },
                _botToken,
                cancellationToken)
            .ConfigureAwait(false);

        var messages = new List<SlackConversationHistoryMessage>();
        if (root.TryGetProperty("messages", out var messagesElement) && messagesElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var messageElement in messagesElement.EnumerateArray())
            {
                if (!TryGetString(messageElement, "ts", out var timestamp))
                {
                    continue;
                }

                TryGetString(messageElement, "user", out var userId);
                TryGetString(messageElement, "bot_id", out var botId);
                TryGetString(messageElement, "text", out var text);
                TryGetString(messageElement, "subtype", out var subtype);

                messages.Add(new SlackConversationHistoryMessage(
                    timestamp,
                    string.IsNullOrWhiteSpace(userId) ? null : userId,
                    string.IsNullOrWhiteSpace(botId) ? null : botId,
                    text,
                    string.IsNullOrWhiteSpace(subtype) ? null : subtype,
                    GetFiles(messageElement)));
            }
        }

        string? nextCursor = null;
        if (root.TryGetProperty("response_metadata", out var metadataElement) &&
            metadataElement.ValueKind == JsonValueKind.Object &&
            TryGetString(metadataElement, "next_cursor", out var parsedCursor) &&
            !string.IsNullOrWhiteSpace(parsedCursor))
        {
            nextCursor = parsedCursor;
        }

        return new SlackConversationHistoryPage(messages, nextCursor);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _httpClient.Dispose();
        _disposed = true;
    }

    internal async Task<string> OpenSocketConnectionAsync(string appToken, CancellationToken cancellationToken)
    {
        var root = await PostApiAsync("apps.connections.open", new { }, appToken, cancellationToken).ConfigureAwait(false);
        return GetRequiredString(root, "url");
    }

    private async Task<JsonElement> PostApiAsync(
        string method,
        object payload,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, method))
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement.Clone();

        if (!root.TryGetProperty("ok", out var okElement) || !okElement.GetBoolean())
        {
            var error = root.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : "unknown_error";
            throw new InvalidOperationException($"Slack API '{method}' failed: {error}");
        }

        return root;
    }

    private async Task<JsonElement> PostApiMultipartAsync(
        string method,
        MultipartFormDataContent payload,
        string token,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(BaseUri, method))
        {
            Content = payload,
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement.Clone();

        if (!root.TryGetProperty("ok", out var okElement) || !okElement.GetBoolean())
        {
            var error = root.TryGetProperty("error", out var errorElement)
                ? errorElement.GetString()
                : "unknown_error";
            throw new InvalidOperationException($"Slack API '{method}' failed: {error}");
        }

        return root;
    }

    private static string GetRequiredString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : throw new InvalidOperationException($"Slack response is missing '{propertyName}'.");

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        if (element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static IReadOnlyList<SlackFileReference> GetFiles(JsonElement eventElement)
    {
        if (!eventElement.TryGetProperty("files", out var filesElement) || filesElement.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<SlackFileReference>();
        }

        var files = new List<SlackFileReference>();
        foreach (var fileElement in filesElement.EnumerateArray())
        {
            if (!TryGetString(fileElement, "name", out var name))
            {
                continue;
            }

            TryGetString(fileElement, "url_private_download", out var privateDownloadUrl);
            TryGetString(fileElement, "url_private", out var privateUrl);
            files.Add(new SlackFileReference(name, privateDownloadUrl, privateUrl));
        }

        return files;
    }
}
