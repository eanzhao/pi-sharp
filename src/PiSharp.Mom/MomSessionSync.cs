using Microsoft.Extensions.AI;
using PiSharp.CodingAgent;

namespace PiSharp.Mom;

public static class MomSessionSync
{
    private static readonly System.Text.Json.JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static IReadOnlyList<ChatMessage> SyncLogToSessionManager(
        SessionManager sessionManager,
        string logFilePath,
        string? excludeTimestamp = null)
    {
        ArgumentNullException.ThrowIfNull(sessionManager);
        ArgumentException.ThrowIfNullOrWhiteSpace(logFilePath);

        if (!File.Exists(logFilePath))
        {
            return Array.Empty<ChatMessage>();
        }

        var existingMessages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in sessionManager.Entries.OfType<SessionMessageEntry>())
        {
            var chatMessage = entry.ToChatMessage();
            if (chatMessage.Role != ChatRole.User)
            {
                continue;
            }

            var normalized = SessionChatMessage.ExtractPlainText(chatMessage);
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                existingMessages.Add(normalized);
            }
        }

        var synchronizedMessages = new List<ChatMessage>();
        foreach (var line in File.ReadLines(logFilePath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            MomLoggedMessage? loggedMessage;
            try
            {
                loggedMessage = System.Text.Json.JsonSerializer.Deserialize<MomLoggedMessage>(line, JsonOptions);
            }
            catch (System.Text.Json.JsonException)
            {
                continue;
            }

            if (loggedMessage is null ||
                loggedMessage.IsBot ||
                !HasContent(loggedMessage) ||
                string.Equals(loggedMessage.Ts, excludeTimestamp, StringComparison.Ordinal))
            {
                continue;
            }

            var messageText = FormatForContext(loggedMessage);
            if (!existingMessages.Add(messageText))
            {
                continue;
            }

            var message = new ChatMessage(ChatRole.User, messageText);
            if (DateTimeOffset.TryParse(loggedMessage.Date, out var createdAt))
            {
                message.CreatedAt = createdAt;
            }

            synchronizedMessages.Add(message);
            sessionManager.AppendEntry(SessionMessageEntry.FromChatMessage(message));
        }

        return synchronizedMessages;
    }

    public static string FormatForContext(MomLoggedMessage loggedMessage)
    {
        ArgumentNullException.ThrowIfNull(loggedMessage);

        var user = loggedMessage.UserName ?? loggedMessage.User;
        var header = string.IsNullOrWhiteSpace(loggedMessage.Text)
            ? $"[{user}]: shared attachments"
            : $"[{user}]: {loggedMessage.Text}";

        if (loggedMessage.Attachments.Count == 0)
        {
            return header;
        }

        var attachmentLines = loggedMessage.Attachments
            .Select(static attachment =>
                string.IsNullOrWhiteSpace(attachment.Local)
                    ? $"- {attachment.Original}"
                    : $"- {attachment.Original} => {attachment.Local}")
            .ToArray();

        return
            $"""
{header}

<slack_attachments>
{string.Join(Environment.NewLine, attachmentLines)}
</slack_attachments>
""";
    }

    private static bool HasContent(MomLoggedMessage loggedMessage) =>
        !string.IsNullOrWhiteSpace(loggedMessage.Text) || loggedMessage.Attachments.Count > 0;
}
