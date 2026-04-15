using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.AI;
using PiSharp.Ai;

namespace PiSharp.Agent;

public static class AgentMessageMetadata
{
    public static readonly ChatFinishReason Error = new("error");

    public static readonly ChatFinishReason Aborted = new("aborted");

    private const string ApiIdKey = "pi-sharp.api_id";
    private const string ProviderIdKey = "pi-sharp.provider_id";
    private const string ModelIdKey = "pi-sharp.model_id";
    private const string FinishReasonKey = "pi-sharp.finish_reason";
    private const string UsageKey = "pi-sharp.usage";
    private const string ErrorMessageKey = "pi-sharp.error_message";
    private const string ToolCallIdKey = "pi-sharp.tool_call_id";
    private const string ToolNameKey = "pi-sharp.tool_name";
    private const string ToolIsErrorKey = "pi-sharp.tool_is_error";
    private const string ToolResultKey = "pi-sharp.tool_result";

    public static ChatMessage CreateAssistantMessage(
        ModelMetadata model,
        IEnumerable<AIContent>? contents = null,
        DateTimeOffset? createdAt = null)
    {
        var message = new ChatMessage(ChatRole.Assistant, contents?.ToList() ?? [])
        {
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };

        return WithAssistantMetadata(message, model);
    }

    public static ChatMessage CreateToolResultMessage(
        FunctionCallContent toolCall,
        AgentToolResult result,
        bool isError,
        DateTimeOffset? createdAt = null)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(result);

        var message = new ChatMessage(
            ChatRole.Tool,
            [new FunctionResultContent(toolCall.CallId, result.Value)])
        {
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
        };

        var properties = EnsureAdditionalProperties(message);
        properties[ToolCallIdKey] = toolCall.CallId;
        properties[ToolNameKey] = toolCall.Name;
        properties[ToolIsErrorKey] = isError;
        properties[ToolResultKey] = result;
        return message;
    }

    public static ChatMessage WithAssistantMetadata(
        ChatMessage message,
        ModelMetadata model,
        ChatFinishReason? finishReason = null,
        UsageDetails? usage = null,
        string? errorMessage = null)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(model);

        var clone = MessageUtilities.Clone(message);
        var properties = EnsureAdditionalProperties(clone);
        properties[ApiIdKey] = model.ApiId.Value;
        properties[ProviderIdKey] = model.ProviderId.Value;
        properties[ModelIdKey] = model.Id;

        if (finishReason is { } reason)
        {
            properties[FinishReasonKey] = reason;
        }

        if (usage is not null)
        {
            properties[UsageKey] = usage as ExtendedUsageDetails ?? ExtendedUsageDetails.FromUsage(usage);
        }

        if (!string.IsNullOrWhiteSpace(errorMessage))
        {
            properties[ErrorMessageKey] = errorMessage;
        }
        else
        {
            properties.Remove(ErrorMessageKey);
        }

        return clone;
    }

    public static bool TryGetFinishReason(ChatMessage message, [NotNullWhen(true)] out ChatFinishReason? finishReason)
    {
        finishReason = null;
        return TryGetAdditionalProperty(message, FinishReasonKey, out finishReason);
    }

    public static bool TryGetUsage(ChatMessage message, [NotNullWhen(true)] out ExtendedUsageDetails? usage)
    {
        usage = null;
        return TryGetAdditionalProperty(message, UsageKey, out usage);
    }

    public static string? GetErrorMessage(ChatMessage message) =>
        TryGetAdditionalProperty(message, ErrorMessageKey, out string? errorMessage)
            ? errorMessage
            : null;

    public static bool TryGetToolCallId(ChatMessage message, [NotNullWhen(true)] out string? toolCallId)
    {
        toolCallId = null;
        return TryGetAdditionalProperty(message, ToolCallIdKey, out toolCallId);
    }

    public static bool TryGetToolName(ChatMessage message, [NotNullWhen(true)] out string? toolName)
    {
        toolName = null;
        return TryGetAdditionalProperty(message, ToolNameKey, out toolName);
    }

    public static bool IsToolError(ChatMessage message) =>
        TryGetAdditionalProperty(message, ToolIsErrorKey, out bool isError) && isError;

    public static bool TryGetToolResult(ChatMessage message, [NotNullWhen(true)] out AgentToolResult? result)
    {
        result = null;
        return TryGetAdditionalProperty(message, ToolResultKey, out result);
    }

    private static AdditionalPropertiesDictionary EnsureAdditionalProperties(ChatMessage message)
    {
        if (message.AdditionalProperties is null)
        {
            message.AdditionalProperties = new AdditionalPropertiesDictionary();
        }

        return message.AdditionalProperties;
    }

    private static bool TryGetAdditionalProperty<T>(ChatMessage message, string key, [MaybeNullWhen(false)] out T value)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (message.AdditionalProperties is not null &&
            message.AdditionalProperties.TryGetValue(key, out var rawValue) &&
            rawValue is T typedValue)
        {
            value = typedValue;
            return true;
        }

        value = default;
        return false;
    }
}
