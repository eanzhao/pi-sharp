using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.AI;

namespace PiSharp.CodingAgent;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionMessageEntry), "message")]
[JsonDerivedType(typeof(ThinkingLevelChangeEntry), "thinkingLevelChange")]
[JsonDerivedType(typeof(ModelChangeEntry), "modelChange")]
[JsonDerivedType(typeof(CompactionEntry), "compaction")]
[JsonDerivedType(typeof(LabelEntry), "label")]
public abstract record SessionEntry
{
    public required string Id { get; init; }
    public string? ParentId { get; init; }
    public required string Timestamp { get; init; }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];
    public static string Now() => DateTimeOffset.UtcNow.ToString("O");
}

public sealed record SessionHeader
{
    [JsonPropertyName("type")]
    public string Type => "session";
    public int Version { get; init; } = 2;
    public required string Id { get; init; }
    public required string Timestamp { get; init; }
    public required string Cwd { get; init; }
    public string? ParentSession { get; init; }
    public string? ProviderId { get; init; }
    public string? ModelId { get; init; }
    public string? ThinkingLevel { get; init; }
    public string? SystemPrompt { get; init; }
    public IReadOnlyList<string> ToolNames { get; init; } = Array.Empty<string>();
}

public sealed record SessionMessageEntry : SessionEntry
{
    public string? Role { get; init; }
    public string? Text { get; init; }
    public SessionChatMessage? Message { get; init; }

    public ChatMessage ToChatMessage() => (Message ?? CreateFallbackMessage()).ToChatMessage();

    public static SessionMessageEntry FromChatMessage(ChatMessage message, string? parentId = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new SessionMessageEntry
        {
            Id = SessionEntry.NewId(),
            ParentId = parentId,
            Timestamp = message.CreatedAt?.ToString("O") ?? SessionEntry.Now(),
            Role = message.Role.Value,
            Text = SessionChatMessage.ExtractPlainText(message),
            Message = SessionChatMessage.FromChatMessage(message),
        };
    }

    private SessionChatMessage CreateFallbackMessage() =>
        new()
        {
            Role = Role ?? ChatRole.User.Value,
            Contents =
            [
                new SessionContent
                {
                    Type = SessionContentType.Text,
                    Text = Text ?? string.Empty,
                },
            ],
        };
}

public sealed record ThinkingLevelChangeEntry : SessionEntry
{
    public required string Level { get; init; }
}

public sealed record ModelChangeEntry : SessionEntry
{
    public required string ProviderId { get; init; }
    public required string ModelId { get; init; }
}

public sealed record CompactionEntry : SessionEntry
{
    public required string Summary { get; init; }
    public required string FirstKeptEntryId { get; init; }
    public required int TokensBefore { get; init; }
    public CompactionDetails? Details { get; init; }
}

public sealed record LabelEntry : SessionEntry
{
    public required string TargetEntryId { get; init; }
    public string? Label { get; init; }
}

public record SessionContext(
    IReadOnlyList<ChatMessage> Messages,
    string? ThinkingLevel,
    string? ProviderId,
    string? ModelId,
    string? SystemPrompt,
    IReadOnlyList<string> ToolNames);

public enum SessionContentType
{
    Text,
    Reasoning,
    FunctionCall,
    FunctionResult,
    Data,
}

public sealed record SessionContent
{
    public required SessionContentType Type { get; init; }
    public string? Text { get; init; }
    public string? DataUri { get; init; }
    public string? MediaType { get; init; }
    public string? CallId { get; init; }
    public string? Name { get; init; }
    public Dictionary<string, object?>? Arguments { get; init; }
    public JsonElement? Result { get; init; }

    public AIContent ToContent() =>
        Type switch
        {
            SessionContentType.Text => new TextContent(Text ?? string.Empty),
            SessionContentType.Reasoning => new TextReasoningContent(Text ?? string.Empty),
            SessionContentType.FunctionCall => new FunctionCallContent(
                CallId ?? string.Empty,
                Name ?? string.Empty,
                Arguments ?? new Dictionary<string, object?>(StringComparer.Ordinal)),
            SessionContentType.FunctionResult => new FunctionResultContent(CallId ?? string.Empty, DeserializeResult()),
            SessionContentType.Data => new DataContent(
                DataUri ?? string.Empty,
                MediaType ?? "application/octet-stream")
            {
                Name = Name,
            },
            _ => new TextContent(Text ?? string.Empty),
        };

    public static SessionContent FromContent(AIContent content) =>
        content switch
        {
            TextContent text => new SessionContent
            {
                Type = SessionContentType.Text,
                Text = text.Text,
            },
            TextReasoningContent reasoning => new SessionContent
            {
                Type = SessionContentType.Reasoning,
                Text = reasoning.Text,
            },
            FunctionCallContent toolCall => new SessionContent
            {
                Type = SessionContentType.FunctionCall,
                CallId = toolCall.CallId,
                Name = toolCall.Name,
                Arguments = toolCall.Arguments is null
                    ? new Dictionary<string, object?>(StringComparer.Ordinal)
                    : new Dictionary<string, object?>(toolCall.Arguments, StringComparer.Ordinal),
            },
            FunctionResultContent toolResult => new SessionContent
            {
                Type = SessionContentType.FunctionResult,
                CallId = toolResult.CallId,
                Result = JsonSerializer.SerializeToElement(toolResult.Result),
            },
            DataContent data => new SessionContent
            {
                Type = SessionContentType.Data,
                DataUri = data.Uri,
                MediaType = data.MediaType,
                Name = data.Name,
            },
            _ => new SessionContent
            {
                Type = SessionContentType.Text,
                Text = content.ToString(),
            },
        };

    private object? DeserializeResult()
    {
        if (Result is null)
        {
            return null;
        }

        return JsonSerializer.Deserialize<object>(Result.Value.GetRawText());
    }
}

public sealed record SessionChatMessage
{
    public required string Role { get; init; }
    public string? AuthorName { get; init; }
    public string? MessageId { get; init; }
    public string? CreatedAt { get; init; }
    public IReadOnlyList<SessionContent> Contents { get; init; } = Array.Empty<SessionContent>();

    public ChatMessage ToChatMessage()
    {
        var message = new ChatMessage(new ChatRole(Role), Contents.Select(static content => content.ToContent()).ToList())
        {
            AuthorName = AuthorName,
            MessageId = MessageId,
        };

        if (DateTimeOffset.TryParse(CreatedAt, out var createdAt))
        {
            message.CreatedAt = createdAt;
        }

        return message;
    }

    public static SessionChatMessage FromChatMessage(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new SessionChatMessage
        {
            Role = message.Role.Value,
            AuthorName = message.AuthorName,
            MessageId = message.MessageId,
            CreatedAt = message.CreatedAt?.ToString("O"),
            Contents = message.Contents.Select(SessionContent.FromContent).ToArray(),
        };
    }

    public static string? ExtractPlainText(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var parts = message.Contents
            .Select(content => content switch
            {
                TextContent text => text.Text,
                TextReasoningContent reasoning => reasoning.Text,
                FunctionCallContent toolCall => $"{toolCall.Name}({JsonSerializer.Serialize(toolCall.Arguments)})",
                FunctionResultContent toolResult => toolResult.Result?.ToString(),
                DataContent data => data.Name ?? $"[{data.MediaType}]",
                _ => content.ToString(),
            })
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        return parts.Length == 0
            ? null
            : string.Join("\n", parts);
    }
}
