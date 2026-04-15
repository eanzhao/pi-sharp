using Microsoft.Extensions.AI;
using PiSharp.Ai;

namespace PiSharp.Agent;

public sealed class AgentOptions
{
    public ModelMetadata Model { get; init; } = AgentDefaults.UnknownModel;

    public string SystemPrompt { get; init; } = string.Empty;

    public IReadOnlyList<AgentTool>? Tools { get; init; }

    public IReadOnlyList<ChatMessage>? Messages { get; init; }

    public ThinkingLevel ThinkingLevel { get; init; } = ThinkingLevel.Off;

    public ChatOptions? ChatOptions { get; init; }

    public AgentMessageTransform? ConvertToLlm { get; init; }

    public AgentMessageTransform? TransformContext { get; init; }

    public BeforeToolCallCallback? BeforeToolCall { get; init; }

    public AfterToolCallCallback? AfterToolCall { get; init; }

    public PendingMessageQueueMode SteeringMode { get; init; } = PendingMessageQueueMode.OneAtATime;

    public PendingMessageQueueMode FollowUpMode { get; init; } = PendingMessageQueueMode.OneAtATime;

    public ToolExecutionMode ToolExecution { get; init; } = ToolExecutionMode.Parallel;
}

public sealed class Agent
{
    private readonly AgentState _state;
    private readonly IChatClient _chatClient;
    private readonly List<AgentEventHandler> _listeners = [];
    private readonly PendingMessageQueue _steeringQueue;
    private readonly PendingMessageQueue _followUpQueue;

    private ActiveRun? _activeRun;

    public Agent(
        IChatClient chatClient,
        AgentOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(chatClient);

        options ??= new AgentOptions();

        _chatClient = chatClient;
        _state = new AgentState
        {
            SystemPrompt = options.SystemPrompt,
            Model = options.Model,
            ThinkingLevel = options.ThinkingLevel,
            Tools = options.Tools ?? Array.Empty<AgentTool>(),
            Messages = options.Messages ?? Array.Empty<ChatMessage>(),
        };

        ChatOptions = options.ChatOptions;
        ConvertToLlm = options.ConvertToLlm;
        TransformContext = options.TransformContext;
        BeforeToolCall = options.BeforeToolCall;
        AfterToolCall = options.AfterToolCall;
        ToolExecution = options.ToolExecution;

        _steeringQueue = new PendingMessageQueue(options.SteeringMode);
        _followUpQueue = new PendingMessageQueue(options.FollowUpMode);
    }

    public AgentMessageTransform? ConvertToLlm { get; set; }

    public AgentMessageTransform? TransformContext { get; set; }

    public BeforeToolCallCallback? BeforeToolCall { get; set; }

    public AfterToolCallCallback? AfterToolCall { get; set; }

    public ChatOptions? ChatOptions { get; set; }

    public ToolExecutionMode ToolExecution { get; set; } = ToolExecutionMode.Parallel;

    public AgentState State => _state;

    public PendingMessageQueueMode SteeringMode
    {
        get => _steeringQueue.Mode;
        set => _steeringQueue.Mode = value;
    }

    public PendingMessageQueueMode FollowUpMode
    {
        get => _followUpQueue.Mode;
        set => _followUpQueue.Mode = value;
    }

    public Action Subscribe(AgentEventHandler listener)
    {
        ArgumentNullException.ThrowIfNull(listener);

        _listeners.Add(listener);
        return () => _listeners.Remove(listener);
    }

    public void Steer(ChatMessage message) => _steeringQueue.Enqueue(message);

    public void FollowUp(ChatMessage message) => _followUpQueue.Enqueue(message);

    public void ClearSteeringQueue() => _steeringQueue.Clear();

    public void ClearFollowUpQueue() => _followUpQueue.Clear();

    public void ClearAllQueues()
    {
        ClearSteeringQueue();
        ClearFollowUpQueue();
    }

    public bool HasQueuedMessages() => _steeringQueue.HasItems || _followUpQueue.HasItems;

    public CancellationToken? CurrentCancellationToken => _activeRun?.CancellationTokenSource.Token;

    public void Abort() => _activeRun?.CancellationTokenSource.Cancel();

    public Task WaitForIdleAsync() => _activeRun?.Completion.Task ?? Task.CompletedTask;

