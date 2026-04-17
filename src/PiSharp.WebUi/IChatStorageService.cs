using PiSharp.CodingAgent;

namespace PiSharp.WebUi;

public sealed record ChatSessionMetadata(
    string SessionId,
    string Title,
    string? ModelId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ChatSessionRecord(
    ChatSessionMetadata Metadata,
    IReadOnlyList<SessionChatMessage> Messages);

public sealed record ChatSessionQuery(
    string? TitleContains = null,
    string? ModelId = null);

public interface IChatStorageService
{
    Task SaveSessionAsync(string sessionId, IReadOnlyList<SessionChatMessage> messages, CancellationToken cancellationToken = default);

    Task SaveSessionAsync(
        string sessionId,
        ChatSessionMetadata metadata,
        IReadOnlyList<SessionChatMessage> messages,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SessionChatMessage>> LoadSessionAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default);

    Task<ChatSessionRecord?> LoadSessionRecordAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<ChatSessionMetadata?> GetSessionMetadataAsync(string sessionId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ChatSessionMetadata>> ListSessionsAsync(
        ChatSessionQuery? query,
        CancellationToken cancellationToken = default);

    Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default);
}
