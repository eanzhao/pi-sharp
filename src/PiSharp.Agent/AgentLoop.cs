using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using PiSharp.Ai;

namespace PiSharp.Agent;

public static class AgentLoop
{
    public static async Task<IReadOnlyList<ChatMessage>> RunAsync(
        IEnumerable<ChatMessage> prompts,
        AgentContext context,
        AgentLoopOptions options,
        AgentEventHandler emitAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(prompts);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(emitAsync);

        var promptMessages = prompts.Select(MessageUtilities.Clone).ToArray();
        var newMessages = new List<ChatMessage>(promptMessages.Length);
        var currentContext = MutableAgentContext.FromSnapshot(context);

        await emitAsync(new AgentEvent.AgentStarted(), cancellationToken).ConfigureAwait(false);
        await emitAsync(new AgentEvent.TurnStarted(), cancellationToken).ConfigureAwait(false);

        foreach (var prompt in promptMessages)
        {
            currentContext.Messages.Add(prompt);
            newMessages.Add(prompt);
            await emitAsync(new AgentEvent.MessageStarted(prompt), cancellationToken).ConfigureAwait(false);
            await emitAsync(new AgentEvent.MessageCompleted(prompt), cancellationToken).ConfigureAwait(false);
        }

        await RunLoopAsync(currentContext, newMessages, options, emitAsync, cancellationToken).ConfigureAwait(false);
        return newMessages;
    }

    public static async Task<IReadOnlyList<ChatMessage>> ContinueAsync(
        AgentContext context,
        AgentLoopOptions options,
        AgentEventHandler emitAsync,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(emitAsync);

        if (context.Messages.Count == 0)
        {
            throw new InvalidOperationException("Cannot continue without existing messages.");
        }

        if (context.Messages[^1].Role == ChatRole.Assistant)
        {
            throw new InvalidOperationException("Cannot continue from an assistant message.");
        }

        var newMessages = new List<ChatMessage>();
        var currentContext = MutableAgentContext.FromSnapshot(context);

        await emitAsync(new AgentEvent.AgentStarted(), cancellationToken).ConfigureAwait(false);
        await emitAsync(new AgentEvent.TurnStarted(), cancellationToken).ConfigureAwait(false);

        await RunLoopAsync(currentContext, newMessages, options, emitAsync, cancellationToken).ConfigureAwait(false);
        return newMessages;
    }

