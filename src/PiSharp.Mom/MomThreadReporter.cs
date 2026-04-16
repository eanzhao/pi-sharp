using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Agent;
using PiSharp.CodingAgent;

namespace PiSharp.Mom;

public static class MomThreadReporter
{
    public static ICodingAgentExtension CreateExtension(
        string channelId,
        string threadTimestamp,
        ISlackMessagingClient slackClient) =>
        new ThreadReporterExtension(channelId, threadTimestamp, slackClient);

    private sealed class ThreadReporterExtension(
        string channelId,
        string threadTimestamp,
        ISlackMessagingClient slackClient) : ICodingAgentExtension
    {
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Dictionary<string, string> _toolMessageTimestamps = new(StringComparer.Ordinal);
        private string? _lastPublishedMainText;
        private DateTimeOffset _lastMainPublishAt = DateTimeOffset.MinValue;

        public async ValueTask OnAgentEventAsync(
            CodingAgentSession session,
            AgentEvent @event,
            CancellationToken cancellationToken = default)
        {
            try
            {
                switch (@event)
                {
                    case AgentEvent.MessageUpdated
                    {
                        Message.Role: var role,
                        AssistantMessageEvent: PiSharp.Ai.AssistantMessageEvent.TextDelta,
                    } updated when role == ChatRole.Assistant:
                    {
                        var text = ExtractAssistantText(updated.Message);
                        if (ShouldPublishMainMessage(text))
                        {
                            await slackClient.UpdateMessageAsync(
                                    channelId,
                                    threadTimestamp,
                                    SlackMrkdwnFormatter.Limit(SlackMrkdwnFormatter.Format(text!)),
                                    cancellationToken)
                                .ConfigureAwait(false);
                            _lastPublishedMainText = text;
                            _lastMainPublishAt = DateTimeOffset.UtcNow;
                        }

                        break;
                    }

                    case AgentEvent.ToolExecutionStarted started:
                        await UpsertToolMessageAsync(
                                started.ToolCallId,
                                BuildStartedText(started.ToolName, started.Arguments),
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;

                    case AgentEvent.ToolExecutionUpdated updated:
                    {
                        var details = FormatToolResult(updated.PartialResult);
                        if (string.IsNullOrWhiteSpace(details))
                        {
                            break;
                        }

                        await UpsertToolMessageAsync(
                                updated.ToolCallId,
                                BuildUpdatedText(updated.ToolName, details),
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }

                    case AgentEvent.ToolExecutionCompleted completed:
                        await UpsertToolMessageAsync(
                                completed.ToolCallId,
                                BuildCompletedText(completed.ToolName, completed.Result, completed.IsError),
                                cancellationToken)
                            .ConfigureAwait(false);
                        break;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch
            {
                // Thread-level tool reporting is auxiliary and must not fail the main turn.
            }
        }

        private async Task UpsertToolMessageAsync(
            string toolCallId,
            string text,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_toolMessageTimestamps.TryGetValue(toolCallId, out var timestamp))
                {
                    await slackClient.UpdateMessageAsync(channelId, timestamp, text, cancellationToken).ConfigureAwait(false);
                    return;
                }

                var messageTimestamp = await slackClient.PostMessageAsync(
                        channelId,
                        text,
                        threadTimestamp,
                        cancellationToken)
                    .ConfigureAwait(false);

                _toolMessageTimestamps[toolCallId] = messageTimestamp;
            }
            finally
            {
                _gate.Release();
            }
        }

        private bool ShouldPublishMainMessage(string? text)
        {
            if (string.IsNullOrWhiteSpace(text) ||
                string.Equals(text.Trim(), "[SILENT]", StringComparison.Ordinal))
            {
                return false;
            }

            if (string.Equals(text, _lastPublishedMainText, StringComparison.Ordinal))
            {
                return false;
            }

            if (_lastPublishedMainText is null)
            {
                return true;
            }

            var now = DateTimeOffset.UtcNow;
            return text.Length - _lastPublishedMainText.Length >= 120 ||
                now - _lastMainPublishAt >= TimeSpan.FromMilliseconds(750) ||
                text.EndsWith('\n');
        }
    }

    private static string BuildStartedText(string toolName, AIFunctionArguments arguments)
    {
        var argumentsJson = SerializeArguments(arguments);
        return SlackMrkdwnFormatter.Limit(
            $"""
*Tool:* `{toolName}` _running_
```json
{argumentsJson}
```
""");
    }

    private static string BuildUpdatedText(string toolName, string details) =>
        SlackMrkdwnFormatter.Limit(
            $"""
*Tool:* `{toolName}` _running_
```text
{EscapeCodeFence(details)}
```
""");

    private static string BuildCompletedText(string toolName, AgentToolResult result, bool isError)
    {
        var details = FormatToolResult(result) ?? (isError ? "Tool failed." : "Done.");
        var status = isError ? "_failed_" : "_done_";
        return SlackMrkdwnFormatter.Limit(
            $"""
*Tool:* `{toolName}` {status}
```text
{EscapeCodeFence(details)}
```
""");
    }

    private static string SerializeArguments(AIFunctionArguments arguments)
    {
        var dictionary = arguments.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value,
            StringComparer.Ordinal);

        return JsonSerializer.Serialize(dictionary, new JsonSerializerOptions
        {
            WriteIndented = true,
        });
    }

    private static string? FormatToolResult(AgentToolResult result)
    {
        var parts = result.Content
            .Select(content => content switch
            {
                TextContent text => text.Text,
                _ => content.ToString(),
            })
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        if (parts.Length > 0)
        {
            return string.Join(Environment.NewLine, parts);
        }

        return result.Value?.ToString();
    }

    private static string EscapeCodeFence(string text) =>
        text.Replace("```", "'''", StringComparison.Ordinal);

    private static string? ExtractAssistantText(ChatMessage message)
    {
        var parts = message.Contents
            .Select(content => content switch
            {
                TextContent text => text.Text,
                _ => null,
            })
            .Where(static part => !string.IsNullOrWhiteSpace(part))
            .ToArray();

        return parts.Length == 0
            ? null
            : string.Join(Environment.NewLine, parts);
    }
}
