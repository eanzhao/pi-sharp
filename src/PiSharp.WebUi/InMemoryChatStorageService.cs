using PiSharp.CodingAgent;

namespace PiSharp.WebUi;

public sealed class InMemoryChatStorageService : IChatStorageService
{
    private readonly Dictionary<string, ChatSessionRecord> _sessions = new(StringComparer.Ordinal);

    public Task SaveSessionAsync(
        string sessionId,
        IReadOnlyList<SessionChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var metadata = ChatStorageService.CreateDefaultMetadata(sessionId, messages);
        return SaveSessionAsync(sessionId, metadata, messages, cancellationToken);
    }

    public Task SaveSessionAsync(
        string sessionId,
        ChatSessionMetadata metadata,
        IReadOnlyList<SessionChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(messages);

        var normalizedMetadata = metadata with
        {
            SessionId = sessionId,
            Title = string.IsNullOrWhiteSpace(metadata.Title) ? sessionId : metadata.Title.Trim(),
            CreatedAt = metadata.CreatedAt == default ? DateTimeOffset.UtcNow : metadata.CreatedAt,
            UpdatedAt = DateTimeOffset.UtcNow,
            ModelId = string.IsNullOrWhiteSpace(metadata.ModelId) ? null : metadata.ModelId.Trim(),
        };

        _sessions[sessionId] = new ChatSessionRecord(normalizedMetadata, messages.ToArray());
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SessionChatMessage>> LoadSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return Task.FromResult<IReadOnlyList<SessionChatMessage>>(
            _sessions.TryGetValue(sessionId, out var record)
                ? record.Messages
                : Array.Empty<SessionChatMessage>());
    }

    public Task<ChatSessionRecord?> LoadSessionRecordAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return Task.FromResult(
            _sessions.TryGetValue(sessionId, out var record)
                ? record
                : null);
    }

    public Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult<IReadOnlyList<string>>(
            _sessions.Values
                .OrderByDescending(static record => record.Metadata.UpdatedAt)
                .ThenBy(static record => record.Metadata.SessionId, StringComparer.Ordinal)
                .Select(static record => record.Metadata.SessionId)
                .ToArray());
    }

    public Task<ChatSessionMetadata?> GetSessionMetadataAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        return Task.FromResult(
            _sessions.TryGetValue(sessionId, out var record)
                ? record.Metadata
                : null);
    }

    public Task<IReadOnlyList<ChatSessionMetadata>> ListSessionsAsync(
        ChatSessionQuery? query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var titleFilter = query?.TitleContains?.Trim();
        var modelFilter = query?.ModelId?.Trim();

        return Task.FromResult<IReadOnlyList<ChatSessionMetadata>>(
            _sessions.Values
                .Select(static record => record.Metadata)
                .Where(metadata =>
                    string.IsNullOrWhiteSpace(titleFilter) ||
                    metadata.Title.Contains(titleFilter, StringComparison.OrdinalIgnoreCase))
                .Where(metadata =>
                    string.IsNullOrWhiteSpace(modelFilter) ||
                    string.Equals(metadata.ModelId, modelFilter, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(static metadata => metadata.UpdatedAt)
                .ThenBy(static metadata => metadata.SessionId, StringComparer.Ordinal)
                .ToArray());
    }

    public Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        _sessions.Remove(sessionId);
        return Task.CompletedTask;
    }
}