    public void Reset()
    {
        _state.Messages = Array.Empty<ChatMessage>();
        _state.IsStreaming = false;
        _state.StreamingMessage = null;
        _state.PendingToolCalls = new HashSet<string>(StringComparer.Ordinal);
        _state.ErrorMessage = null;
        ClearAllQueues();
    }

    public Task PromptAsync(string text, IEnumerable<AIContent>? additionalContent = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var contents = new List<AIContent> { new TextContent(text) };
        if (additionalContent is not null)
        {
            contents.AddRange(additionalContent);
        }

        return PromptAsync(new ChatMessage(ChatRole.User, contents), cancellationToken);
    }

    public Task PromptAsync(ChatMessage message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        return PromptAsync([message], cancellationToken);
    }

    public Task PromptAsync(IEnumerable<ChatMessage> messages, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);

        if (_activeRun is not null)
        {
            throw new InvalidOperationException("Agent is already processing a prompt.");
        }

        return RunPromptMessagesAsync(messages.Select(MessageUtilities.Clone).ToArray(), false, cancellationToken);
    }

    public async Task ContinueAsync(CancellationToken cancellationToken = default)
    {
        if (_activeRun is not null)
        {
            throw new InvalidOperationException("Agent is already processing.");
        }

        var lastMessage = _state.Messages.LastOrDefault();
        if (lastMessage is null)
        {
            throw new InvalidOperationException("No messages to continue from.");
        }

        if (lastMessage.Role == ChatRole.Assistant)
        {
            var queuedSteeringMessages = _steeringQueue.Drain();
            if (queuedSteeringMessages.Count > 0)
            {
                await RunPromptMessagesAsync(queuedSteeringMessages, true, cancellationToken).ConfigureAwait(false);
                return;
            }

            var queuedFollowUpMessages = _followUpQueue.Drain();
            if (queuedFollowUpMessages.Count > 0)
            {
                await RunPromptMessagesAsync(queuedFollowUpMessages, false, cancellationToken).ConfigureAwait(false);
                return;
            }

            throw new InvalidOperationException("Cannot continue from an assistant message.");
        }

        await RunContinuationAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task RunPromptMessagesAsync(
        IReadOnlyList<ChatMessage> messages,
        bool skipInitialSteeringPoll,
        CancellationToken cancellationToken)
    {
        await RunWithLifecycleAsync(
            async linkedCancellationToken =>
            {
                await AgentLoop.RunAsync(
                    messages,
                    CreateContextSnapshot(),
                    CreateLoopOptions(skipInitialSteeringPoll),
                    ProcessEventAsync,
                    linkedCancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RunContinuationAsync(CancellationToken cancellationToken)
    {
        await RunWithLifecycleAsync(
            async linkedCancellationToken =>
            {
                await AgentLoop.ContinueAsync(
                    CreateContextSnapshot(),
                    CreateLoopOptions(false),
                    ProcessEventAsync,
                    linkedCancellationToken).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task RunWithLifecycleAsync(
        Func<CancellationToken, Task> executor,
        CancellationToken cancellationToken)
    {
        if (_activeRun is not null)
        {
            throw new InvalidOperationException("Agent is already processing.");
        }

        using var linkedCancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _activeRun = new ActiveRun(linkedCancellationTokenSource, completion);

        _state.IsStreaming = true;
        _state.StreamingMessage = null;
        _state.ErrorMessage = null;

        try
        {
            await executor(linkedCancellationTokenSource.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await HandleRunFailureAsync(exception, linkedCancellationTokenSource.Token).ConfigureAwait(false);
        }
        finally
        {
            FinishRun();
        }
    }

    private async Task HandleRunFailureAsync(Exception exception, CancellationToken cancellationToken)
    {
        var failureMessage = AgentMessageMetadata.WithAssistantMetadata(
            AgentMessageMetadata.CreateAssistantMessage(_state.Model),
            _state.Model,
            cancellationToken.IsCancellationRequested ? AgentMessageMetadata.Aborted : AgentMessageMetadata.Error,
            errorMessage: exception.Message);

        await ProcessEventAsync(new AgentEvent.MessageStarted(failureMessage), cancellationToken).ConfigureAwait(false);
        await ProcessEventAsync(new AgentEvent.MessageCompleted(failureMessage), cancellationToken).ConfigureAwait(false);
        await ProcessEventAsync(
            new AgentEvent.TurnCompleted(failureMessage, Array.Empty<ChatMessage>()),
            cancellationToken).ConfigureAwait(false);
        await ProcessEventAsync(
            new AgentEvent.AgentCompleted([failureMessage]),
            cancellationToken).ConfigureAwait(false);
    }

    private void FinishRun()
    {
        _state.IsStreaming = false;
        _state.StreamingMessage = null;
        _state.PendingToolCalls = new HashSet<string>(StringComparer.Ordinal);

        _activeRun?.Completion.SetResult();
        _activeRun = null;
    }

    private async ValueTask ProcessEventAsync(AgentEvent @event, CancellationToken cancellationToken)
    {
        switch (@event)
        {
            case AgentEvent.MessageStarted(var message) when message.Role == ChatRole.Assistant:
                _state.StreamingMessage = message;
                break;

            case AgentEvent.MessageUpdated(var message, _):
                _state.StreamingMessage = message;
                break;

            case AgentEvent.MessageCompleted(var message):
                _state.StreamingMessage = null;
                _state.Messages = [.. _state.Messages, message];
                break;

            case AgentEvent.ToolExecutionStarted(var toolCallId, _, _):
            {
                var pendingToolCalls = new HashSet<string>(_state.PendingToolCalls, StringComparer.Ordinal)
                {
                    toolCallId,
                };
                _state.PendingToolCalls = pendingToolCalls;
                break;
            }

            case AgentEvent.ToolExecutionCompleted(var toolCallId, _, _, _):
            {
                var pendingToolCalls = new HashSet<string>(_state.PendingToolCalls, StringComparer.Ordinal);
                pendingToolCalls.Remove(toolCallId);
                _state.PendingToolCalls = pendingToolCalls;
                break;
            }

            case AgentEvent.TurnCompleted(var message, _):
                _state.ErrorMessage = AgentMessageMetadata.GetErrorMessage(message);
                break;

            case AgentEvent.AgentCompleted:
                _state.StreamingMessage = null;
                break;
        }

        foreach (var listener in _listeners.ToArray())
        {
            await listener(@event, cancellationToken).ConfigureAwait(false);
        }
    }

    private AgentContext CreateContextSnapshot() =>
        new(
            _state.SystemPrompt,
            _state.Messages.Select(MessageUtilities.Clone).ToArray(),
            _state.Tools.ToArray());

    private AgentLoopOptions CreateLoopOptions(bool skipInitialSteeringPoll)
    {
        var skipInitialPoll = skipInitialSteeringPoll;

        return new AgentLoopOptions
        {
            ChatClient = _chatClient,
            Model = _state.Model,
            ChatOptions = ChatOptions,
            ConvertToLlm = ConvertToLlm,
            TransformContext = TransformContext,
            BeforeToolCall = BeforeToolCall,
            AfterToolCall = AfterToolCall,
            ToolExecution = ToolExecution,
            ThinkingLevel = _state.ThinkingLevel,
            GetSteeringMessages = _ =>
            {
                if (skipInitialPoll)
                {
                    skipInitialPoll = false;
                    return ValueTask.FromResult<IReadOnlyList<ChatMessage>>(Array.Empty<ChatMessage>());
                }

                return ValueTask.FromResult(_steeringQueue.Drain());
            },
            GetFollowUpMessages = _ => ValueTask.FromResult(_followUpQueue.Drain()),
        };
    }

    private sealed record ActiveRun(
        CancellationTokenSource CancellationTokenSource,
        TaskCompletionSource Completion);

    private sealed class PendingMessageQueue
    {
        private readonly List<ChatMessage> _messages = [];

        public PendingMessageQueue(PendingMessageQueueMode mode)
        {
            Mode = mode;
        }

        public PendingMessageQueueMode Mode { get; set; }

        public bool HasItems => _messages.Count > 0;

        public void Enqueue(ChatMessage message) => _messages.Add(MessageUtilities.Clone(message));

        public IReadOnlyList<ChatMessage> Drain()
        {
            if (_messages.Count == 0)
            {
                return Array.Empty<ChatMessage>();
            }

            if (Mode == PendingMessageQueueMode.All)
            {
                var drainedMessages = _messages.ToArray();
                _messages.Clear();
                return drainedMessages;
            }

            var firstMessage = _messages[0];
            _messages.RemoveAt(0);
            return [firstMessage];
        }

        public void Clear() => _messages.Clear();
    }
}