    private static async Task RunLoopAsync(
        MutableAgentContext currentContext,
        List<ChatMessage> newMessages,
        AgentLoopOptions options,
        AgentEventHandler emitAsync,
        CancellationToken cancellationToken)
    {
        var firstTurn = true;
        var pendingMessages = await GetPendingMessagesAsync(options.GetSteeringMessages, cancellationToken).ConfigureAwait(false);

        while (true)
        {
            var hasMoreToolCalls = true;

            while (hasMoreToolCalls || pendingMessages.Count > 0)
            {
                if (firstTurn)
                {
                    firstTurn = false;
                }
                else
                {
                    await emitAsync(new AgentEvent.TurnStarted(), cancellationToken).ConfigureAwait(false);
                }

                if (pendingMessages.Count > 0)
                {
                    foreach (var pendingMessage in pendingMessages)
                    {
                        var message = MessageUtilities.Clone(pendingMessage);
                        currentContext.Messages.Add(message);
                        newMessages.Add(message);

                        await emitAsync(new AgentEvent.MessageStarted(message), cancellationToken).ConfigureAwait(false);
                        await emitAsync(new AgentEvent.MessageCompleted(message), cancellationToken).ConfigureAwait(false);
                    }

                    pendingMessages = Array.Empty<ChatMessage>();
                }

                var assistantMessage = await StreamAssistantResponseAsync(
                    currentContext,
                    options,
                    emitAsync,
                    cancellationToken).ConfigureAwait(false);

                newMessages.Add(assistantMessage);

                if (AgentMessageMetadata.TryGetFinishReason(assistantMessage, out var finishReason) &&
                    (finishReason == AgentMessageMetadata.Error || finishReason == AgentMessageMetadata.Aborted))
                {
                    await emitAsync(
                        new AgentEvent.TurnCompleted(assistantMessage, Array.Empty<ChatMessage>()),
                        cancellationToken).ConfigureAwait(false);
                    await emitAsync(
                        new AgentEvent.AgentCompleted(newMessages.ToArray()),
                        cancellationToken).ConfigureAwait(false);
                    return;
                }

                var toolCalls = assistantMessage.Contents
                    .OfType<FunctionCallContent>()
                    .Where(static toolCall => !toolCall.InformationalOnly)
                    .ToArray();

                hasMoreToolCalls = toolCalls.Length > 0;
                var toolResults = Array.Empty<ChatMessage>();

                if (hasMoreToolCalls)
                {
                    toolResults = await ExecuteToolCallsAsync(
                        currentContext,
                        assistantMessage,
                        toolCalls,
                        options,
                        emitAsync,
                        cancellationToken).ConfigureAwait(false);

                    foreach (var toolResult in toolResults)
                    {
                        currentContext.Messages.Add(toolResult);
                        newMessages.Add(toolResult);
                    }
                }

                await emitAsync(
                    new AgentEvent.TurnCompleted(assistantMessage, toolResults),
                    cancellationToken).ConfigureAwait(false);

                pendingMessages = await GetPendingMessagesAsync(options.GetSteeringMessages, cancellationToken).ConfigureAwait(false);
            }

            var followUpMessages = await GetPendingMessagesAsync(options.GetFollowUpMessages, cancellationToken).ConfigureAwait(false);
            if (followUpMessages.Count > 0)
            {
                pendingMessages = followUpMessages;
                continue;
            }

            break;
        }

        await emitAsync(new AgentEvent.AgentCompleted(newMessages.ToArray()), cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ChatMessage> StreamAssistantResponseAsync(
        MutableAgentContext currentContext,
        AgentLoopOptions options,
        AgentEventHandler emitAsync,
        CancellationToken cancellationToken)
    {
        var requestMessages = currentContext.Messages.ToArray();
        if (options.TransformContext is not null)
        {
            requestMessages = (await options.TransformContext(requestMessages, cancellationToken).ConfigureAwait(false)).ToArray();
        }

        if (options.ConvertToLlm is not null)
        {
            requestMessages = (await options.ConvertToLlm(requestMessages, cancellationToken).ConfigureAwait(false)).ToArray();
        }

        var chatOptions = CreateChatOptions(currentContext, options);
        var updates = options.ChatClient.GetStreamingResponseAsync(requestMessages, chatOptions, cancellationToken);
        var bufferedUpdates = new List<ChatResponseUpdate>();

        ChatMessage? partialMessage = null;
        var started = false;

        await foreach (var assistantMessageEvent in StreamAdapter.ToEvents(
            BufferUpdatesAsync(updates, bufferedUpdates, cancellationToken),
            cancellationToken))
        {
            switch (assistantMessageEvent)
            {
                case AssistantMessageEvent.Done done:
                {
                    var finalMessage = BuildFinalAssistantMessage(
                        bufferedUpdates,
                        partialMessage,
                        options.Model,
                        done.FinishReason,
                        done.Usage);

                    if (!started)
                    {
                        started = true;
                        await emitAsync(new AgentEvent.MessageStarted(finalMessage), cancellationToken).ConfigureAwait(false);
                    }

                    await emitAsync(new AgentEvent.MessageCompleted(finalMessage), cancellationToken).ConfigureAwait(false);
                    currentContext.Messages.Add(finalMessage);
                    return finalMessage;
                }

                case AssistantMessageEvent.Error error:
                {
                    var failureMessage = BuildFailureAssistantMessage(
                        partialMessage,
                        options.Model,
                        error.Exception,
                        cancellationToken.IsCancellationRequested);

                    if (!started)
                    {
                        started = true;
                        await emitAsync(new AgentEvent.MessageStarted(failureMessage), cancellationToken).ConfigureAwait(false);
                    }

                    await emitAsync(new AgentEvent.MessageCompleted(failureMessage), cancellationToken).ConfigureAwait(false);
                    currentContext.Messages.Add(failureMessage);
                    return failureMessage;
                }

                default:
                    partialMessage = MessageUtilities.ApplyAssistantEvent(partialMessage, assistantMessageEvent, options.Model);

                    if (!started)
                    {
                        started = true;
                        await emitAsync(new AgentEvent.MessageStarted(partialMessage), cancellationToken).ConfigureAwait(false);
                    }

                    await emitAsync(
                        new AgentEvent.MessageUpdated(partialMessage, assistantMessageEvent),
                        cancellationToken).ConfigureAwait(false);
                    break;
            }
        }

        var completedMessage = BuildFinalAssistantMessage(bufferedUpdates, partialMessage, options.Model, null, null);
        if (!started)
        {
            await emitAsync(new AgentEvent.MessageStarted(completedMessage), cancellationToken).ConfigureAwait(false);
        }

        await emitAsync(new AgentEvent.MessageCompleted(completedMessage), cancellationToken).ConfigureAwait(false);
        currentContext.Messages.Add(completedMessage);
        return completedMessage;
    }

    private static ChatMessage BuildFinalAssistantMessage(
        IReadOnlyList<ChatResponseUpdate> bufferedUpdates,
        ChatMessage? partialMessage,
        ModelMetadata model,
        ChatFinishReason? finishReason,
        UsageDetails? usage)
    {
        var response = bufferedUpdates.Count > 0
            ? ChatResponseExtensions.ToChatResponse(bufferedUpdates)
            : new ChatResponse(partialMessage ?? AgentMessageMetadata.CreateAssistantMessage(model));

        var assistantMessage = response.Messages.LastOrDefault(static message => message.Role == ChatRole.Assistant)
            ?? partialMessage
            ?? AgentMessageMetadata.CreateAssistantMessage(model);

        if (response.CreatedAt is not null)
        {
            assistantMessage = MessageUtilities.Clone(assistantMessage);
            assistantMessage.CreatedAt = response.CreatedAt;
        }

        var pricedUsage = response.Usage ?? usage;
        if (pricedUsage is not null)
        {
            var extendedUsage = pricedUsage as ExtendedUsageDetails ?? ExtendedUsageDetails.FromUsage(pricedUsage);
            extendedUsage.ApplyPricing(model.Pricing);
            pricedUsage = extendedUsage;
        }

        return AgentMessageMetadata.WithAssistantMetadata(
            assistantMessage,
            model,
            response.FinishReason ?? finishReason,
            pricedUsage);
    }

    private static ChatMessage BuildFailureAssistantMessage(
        ChatMessage? partialMessage,
        ModelMetadata model,
        Exception exception,
        bool aborted)
    {
        var message = partialMessage ?? AgentMessageMetadata.CreateAssistantMessage(model);
        return AgentMessageMetadata.WithAssistantMetadata(
            message,
            model,
            aborted ? AgentMessageMetadata.Aborted : AgentMessageMetadata.Error,
            errorMessage: exception.Message);
    }

    private static ChatOptions CreateChatOptions(MutableAgentContext currentContext, AgentLoopOptions options)
    {
        var chatOptions = options.ChatOptions?.Clone() ?? new ChatOptions();

        chatOptions.ModelId = options.Model.Id;
        chatOptions.AllowMultipleToolCalls = true;
        chatOptions.Instructions = currentContext.SystemPrompt;

        if (options.ThinkingLevel != ThinkingLevel.Off)
        {
            chatOptions.Reasoning = options.ThinkingLevel.ToReasoningOptions();
        }

        if (currentContext.Tools.Count > 0)
        {
            chatOptions.Tools = currentContext.Tools.Select(static tool => (AITool)tool.Function).ToList();
        }

        return chatOptions;
    }

    private static async Task<ChatMessage[]> ExecuteToolCallsAsync(
        MutableAgentContext currentContext,
        ChatMessage assistantMessage,
        IReadOnlyList<FunctionCallContent> toolCalls,
        AgentLoopOptions options,
        AgentEventHandler emitAsync,
        CancellationToken cancellationToken)
    {
        return options.ToolExecution == ToolExecutionMode.Sequential
            ? await ExecuteToolCallsSequentialAsync(currentContext, assistantMessage, toolCalls, options, emitAsync, cancellationToken).ConfigureAwait(false)
            : await ExecuteToolCallsParallelAsync(currentContext, assistantMessage, toolCalls, options, emitAsync, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ChatMessage[]> ExecuteToolCallsSequentialAsync(
        MutableAgentContext currentContext,
        ChatMessage assistantMessage,
        IReadOnlyList<FunctionCallContent> toolCalls,
        AgentLoopOptions options,
        AgentEventHandler emitAsync,
        CancellationToken cancellationToken)
    {
        var results = new List<ChatMessage>(toolCalls.Count);

        foreach (var toolCall in toolCalls)
        {
            await emitAsync(
                new AgentEvent.ToolExecutionStarted(
                    toolCall.CallId,
                    toolCall.Name,
                    CreateToolArguments(toolCall)),
                cancellationToken).ConfigureAwait(false);

            var preparation = await PrepareToolCallAsync(currentContext, assistantMessage, toolCall, options, cancellationToken).ConfigureAwait(false);
            if (preparation is ImmediateToolCallOutcome immediate)
            {
                results.Add(await EmitToolCallOutcomeAsync(toolCall, immediate.Result, immediate.IsError, emitAsync, cancellationToken).ConfigureAwait(false));
                continue;
            }

            var prepared = (PreparedToolCall)preparation;
            var executed = await ExecutePreparedToolCallAsync(prepared, emitAsync, cancellationToken).ConfigureAwait(false);
            results.Add(await FinalizeExecutedToolCallAsync(
                currentContext,
                assistantMessage,
                prepared,
                executed,
                options,
                emitAsync,
                cancellationToken).ConfigureAwait(false));
        }

        return results.ToArray();
    }

    private static async Task<ChatMessage[]> ExecuteToolCallsParallelAsync(
        MutableAgentContext currentContext,
        ChatMessage assistantMessage,
        IReadOnlyList<FunctionCallContent> toolCalls,
        AgentLoopOptions options,
        AgentEventHandler emitAsync,
        CancellationToken cancellationToken)
    {
        var results = new List<ChatMessage>(toolCalls.Count);
        var runnableCalls = new List<PreparedToolCall>(toolCalls.Count);

        foreach (var toolCall in toolCalls)
        {
            await emitAsync(
                new AgentEvent.ToolExecutionStarted(
                    toolCall.CallId,
                    toolCall.Name,
                    CreateToolArguments(toolCall)),
                cancellationToken).ConfigureAwait(false);

            var preparation = await PrepareToolCallAsync(currentContext, assistantMessage, toolCall, options, cancellationToken).ConfigureAwait(false);
            if (preparation is ImmediateToolCallOutcome immediate)
            {
                results.Add(await EmitToolCallOutcomeAsync(toolCall, immediate.Result, immediate.IsError, emitAsync, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                runnableCalls.Add((PreparedToolCall)preparation);
            }
        }

        var runningCalls = runnableCalls
            .Select(prepared => new RunningToolCall(
                prepared,
                ExecutePreparedToolCallAsync(prepared, emitAsync, cancellationToken)))
            .ToArray();

        foreach (var runningCall in runningCalls)
        {
            var executed = await runningCall.Execution.ConfigureAwait(false);
            results.Add(await FinalizeExecutedToolCallAsync(
                currentContext,
                assistantMessage,
                runningCall.Prepared,
                executed,
                options,
                emitAsync,
                cancellationToken).ConfigureAwait(false));
        }

        return results.ToArray();
    }

    private static async Task<ToolCallPreparation> PrepareToolCallAsync(
        MutableAgentContext currentContext,
        ChatMessage assistantMessage,
        FunctionCallContent toolCall,
        AgentLoopOptions options,
        CancellationToken cancellationToken)
    {
        var tool = currentContext.Tools.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, toolCall.Name, StringComparison.Ordinal));
        if (tool is null)
        {
            return new ImmediateToolCallOutcome(
                AgentToolResult.FromText($"Tool '{toolCall.Name}' not found."),
                true);
        }

        try
        {
            var arguments = CreateToolArguments(toolCall);
            if (tool.PrepareArguments is not null)
            {
                arguments = tool.PrepareArguments(MessageUtilities.CloneArguments(arguments));
            }

            if (options.BeforeToolCall is not null)
            {
                var beforeResult = await options.BeforeToolCall(
                    new BeforeToolCallContext(
                        assistantMessage,
                        toolCall,
                        MessageUtilities.CloneArguments(arguments),
                        currentContext.ToSnapshot()),
                    cancellationToken).ConfigureAwait(false);

                if (beforeResult?.Block == true)
                {
                    return new ImmediateToolCallOutcome(
                        AgentToolResult.FromText(beforeResult.Reason ?? "Tool execution was blocked."),
                        true);
                }
            }

            return new PreparedToolCall(toolCall, tool, arguments);
        }
        catch (Exception exception)
        {
            return new ImmediateToolCallOutcome(AgentToolResult.FromText(exception.Message), true);
        }
    }

    private static async Task<ExecutedToolCallOutcome> ExecutePreparedToolCallAsync(
        PreparedToolCall prepared,
        AgentEventHandler emitAsync,
        CancellationToken cancellationToken)
    {
        try
        {
            async ValueTask OnUpdate(AgentToolResult partialResult)
            {
                await emitAsync(
                    new AgentEvent.ToolExecutionUpdated(
                        prepared.ToolCall.CallId,
                        prepared.ToolCall.Name,
                        MessageUtilities.CloneArguments(prepared.Arguments),
                        partialResult),
                    cancellationToken).ConfigureAwait(false);
            }

            var result = await prepared.Tool.ExecuteAsync(
                prepared.ToolCall.CallId,
                MessageUtilities.CloneArguments(prepared.Arguments),
                OnUpdate,
                cancellationToken).ConfigureAwait(false);

            return new ExecutedToolCallOutcome(result, false);
        }
        catch (Exception exception)
        {
            return new ExecutedToolCallOutcome(AgentToolResult.FromText(exception.Message), true);
        }
    }

    private static async Task<ChatMessage> FinalizeExecutedToolCallAsync(
        MutableAgentContext currentContext,
        ChatMessage assistantMessage,
        PreparedToolCall prepared,
        ExecutedToolCallOutcome executed,
        AgentLoopOptions options,
        AgentEventHandler emitAsync,
        CancellationToken cancellationToken)
    {
        var result = executed.Result;
        var isError = executed.IsError;

        if (options.AfterToolCall is not null)
        {
            var afterResult = await options.AfterToolCall(
                new AfterToolCallContext(
                    assistantMessage,
                    prepared.ToolCall,
                    MessageUtilities.CloneArguments(prepared.Arguments),
                    result,
                    isError,
                    currentContext.ToSnapshot()),
                cancellationToken).ConfigureAwait(false);

            if (afterResult is not null)
            {
                result = result with
                {
                    Content = afterResult.Content.HasValue
                        ? afterResult.Content.Value ?? Array.Empty<AIContent>()
                        : result.Content,
                    Value = afterResult.Value.HasValue
                        ? afterResult.Value.Value
                        : result.Value,
                    Details = afterResult.Details.HasValue
                        ? afterResult.Details.Value
                        : result.Details,
                };

                isError = afterResult.IsError ?? isError;
            }
        }

        return await EmitToolCallOutcomeAsync(
            prepared.ToolCall,
            result,
            isError,
            emitAsync,
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task<ChatMessage> EmitToolCallOutcomeAsync(
        FunctionCallContent toolCall,
        AgentToolResult result,
        bool isError,
        AgentEventHandler emitAsync,
        CancellationToken cancellationToken)
    {
        await emitAsync(
            new AgentEvent.ToolExecutionCompleted(toolCall.CallId, toolCall.Name, result, isError),
            cancellationToken).ConfigureAwait(false);

        var toolResultMessage = AgentMessageMetadata.CreateToolResultMessage(toolCall, result, isError);

        await emitAsync(new AgentEvent.MessageStarted(toolResultMessage), cancellationToken).ConfigureAwait(false);
        await emitAsync(new AgentEvent.MessageCompleted(toolResultMessage), cancellationToken).ConfigureAwait(false);
        return toolResultMessage;
    }

    private static AIFunctionArguments CreateToolArguments(FunctionCallContent toolCall) =>
        new(toolCall.Arguments is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(toolCall.Arguments, StringComparer.Ordinal));

    private static async ValueTask<IReadOnlyList<ChatMessage>> GetPendingMessagesAsync(
        PendingMessagesProvider? provider,
        CancellationToken cancellationToken)
    {
        if (provider is null)
        {
            return Array.Empty<ChatMessage>();
        }

        return await provider(cancellationToken).ConfigureAwait(false) ?? Array.Empty<ChatMessage>();
    }

    private static async IAsyncEnumerable<ChatResponseUpdate> BufferUpdatesAsync(
        IAsyncEnumerable<ChatResponseUpdate> updates,
        List<ChatResponseUpdate> buffer,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var update in updates.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            buffer.Add(update);
            yield return update;
        }
    }

    private sealed class MutableAgentContext
    {
        public string SystemPrompt { get; init; } = string.Empty;

        public List<ChatMessage> Messages { get; } = [];

        public List<AgentTool> Tools { get; } = [];

        public AgentContext ToSnapshot() =>
            new(SystemPrompt, Messages.ToArray(), Tools.ToArray());

        public static MutableAgentContext FromSnapshot(AgentContext context)
        {
            var currentContext = new MutableAgentContext
            {
                SystemPrompt = context.SystemPrompt,
            };

            currentContext.Messages.AddRange(context.Messages.Select(MessageUtilities.Clone));
            if (context.Tools is not null)
            {
                currentContext.Tools.AddRange(context.Tools);
            }

            return currentContext;
        }
    }

    private abstract record ToolCallPreparation;

    private sealed record PreparedToolCall(
        FunctionCallContent ToolCall,
        AgentTool Tool,
        AIFunctionArguments Arguments) : ToolCallPreparation;

    private sealed record ImmediateToolCallOutcome(
        AgentToolResult Result,
        bool IsError) : ToolCallPreparation;

    private sealed record ExecutedToolCallOutcome(
        AgentToolResult Result,
        bool IsError);

    private sealed record RunningToolCall(
        PreparedToolCall Prepared,
        Task<ExecutedToolCallOutcome> Execution);
}
