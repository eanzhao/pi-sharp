using System.Net;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;

namespace PiSharp.Agent;

public sealed class RetrySettings
{
    public int MaxRetries { get; init; } = 3;

    public int BaseDelayMs { get; init; } = 250;

    public int MaxDelayMs { get; init; } = 4_000;
}

public sealed class RetryMiddleware : DelegatingChatClient
{
    private static readonly HashSet<int> RetriableStatusCodes = [429, 500, 502, 503];

    private readonly RetrySettings _settings;
    private readonly TimeProvider _timeProvider;
    private readonly Random _random;

    public RetryMiddleware(
        IChatClient innerClient,
        RetrySettings? settings = null,
        TimeProvider? timeProvider = null,
        Random? random = null)
        : base(innerClient)
    {
        _settings = settings ?? new RetrySettings();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _random = random ?? Random.Shared;
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var snapshot = SnapshotMessages(messages);

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await base.GetResponseAsync(CloneMessages(snapshot), options?.Clone(), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (ShouldRetry(exception, attempt, cancellationToken))
            {
                await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public override async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var snapshot = SnapshotMessages(messages);

        for (var attempt = 0; ; attempt++)
        {
            var shouldRetry = false;
            var updates = base.GetStreamingResponseAsync(CloneMessages(snapshot), options?.Clone(), cancellationToken);

            await using var enumerator = updates.GetAsyncEnumerator(cancellationToken);
            var emittedAny = false;

            while (true)
            {
                bool hasNext;

                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (!emittedAny && ShouldRetry(exception, attempt, cancellationToken))
                {
                    shouldRetry = true;
                    break;
                }

                if (!hasNext)
                {
                    yield break;
                }

                emittedAny = true;
                yield return enumerator.Current;
            }

            if (!shouldRetry)
            {
                yield break;
            }

            await DelayAsync(attempt, cancellationToken).ConfigureAwait(false);
        }
    }

    private bool ShouldRetry(Exception exception, int attempt, CancellationToken cancellationToken)
    {
        if (attempt >= _settings.MaxRetries || cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        if (exception is OperationCanceledException)
        {
            return false;
        }

        return TryGetStatusCode(exception, out var statusCode) &&
            RetriableStatusCodes.Contains(statusCode);
    }

    private async Task DelayAsync(int attempt, CancellationToken cancellationToken)
    {
        if (_settings.BaseDelayMs <= 0 || _settings.MaxDelayMs <= 0)
        {
            return;
        }

        var exponent = Math.Min(attempt, 10);
        var exponentialDelay = _settings.BaseDelayMs * Math.Pow(2, exponent);
        var cappedDelay = Math.Min(exponentialDelay, _settings.MaxDelayMs);
        var jitteredDelay = cappedDelay * (0.5d + _random.NextDouble() * 0.5d);
        var delay = TimeSpan.FromMilliseconds(Math.Min(jitteredDelay, _settings.MaxDelayMs));

        if (delay > TimeSpan.Zero)
        {
            await Task.Delay(delay, _timeProvider, cancellationToken).ConfigureAwait(false);
        }
    }

    private static ChatMessage[] SnapshotMessages(IEnumerable<ChatMessage> messages) =>
        messages.Select(MessageUtilities.Clone).ToArray();

    private static IEnumerable<ChatMessage> CloneMessages(IEnumerable<ChatMessage> messages) =>
        messages.Select(MessageUtilities.Clone).ToArray();

    private static bool TryGetStatusCode(Exception exception, out int statusCode)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is HttpRequestException { StatusCode: HttpStatusCode httpStatusCode })
            {
                statusCode = (int)httpStatusCode;
                return true;
            }

            if (TryReadStatusCodeProperty(current, "StatusCode", out statusCode) ||
                TryReadStatusCodeProperty(current, "Status", out statusCode) ||
                TryReadStatusCodeProperty(current, "ResponseStatus", out statusCode))
            {
                return true;
            }
        }

        statusCode = 0;
        return false;
    }

    private static bool TryReadStatusCodeProperty(Exception exception, string propertyName, out int statusCode)
    {
        var property = exception.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
        var value = property?.GetValue(exception);

        statusCode = value switch
        {
            int intValue => intValue,
            HttpStatusCode httpStatusCode => (int)httpStatusCode,
            _ => 0,
        };

        return statusCode != 0;
    }
}
