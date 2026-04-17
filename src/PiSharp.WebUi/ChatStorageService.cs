using System.Text.Json;
using Microsoft.JSInterop;
using PiSharp.CodingAgent;

namespace PiSharp.WebUi;

public sealed class ChatStorageService : IChatStorageService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    private readonly IJSRuntime _jsRuntime;
    private readonly string _modulePath;
    private Task<IJSObjectReference>? _moduleTask;
    private Task? _openTask;

    public ChatStorageService(IJSRuntime jsRuntime, string modulePath = "./_content/PiSharp.WebUi/pisharp-storage.js")
    {
        _jsRuntime = jsRuntime ?? throw new ArgumentNullException(nameof(jsRuntime));
        _modulePath = string.IsNullOrWhiteSpace(modulePath)
            ? "./_content/PiSharp.WebUi/pisharp-storage.js"
            : modulePath;
    }

    public async Task SaveSessionAsync(
        string sessionId,
        IReadOnlyList<SessionChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        var metadata = CreateDefaultMetadata(sessionId, messages);
        await SaveSessionAsync(sessionId, metadata, messages, cancellationToken).ConfigureAwait(false);
    }

    public async Task SaveSessionAsync(
        string sessionId,
        ChatSessionMetadata metadata,
        IReadOnlyList<SessionChatMessage> messages,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(messages);

        var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(messages, SerializerOptions);
        var metadataPayload = JsonSerializer.Serialize(NormalizeMetadata(sessionId, metadata), SerializerOptions);
        await module.InvokeVoidAsync("saveSession", cancellationToken, sessionId, payload, metadataPayload).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionChatMessage>> LoadSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        var record = await LoadSessionRecordAsync(sessionId, cancellationToken).ConfigureAwait(false);
        return record?.Messages ?? Array.Empty<SessionChatMessage>();
    }

    public async Task<ChatSessionRecord?> LoadSessionRecordAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        var payload = await module.InvokeAsync<string?>("loadSessionRecord", cancellationToken, sessionId).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        return JsonSerializer.Deserialize<ChatSessionRecord>(payload, SerializerOptions);
    }

    public async Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        return await module.InvokeAsync<string[]>("listSessions", cancellationToken).ConfigureAwait(false)
            ?? Array.Empty<string>();
    }

    public async Task<ChatSessionMetadata?> GetSessionMetadataAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        var payload = await module.InvokeAsync<string?>("getSessionMetadata", cancellationToken, sessionId).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(payload)
            ? null
            : JsonSerializer.Deserialize<ChatSessionMetadata>(payload, SerializerOptions);
    }

    public async Task<IReadOnlyList<ChatSessionMetadata>> ListSessionsAsync(
        ChatSessionQuery? query,
        CancellationToken cancellationToken = default)
    {
        var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        var queryPayload = JsonSerializer.Serialize(query ?? new ChatSessionQuery(), SerializerOptions);
        var payload = await module.InvokeAsync<string?>("searchSessions", cancellationToken, queryPayload).ConfigureAwait(false);

        return string.IsNullOrWhiteSpace(payload)
            ? Array.Empty<ChatSessionMetadata>()
            : JsonSerializer.Deserialize<ChatSessionMetadata[]>(payload, SerializerOptions) ?? Array.Empty<ChatSessionMetadata>();
    }

    public async Task DeleteSessionAsync(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        await module.InvokeVoidAsync("deleteSession", cancellationToken, sessionId).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_moduleTask is not null)
        {
            var module = await _moduleTask.ConfigureAwait(false);
            await module.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task<IJSObjectReference> GetModuleAsync(CancellationToken cancellationToken)
    {
        _moduleTask ??= _jsRuntime.InvokeAsync<IJSObjectReference>("import", cancellationToken, _modulePath).AsTask();
        var module = await _moduleTask.ConfigureAwait(false);

        _openTask ??= module.InvokeVoidAsync("openDb", cancellationToken).AsTask();
        await _openTask.ConfigureAwait(false);

        return module;
    }

    internal static ChatSessionMetadata CreateDefaultMetadata(
        string sessionId,
        IReadOnlyList<SessionChatMessage> messages)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(messages);

        var timestamp = ResolveCreatedAt(messages) ?? DateTimeOffset.UtcNow;
        return new ChatSessionMetadata(
            sessionId,
            ResolveTitle(sessionId, messages),
            null,
            timestamp,
            timestamp);
    }

    private static ChatSessionMetadata NormalizeMetadata(string sessionId, ChatSessionMetadata metadata)
    {
        var createdAt = metadata.CreatedAt == default ? DateTimeOffset.UtcNow : metadata.CreatedAt;
        var updatedAt = metadata.UpdatedAt == default ? createdAt : metadata.UpdatedAt;

        return metadata with
        {
            SessionId = sessionId,
            Title = string.IsNullOrWhiteSpace(metadata.Title) ? sessionId : metadata.Title.Trim(),
            CreatedAt = createdAt,
            UpdatedAt = updatedAt,
            ModelId = string.IsNullOrWhiteSpace(metadata.ModelId) ? null : metadata.ModelId.Trim(),
        };
    }

    private static DateTimeOffset? ResolveCreatedAt(IReadOnlyList<SessionChatMessage> messages)
    {
        foreach (var message in messages)
        {
            if (DateTimeOffset.TryParse(message.CreatedAt, out var createdAt))
            {
                return createdAt;
            }
        }

        return null;
    }

    private static string ResolveTitle(string sessionId, IReadOnlyList<SessionChatMessage> messages)
    {
        foreach (var message in messages)
        {
            foreach (var content in message.Contents)
            {
                if (content.Type != SessionContentType.Text || string.IsNullOrWhiteSpace(content.Text))
                {
                    continue;
                }

                var normalized = content.Text.Trim().ReplaceLineEndings(" ");
                if (normalized.Length <= 80)
                {
                    return normalized;
                }

                return $"{normalized[..77].TrimEnd()}...";
            }
        }

        return sessionId;
    }
}
