using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.AI;
using PiSharp.Agent;
using PiSharp.Ai;

namespace PiSharp.WebUi;

public static class WebUiFormatting
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = new()
    {
        WriteIndented = true,
    };

    public static string FormatUsage(ExtendedUsageDetails? usage)
    {
        if (usage is null)
        {
            return string.Empty;
        }

        var parts = new List<string>();

        if (usage.InputTokenCount is > 0)
        {
            parts.Add($"↑{FormatTokenCount(usage.InputTokenCount.Value)}");
        }

        if (usage.OutputTokenCount is > 0)
        {
            parts.Add($"↓{FormatTokenCount(usage.OutputTokenCount.Value)}");
        }

        if (usage.CacheReadTokenCount is > 0)
        {
            parts.Add($"R{FormatTokenCount(usage.CacheReadTokenCount.Value)}");
        }

        if (usage.CacheWriteTokenCount is > 0)
        {
            parts.Add($"W{FormatTokenCount(usage.CacheWriteTokenCount.Value)}");
        }

        if (usage.Cost?.TotalCost is > 0m)
        {
            parts.Add(FormatCost(usage.Cost.TotalCost));
        }

        return string.Join(' ', parts);
    }

    public static string FormatCost(decimal cost) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"${Math.Round(cost, 4, MidpointRounding.AwayFromZero):0.0000}");

    public static string FormatTokenCount(long count)
    {
        if (count < 1_000)
        {
            return count.ToString(CultureInfo.InvariantCulture);
        }

        if (count < 10_000)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{count / 1_000d:0.0}k");
        }

        return string.Create(
            CultureInfo.InvariantCulture,
            $"{Math.Round(count / 1_000d, MidpointRounding.AwayFromZero):0}k");
    }

    public static string FormatThinkingLevel(ThinkingLevel level) =>
        level switch
        {
            ThinkingLevel.ExtraHigh => "extra high",
            _ => level.ToString().ToLowerInvariant(),
        };

    public static string ResolveToolLabel(FunctionCallContent toolCall, IReadOnlyList<AgentTool> tools)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        ArgumentNullException.ThrowIfNull(tools);

        return tools.FirstOrDefault(tool => string.Equals(tool.Name, toolCall.Name, StringComparison.Ordinal))?.Label
            ?? toolCall.Name;
    }

    public static string SerializeToolArguments(FunctionCallContent toolCall)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        return SerializeValue(toolCall.Arguments);
    }

    public static string SerializeValue(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        if (value is string text)
        {
            return text;
        }

        try
        {
            return JsonSerializer.Serialize(value, IndentedJsonOptions);
        }
        catch (NotSupportedException)
        {
            return value.ToString() ?? string.Empty;
        }
    }

    public static string GetToolResultBody(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (AgentMessageMetadata.TryGetToolResult(message, out var toolResult))
        {
            var renderedContent = string.Join(
                "\n\n",
                toolResult.Content.Select(RenderContentBlock).Where(static value => !string.IsNullOrWhiteSpace(value)));

            if (!string.IsNullOrWhiteSpace(renderedContent))
            {
                return renderedContent;
            }

            return SerializeValue(toolResult.Value);
        }

        if (message.Contents.OfType<FunctionResultContent>().FirstOrDefault() is { } result)
        {
            return SerializeValue(result.Result);
        }

        return string.Empty;
    }

    private static string RenderContentBlock(AIContent content) =>
        content switch
        {
            TextContent text => text.Text,
            TextReasoningContent reasoning => reasoning.Text,
            _ => SerializeValue(content)
        };
}
