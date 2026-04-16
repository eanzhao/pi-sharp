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

    private static string GetRequiredString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()!
            : throw new InvalidOperationException($"Slack response is missing '{propertyName}'.");
}
