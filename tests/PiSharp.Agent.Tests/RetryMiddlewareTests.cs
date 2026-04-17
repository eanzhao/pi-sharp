using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Agent.Tests.Support;

namespace PiSharp.Agent.Tests;

public sealed class RetryMiddlewareTests
{
    [Fact]
    public async Task GetResponseAsync_RetriesRetriableFailuresThenSucceeds()
    {
        var client = new RetryingFakeChatClient(
            getResponse: attempt =>
                attempt < 3
                    ? throw new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests)
                    : new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        var middleware = new RetryMiddleware(
            client,
            new RetrySettings
            {
                MaxRetries = 3,
                BaseDelayMs = 0,
                MaxDelayMs = 0,
            });

        var response = await middleware.GetResponseAsync([new ChatMessage(ChatRole.User, "hello")]);

        Assert.Equal(3, client.GetResponseAttempts);
        Assert.Equal("ok", response.Messages.Last().Text);
    }

    [Fact]
    public async Task GetStreamingResponseAsync_RetriesBeforeFirstChunk()
    {
        var client = new RetryingFakeChatClient(
            getStreamingResponse: attempt =>
                attempt < 2
                    ? ThrowStreaming(new HttpRequestException("unavailable", null, HttpStatusCode.ServiceUnavailable))
                    : YieldUpdates(
                    [
                        new ChatResponseUpdate(ChatRole.Assistant, [new TextContent("done")])
                        {
                            FinishReason = ChatFinishReason.Stop,
                        },
                    ]));
        var middleware = new RetryMiddleware(
            client,
            new RetrySettings
            {
                MaxRetries = 2,
                BaseDelayMs = 0,
                MaxDelayMs = 0,
            });

        var updates = new List<ChatResponseUpdate>();
        await foreach (var update in middleware.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "hello")]))
        {
            updates.Add(update);
        }

        Assert.Equal(2, client.GetStreamingAttempts);
        Assert.Single(updates);
        Assert.Equal("done", Assert.IsType<TextContent>(updates[0].Contents[0]).Text);
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> ThrowStreaming(
        Exception exception,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.Yield();
        throw exception;
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> YieldUpdates(
        IReadOnlyList<ChatResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var update in updates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return update;
            await Task.Yield();
        }
    }

    private sealed class RetryingFakeChatClient(
        Func<int, ChatResponse>? getResponse = null,
        Func<int, IAsyncEnumerable<ChatResponseUpdate>>? getStreamingResponse = null) : IChatClient
    {
        public int GetResponseAttempts { get; private set; }

        public int GetStreamingAttempts { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            GetResponseAttempts++;
            return Task.FromResult(getResponse?.Invoke(GetResponseAttempts) ?? new ChatResponse(new ChatMessage(ChatRole.Assistant, "ok")));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            GetStreamingAttempts++;
            return getStreamingResponse?.Invoke(GetStreamingAttempts) ?? YieldUpdates([]);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
