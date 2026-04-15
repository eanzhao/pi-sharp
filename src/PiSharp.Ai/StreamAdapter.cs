using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.Ai;

public static class StreamAdapter
{
    private static readonly JsonSerializerOptions ToolCallArgumentsJsonOptions = new()
    {
        WriteIndented = false,
    };

    public static IAsyncEnumerable<AssistantMessageEvent> ToEvents(
        IChatClient chatClient,
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(chatClient);
        ArgumentNullException.ThrowIfNull(messages);

        return ToEvents(chatClient.GetStreamingResponseAsync(messages, options, cancellationToken), cancellationToken);
    }

    public static async IAsyncEnumerable<AssistantMessageEvent> ToEvents(
        IAsyncEnumerable<ChatResponseUpdate> updates,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updates);

        var state = new StreamAdapterState();
        Exception? capturedException = null;
        await using var enumerator = updates.GetAsyncEnumerator(cancellationToken);

        while (true)
        {
            ChatResponseUpdate update;

            try
            {
                if (!await enumerator.MoveNextAsync())
                {
                    break;
                }

                update = enumerator.Current;
            }
            catch (Exception exception)
            {
                capturedException = exception;
                break;
            }

            foreach (var @event in state.Process(update))
            {
                yield return @event;
            }
        }

        if (capturedException is not null)
        {
            foreach (var @event in state.Fail(capturedException))
            {
                yield return @event;
            }

            yield break;
        }

        foreach (var @event in state.Complete())
        {
            yield return @event;
        }
    }

    private sealed class StreamAdapterState
    {
        private readonly StringBuilder _buffer = new();
        private readonly ExtendedUsageDetails _usage = new();

        private ActiveBlockKind _activeBlockKind;
        private string? _activeToolCallId;
        private ChatFinishReason? _finishReason;
        private bool _hasUsage;

        public IEnumerable<AssistantMessageEvent> Process(ChatResponseUpdate update)
        {
            if (update.FinishReason is not null)
            {
                _finishReason = update.FinishReason;
            }

            foreach (var content in update.Contents)
            {
                switch (content)
                {
                    case TextContent textContent:
                        foreach (var @event in SwitchToText())
                        {
                            yield return @event;
                        }

                        if (!string.IsNullOrEmpty(textContent.Text))
                        {
                            _buffer.Append(textContent.Text);
                            yield return new AssistantMessageEvent.TextDelta(textContent.Text);
                        }

                        break;

                    case TextReasoningContent reasoningContent:
                        foreach (var @event in SwitchToThinking())
                        {
                            yield return @event;
                        }

                        if (!string.IsNullOrEmpty(reasoningContent.Text))
                        {
                            _buffer.Append(reasoningContent.Text);
                            yield return new AssistantMessageEvent.ThinkingDelta(reasoningContent.Text);
                        }

                        break;

                    case FunctionCallContent functionCallContent:
                        foreach (var @event in SwitchToToolCall(functionCallContent))
                        {
                            yield return @event;
                        }

                        var currentArgumentsJson = SerializeArguments(functionCallContent.Arguments ?? new Dictionary<string, object?>());
                        var previousArgumentsJson = _buffer.ToString();
                        var delta = ComputeDelta(previousArgumentsJson, currentArgumentsJson);

                        _buffer.Clear();
                        _buffer.Append(currentArgumentsJson);

                        if (!string.IsNullOrEmpty(delta))
                        {
                            yield return new AssistantMessageEvent.ToolCallDelta(functionCallContent.CallId, delta);
                        }

                        break;

                    case UsageContent usageContent:
                        _usage.AddUsage(usageContent.Details);
                        _hasUsage = true;
                        break;
                }
            }
        }

        public IEnumerable<AssistantMessageEvent> Complete()
        {
            foreach (var @event in CloseActiveBlock())
            {
                yield return @event;
            }

            yield return new AssistantMessageEvent.Done(_finishReason, _hasUsage ? _usage : null);
        }

        public IEnumerable<AssistantMessageEvent> Fail(Exception exception)
        {
            foreach (var @event in CloseActiveBlock())
            {
                yield return @event;
            }

            yield return new AssistantMessageEvent.Error(exception);
        }

        private IEnumerable<AssistantMessageEvent> SwitchToText()
        {
            if (_activeBlockKind == ActiveBlockKind.Text)
            {
                yield break;
            }

            foreach (var @event in CloseActiveBlock())
            {
                yield return @event;
            }

            _activeBlockKind = ActiveBlockKind.Text;
            _buffer.Clear();

            yield return new AssistantMessageEvent.TextStart();
        }

        private IEnumerable<AssistantMessageEvent> SwitchToThinking()
        {
            if (_activeBlockKind == ActiveBlockKind.Thinking)
            {
                yield break;
            }

            foreach (var @event in CloseActiveBlock())
            {
                yield return @event;
            }

            _activeBlockKind = ActiveBlockKind.Thinking;
            _buffer.Clear();

            yield return new AssistantMessageEvent.ThinkingStart();
        }

        private IEnumerable<AssistantMessageEvent> SwitchToToolCall(FunctionCallContent functionCallContent)
        {
            if (_activeBlockKind == ActiveBlockKind.ToolCall && _activeToolCallId == functionCallContent.CallId)
            {
                yield break;
            }

            foreach (var @event in CloseActiveBlock())
            {
                yield return @event;
            }

            _activeBlockKind = ActiveBlockKind.ToolCall;
            _activeToolCallId = functionCallContent.CallId;
            _buffer.Clear();

            yield return new AssistantMessageEvent.ToolCallStart(functionCallContent.CallId, functionCallContent.Name);
        }

        private IEnumerable<AssistantMessageEvent> CloseActiveBlock()
        {
            switch (_activeBlockKind)
            {
                case ActiveBlockKind.Text:
                    yield return new AssistantMessageEvent.TextEnd();
                    break;

                case ActiveBlockKind.Thinking:
                    yield return new AssistantMessageEvent.ThinkingEnd();
                    break;

                case ActiveBlockKind.ToolCall when _activeToolCallId is not null:
                    yield return new AssistantMessageEvent.ToolCallEnd(_activeToolCallId);
                    break;
            }

            _activeBlockKind = ActiveBlockKind.None;
            _activeToolCallId = null;
            _buffer.Clear();
        }
    }

    private static string SerializeArguments(IDictionary<string, object?> arguments)
    {
        if (arguments.Count == 0)
        {
            return "{}";
        }

        return JsonSerializer.Serialize(arguments, ToolCallArgumentsJsonOptions);
    }

    private static string ComputeDelta(string previousValue, string currentValue)
    {
        if (string.IsNullOrEmpty(previousValue))
        {
            return currentValue;
        }

        if (previousValue == currentValue)
        {
            return string.Empty;
        }

        if (previousValue.Length > 1 &&
            previousValue[^1] == '}' &&
            currentValue.StartsWith(previousValue[..^1], StringComparison.Ordinal))
        {
            return currentValue[(previousValue.Length - 1)..];
        }

        if (currentValue.StartsWith(previousValue, StringComparison.Ordinal))
        {
            return currentValue[previousValue.Length..];
        }

        return currentValue;
    }

    private enum ActiveBlockKind
    {
        None,
        Text,
        Thinking,
        ToolCall,
    }
}
