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
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(messages);

        var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        var payload = JsonSerializer.Serialize(messages, SerializerOptions);
        await module.InvokeVoidAsync("saveSession", cancellationToken, sessionId, payload).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SessionChatMessage>> LoadSessionAsync(
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        var payload = await module.InvokeAsync<string?>("loadSession", cancellationToken, sessionId).ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(payload))
        {
            return Array.Empty<SessionChatMessage>();
        }

        return JsonSerializer.Deserialize<SessionChatMessage[]>(payload, SerializerOptions) ?? Array.Empty<SessionChatMessage>();
    }

    public async Task<IReadOnlyList<string>> ListSessionsAsync(CancellationToken cancellationToken = default)
    {
        var module = await GetModuleAsync(cancellationToken).ConfigureAwait(false);
        return await module.InvokeAsync<string[]>("listSessions", cancellationToken).ConfigureAwait(false)
            ?? Array.Empty<string>();
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
}
