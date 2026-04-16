using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Globalization;

namespace PiSharp.Mom;

public sealed class SlackSocketModeClient
{
    private readonly SlackWebApiClient _webApiClient;
    private readonly string _appToken;

    public SlackSocketModeClient(SlackWebApiClient webApiClient, string appToken)
    {
        _webApiClient = webApiClient ?? throw new ArgumentNullException(nameof(webApiClient));
        _appToken = string.IsNullOrWhiteSpace(appToken)
            ? throw new ArgumentException("Slack app token is required.", nameof(appToken))
            : appToken.Trim();
    }

    public async Task RunAsync(
        string botUserId,
        Func<SlackIncomingEvent, CancellationToken, Task> onEventAsync,
        string? responseCutoffTimestamp = null,
        MomRuntimeStats? runtimeStats = null,
        Func<string, CancellationToken, Task>? reportNoticeAsync = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botUserId);
        ArgumentNullException.ThrowIfNull(onEventAsync);

        var connectionGeneration = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            var socketUrl = await _webApiClient.OpenSocketConnectionAsync(_appToken, cancellationToken).ConfigureAwait(false);
            await socket.ConnectAsync(new Uri(socketUrl), cancellationToken).ConfigureAwait(false);

            if (connectionGeneration > 0)
            {
                runtimeStats?.RecordReconnect(connectionGeneration, DateTimeOffset.UtcNow);

                if (reportNoticeAsync is not null)
                {
                    try
                    {
                        await reportNoticeAsync(
                                $"Slack Socket Mode reconnected #{connectionGeneration}",
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        // Reconnect notices are best-effort and should not interfere with the socket loop.
                    }
                }
            }

            var reconnect = await ReceiveLoopAsync(
                    socket,
                    botUserId,
                    onEventAsync,
                    responseCutoffTimestamp,
                    connectionGeneration,
                    cancellationToken)
                .ConfigureAwait(false);
            if (!reconnect)
            {
                return;
            }

            connectionGeneration++;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ReceiveLoopAsync(
        ClientWebSocket socket,
        string botUserId,
        Func<SlackIncomingEvent, CancellationToken, Task> onEventAsync,
        string? responseCutoffTimestamp,
        int connectionGeneration,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[8 * 1024];

        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            using var payload = new MemoryStream();
            WebSocketReceiveResult? result = null;

            do
            {
                result = await socket.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return true;
                }

                payload.Write(buffer, 0, result.Count);
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
            {
                continue;
            }

            payload.Position = 0;
            using var document = await JsonDocument.ParseAsync(payload, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            if (root.TryGetProperty("envelope_id", out var envelopeId) && envelopeId.ValueKind == JsonValueKind.String)
            {
                await AcknowledgeAsync(socket, envelopeId.GetString()!, cancellationToken).ConfigureAwait(false);
            }

            var envelopeType = root.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
                ? typeElement.GetString()
                : null;

            if (string.Equals(envelopeType, "disconnect", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (TryParseIncomingEvent(root, botUserId, out var incomingEvent) && incomingEvent is not null)
            {
                incomingEvent = ApplyResponseCutoff(
                    incomingEvent with
                    {
                        ConnectionGeneration = connectionGeneration,
                    },
                    responseCutoffTimestamp);
                await onEventAsync(incomingEvent, cancellationToken).ConfigureAwait(false);
            }
        }

        return !cancellationToken.IsCancellationRequested;
    }

    public static bool TryParseIncomingEvent(JsonElement root, string botUserId, out SlackIncomingEvent? incomingEvent)
    {
        incomingEvent = null;

        if (!TryGetString(root, "type", out var envelopeType) ||
            !string.Equals(envelopeType, "events_api", StringComparison.OrdinalIgnoreCase) ||
            !root.TryGetProperty("payload", out var payload) ||
            !TryGetString(payload, "type", out var payloadType) ||
            !string.Equals(payloadType, "event_callback", StringComparison.OrdinalIgnoreCase) ||
            !payload.TryGetProperty("event", out var eventElement) ||
            !TryGetString(eventElement, "type", out var eventType))
        {
            return false;
        }

        var isFileShare = false;
        if (eventElement.TryGetProperty("subtype", out var subtype) && subtype.ValueKind == JsonValueKind.String)
        {
            isFileShare = string.Equals(subtype.GetString(), "file_share", StringComparison.OrdinalIgnoreCase);
            if (!isFileShare)
            {
                return false;
            }
        }

        if (!TryGetString(eventElement, "user", out var userId) ||
            string.Equals(userId, botUserId, StringComparison.Ordinal) ||
            !TryGetString(eventElement, "channel", out var channelId) ||
            !TryGetString(eventElement, "ts", out var timestamp))
        {
            return false;
        }

        var isDirectMessage = channelId.StartsWith('D');
        var text = TryGetString(eventElement, "text", out var parsedText) ? parsedText : string.Empty;
        var files = GetFiles(eventElement);
        if (string.IsNullOrWhiteSpace(text) && files.Count == 0)
        {
            return false;
        }

        if (string.Equals(eventType, "message", StringComparison.OrdinalIgnoreCase) &&
            !isDirectMessage &&
            text.Contains($"<@{botUserId}>", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var requiresResponse =
            string.Equals(eventType, "app_mention", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(eventType, "message", StringComparison.OrdinalIgnoreCase) && isDirectMessage);

        if (!requiresResponse && !string.Equals(eventType, "message", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        incomingEvent = new SlackIncomingEvent(
            channelId,
            userId,
            text,
            timestamp,
            eventType,
            isDirectMessage,
            files,
            RequiresResponse: requiresResponse);

        return true;
    }

    public static SlackIncomingEvent ApplyResponseCutoff(
        SlackIncomingEvent incomingEvent,
        string? responseCutoffTimestamp)
    {
        ArgumentNullException.ThrowIfNull(incomingEvent);

        if (!incomingEvent.RequiresResponse ||
            string.IsNullOrWhiteSpace(responseCutoffTimestamp) ||
            !TryParseTimestamp(incomingEvent.Timestamp, out var messageTimestamp) ||
            !TryParseTimestamp(responseCutoffTimestamp, out var cutoffTimestamp) ||
            messageTimestamp >= cutoffTimestamp)
        {
            return incomingEvent;
        }

        return incomingEvent with { RequiresResponse = false };
    }

    private static async Task AcknowledgeAsync(
        ClientWebSocket socket,
        string envelopeId,
        CancellationToken cancellationToken)
    {
        if (socket.State != WebSocketState.Open)
        {
            return;
        }

        var buffer = Encoding.UTF8.GetBytes($$"""{"envelope_id":"{{envelopeId}}"}""");
        await socket.SendAsync(buffer, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
    }

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

    private static bool TryParseTimestamp(string value, out double timestamp) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out timestamp);
}
