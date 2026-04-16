using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

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
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botUserId);
        ArgumentNullException.ThrowIfNull(onEventAsync);

        while (!cancellationToken.IsCancellationRequested)
        {
            using var socket = new ClientWebSocket();
            var socketUrl = await _webApiClient.OpenSocketConnectionAsync(_appToken, cancellationToken).ConfigureAwait(false);
            await socket.ConnectAsync(new Uri(socketUrl), cancellationToken).ConfigureAwait(false);

            var reconnect = await ReceiveLoopAsync(socket, botUserId, onEventAsync, cancellationToken).ConfigureAwait(false);
            if (!reconnect)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ReceiveLoopAsync(
        ClientWebSocket socket,
        string botUserId,
        Func<SlackIncomingEvent, CancellationToken, Task> onEventAsync,
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

        if (eventElement.TryGetProperty("subtype", out var subtype) && subtype.ValueKind != JsonValueKind.Null)
        {
            return false;
        }

        if (!TryGetString(eventElement, "user", out var userId) ||
            string.Equals(userId, botUserId, StringComparison.Ordinal) ||
            !TryGetString(eventElement, "channel", out var channelId) ||
            !TryGetString(eventElement, "ts", out var timestamp))
        {
            return false;
        }

        var isDirectMessage = channelId.StartsWith('D');
        var isRelevant =
            string.Equals(eventType, "app_mention", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(eventType, "message", StringComparison.OrdinalIgnoreCase) && isDirectMessage);

        if (!isRelevant)
        {
            return false;
        }

        incomingEvent = new SlackIncomingEvent(
            channelId,
            userId,
            TryGetString(eventElement, "text", out var text) ? text : string.Empty,
            timestamp,
            eventType,
            isDirectMessage);

        return true;
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
}
