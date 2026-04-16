using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace PiSharp.Pods.Tests.Support;

internal sealed class FakeChatClient(params IReadOnlyList<ChatResponseUpdate>[] responses) : IChatClient
{
    private readonly Queue<IReadOnlyList<ChatResponseUpdate>> _responses = new(responses);

    public List<IReadOnlyList<ChatMessage>> Requests { get; } = [];

    public List<ChatOptions> Options { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(messages.ToArray());
        Options.Add(options?.Clone() ?? new ChatOptions());

        if (_responses.Count == 0)
        {
            throw new InvalidOperationException("No fake response is queued.");
        }

        return ToAsyncEnumerable(_responses.Dequeue(), cancellationToken);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ToAsyncEnumerable(
        IReadOnlyList<ChatResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.Yield();
        }
    }
}
