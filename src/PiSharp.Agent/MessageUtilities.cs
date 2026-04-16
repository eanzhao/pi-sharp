using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Ai;

namespace PiSharp.Agent;

internal static class MessageUtilities
{
    private static readonly JsonSerializerOptions ToolArgumentsJsonOptions = new()
    {
        WriteIndented = false,
    };

    public static ChatMessage Clone(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var clone = new ChatMessage(message.Role, CloneContents(message.Contents))
        {
            AuthorName = message.AuthorName,
            CreatedAt = message.CreatedAt,
            MessageId = message.MessageId,
            RawRepresentation = message.RawRepresentation,
            AdditionalProperties = CloneAdditionalProperties(message.AdditionalProperties),
        };

        return clone;
    }

    public static AIFunctionArguments CloneArguments(AIFunctionArguments arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);

        var clone = new AIFunctionArguments(arguments.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.Ordinal))
        {
            Services = arguments.Services,
        };

        if (arguments.Context is null || clone.Context is null)
        {
            return clone;
        }

        foreach (var pair in arguments.Context)
        {
            clone.Context[pair.Key] = pair.Value;
        }

        return clone;
    }

    public static ChatMessage ApplyAssistantEvent(
        ChatMessage? currentMessage,
        AssistantMessageEvent assistantMessageEvent,
        ModelMetadata model)
    {
        var message = currentMessage ?? AgentMessageMetadata.CreateAssistantMessage(model);

        return assistantMessageEvent switch
        {
            AssistantMessageEvent.TextStart => AppendContent(message, new TextContent(string.Empty)),
            AssistantMessageEvent.TextDelta delta => UpdateLastText(message, delta.Text),
            AssistantMessageEvent.ThinkingStart => AppendContent(message, new TextReasoningContent(string.Empty)),
            AssistantMessageEvent.ThinkingDelta delta => UpdateLastReasoning(message, delta.Text),
            AssistantMessageEvent.ToolCallStart start => AppendContent(
                message,
                new FunctionCallContent(
                    start.CallId,
                    start.Name,
                    new Dictionary<string, object?>(StringComparer.Ordinal))),
            AssistantMessageEvent.ToolCallDelta delta => UpdateToolCallArguments(message, delta.CallId, delta.ArgumentsDelta),
            _ => message,
        };
    }

    public static IList<AIContent> CloneContents(IList<AIContent> contents)
    {
        ArgumentNullException.ThrowIfNull(contents);

        return contents.Select(CloneContent).ToList();
    }

    public static AdditionalPropertiesDictionary? CloneAdditionalProperties(
        AdditionalPropertiesDictionary? properties)
    {
        if (properties is null)
        {
            return null;
        }

        var clone = new AdditionalPropertiesDictionary();
        foreach (var pair in properties)
        {
            clone[pair.Key] = pair.Value;
        }

        return clone;
    }

    private static AIContent CloneContent(AIContent content) =>
        content switch
        {
            TextContent text => new TextContent(text.Text),
            TextReasoningContent reasoning => new TextReasoningContent(reasoning.Text),
            DataContent data => new DataContent(data.Uri, data.MediaType)
            {
                Name = data.Name,
            },
            FunctionCallContent toolCall => new FunctionCallContent(
                toolCall.CallId,
                toolCall.Name,
                CloneArgumentDictionary(toolCall.Arguments)),
            FunctionResultContent result => new FunctionResultContent(result.CallId, result.Result),
            _ => content,
        };

    private static ChatMessage AppendContent(ChatMessage message, AIContent content)
    {
        var clone = Clone(message);
        clone.Contents.Add(content);
        return clone;
    }

    private static ChatMessage UpdateLastText(ChatMessage message, string delta)
    {
        var clone = Clone(message);
        if (clone.Contents.Count > 0 && clone.Contents[^1] is TextContent textContent)
        {
            clone.Contents[^1] = new TextContent(textContent.Text + delta);
        }
        else
        {
            clone.Contents.Add(new TextContent(delta));
        }

        return clone;
    }

    private static ChatMessage UpdateLastReasoning(ChatMessage message, string delta)
    {
        var clone = Clone(message);
        if (clone.Contents.Count > 0 && clone.Contents[^1] is TextReasoningContent reasoningContent)
        {
            clone.Contents[^1] = new TextReasoningContent(reasoningContent.Text + delta);
        }
        else
        {
            clone.Contents.Add(new TextReasoningContent(delta));
        }

        return clone;
    }

    private static ChatMessage UpdateToolCallArguments(ChatMessage message, string callId, string delta)
    {
        var clone = Clone(message);
        var index = FindLastToolCallIndex(clone.Contents, callId);
        if (index < 0 || clone.Contents[index] is not FunctionCallContent toolCall)
        {
            return clone;
        }

        var previousJson = SerializeArguments(toolCall.Arguments);
        var nextJson = ApplyJsonDelta(previousJson, delta);

        if (!TryParseArguments(nextJson, out var arguments))
        {
            return clone;
        }

        clone.Contents[index] = new FunctionCallContent(toolCall.CallId, toolCall.Name, arguments);
        return clone;
    }

    private static int FindLastToolCallIndex(IList<AIContent> contents, string callId)
    {
        for (var index = contents.Count - 1; index >= 0; index--)
        {
            if (contents[index] is FunctionCallContent toolCall &&
                string.Equals(toolCall.CallId, callId, StringComparison.Ordinal))
            {
                return index;
            }
        }

        return -1;
    }

    private static Dictionary<string, object?> CloneArgumentDictionary(IDictionary<string, object?>? arguments) =>
        arguments is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(arguments, StringComparer.Ordinal);

    private static string SerializeArguments(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return string.Empty;
        }

        return JsonSerializer.Serialize(arguments, ToolArgumentsJsonOptions);
    }

    private static string ApplyJsonDelta(string previousJson, string delta)
    {
        if (string.IsNullOrEmpty(delta))
        {
            return previousJson;
        }

        if (string.IsNullOrEmpty(previousJson) || delta.StartsWith('{'))
        {
            return delta;
        }

        if (previousJson.Length > 0 && previousJson[^1] == '}')
        {
            return previousJson[..^1] + delta;
        }

        return previousJson + delta;
    }

    private static bool TryParseArguments(string json, out Dictionary<string, object?> arguments)
    {
        try
        {
            arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(json, ToolArgumentsJsonOptions)
                ?? new Dictionary<string, object?>(StringComparer.Ordinal);
            return true;
        }
        catch (JsonException)
        {
            arguments = new Dictionary<string, object?>(StringComparer.Ordinal);
            return false;
        }
    }
}
