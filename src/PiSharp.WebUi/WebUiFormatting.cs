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

    public static IReadOnlyList<ArtifactVersion> GetArtifacts(ChatMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        var artifacts = new List<ArtifactVersion>();

        if (AgentMessageMetadata.TryGetToolResult(message, out var toolResult))
        {
            artifacts.AddRange(ExtractArtifacts(toolResult.Value));
            artifacts.AddRange(ExtractArtifacts(toolResult.Details));
            foreach (var content in toolResult.Content)
            {
                artifacts.AddRange(ExtractArtifacts(content));
            }
        }

        foreach (var content in message.Contents)
        {
            artifacts.AddRange(ExtractArtifacts(content));
        }

        return artifacts
            .GroupBy(
                static artifact => $"{artifact.ArtifactId}:{artifact.VersionNumber}:{artifact.NormalizedContentType}",
                StringComparer.Ordinal)
            .Select(static group => group.Last())
            .ToArray();
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

            if (GetArtifacts(message).Count > 0)
            {
                return string.Empty;
            }

            return SerializeValue(toolResult.Value);
        }

        if (message.Contents.OfType<FunctionResultContent>().FirstOrDefault() is { } result)
        {
            if (GetArtifacts(message).Count > 0)
            {
                return string.Empty;
            }

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

    private static IReadOnlyList<ArtifactVersion> ExtractArtifacts(AIContent content) =>
        content switch
        {
            FunctionResultContent result => ExtractArtifacts(result.Result),
            TextContent text => ExtractArtifacts(text.Text),
            _ => Array.Empty<ArtifactVersion>(),
        };

    private static IReadOnlyList<ArtifactVersion> ExtractArtifacts(object? value)
    {
        switch (value)
        {
            case null:
                return Array.Empty<ArtifactVersion>();

            case ArtifactVersion artifact:
                return [artifact with { ContentType = artifact.NormalizedContentType }];

            case IEnumerable<ArtifactVersion> artifacts:
                return artifacts.Select(static artifact => artifact with { ContentType = artifact.NormalizedContentType }).ToArray();

            case JsonElement jsonElement:
                return ExtractArtifacts(jsonElement);

            case string text:
                return ExtractArtifactsFromString(text);

            case IEnumerable<object?> items:
                return items.SelectMany(ExtractArtifacts).ToArray();

            default:
                try
                {
                    return ExtractArtifacts(JsonSerializer.SerializeToElement(value));
                }
                catch (Exception) when (value is not string)
                {
                    return Array.Empty<ArtifactVersion>();
                }
        }
    }

    private static IReadOnlyList<ArtifactVersion> ExtractArtifactsFromString(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return Array.Empty<ArtifactVersion>();
        }

        var trimmed = text.Trim();
        if ((trimmed.StartsWith('{') && trimmed.EndsWith('}')) || (trimmed.StartsWith('[') && trimmed.EndsWith(']')))
        {
            try
            {
                return ExtractArtifacts(JsonSerializer.Deserialize<JsonElement>(trimmed));
            }
            catch (JsonException)
            {
            }
        }

        if (trimmed.StartsWith("<svg", StringComparison.OrdinalIgnoreCase))
        {
            return [new ArtifactVersion("artifact", 1, "svg", text)];
        }

        if (trimmed.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
        {
            return [new ArtifactVersion("artifact", 1, "html", text)];
        }

        return Array.Empty<ArtifactVersion>();
    }

    private static IReadOnlyList<ArtifactVersion> ExtractArtifacts(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Array)
        {
            return element.EnumerateArray().SelectMany(ExtractArtifacts).ToArray();
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            return Array.Empty<ArtifactVersion>();
        }

        var nestedArtifacts = new List<ArtifactVersion>();
        if (TryGetProperty(element, "artifacts", out var artifactsProperty))
        {
            nestedArtifacts.AddRange(ExtractArtifacts(artifactsProperty));
        }

        if (TryGetProperty(element, "artifact", out var artifactProperty))
        {
            nestedArtifacts.AddRange(ExtractArtifacts(artifactProperty));
        }

        if (TryCreateArtifact(element, out var artifact))
        {
            nestedArtifacts.Add(artifact);
        }

        return nestedArtifacts;
    }

    private static bool TryCreateArtifact(JsonElement element, out ArtifactVersion artifact)
    {
        artifact = default!;

        if (!TryReadStringProperty(element, ["artifactId", "id", "filename", "name"], out var artifactId) ||
            !TryReadIntegerProperty(element, ["versionNumber", "version"], out var versionNumber) ||
            !TryReadStringProperty(element, ["contentType", "type", "mimeType"], out var contentType) ||
            !TryReadStringProperty(element, ["content", "value", "text"], out var content))
        {
            return false;
        }

        var normalizedContentType = ArtifactVersion.NormalizeContentType(contentType);
        if (normalizedContentType is null)
        {
            return false;
        }

        artifact = new ArtifactVersion(artifactId, versionNumber, normalizedContentType, content);
        return true;
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static bool TryReadStringProperty(JsonElement element, IReadOnlyList<string> propertyNames, out string value)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var propertyValue))
            {
                continue;
            }

            if (propertyValue.ValueKind == JsonValueKind.String)
            {
                value = propertyValue.GetString() ?? string.Empty;
                return !string.IsNullOrWhiteSpace(value);
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryReadIntegerProperty(JsonElement element, IReadOnlyList<string> propertyNames, out int value)
    {
        foreach (var propertyName in propertyNames)
        {
            if (!TryGetProperty(element, propertyName, out var propertyValue))
            {
                continue;
            }

            if (propertyValue.ValueKind == JsonValueKind.Number && propertyValue.TryGetInt32(out value))
            {
                return true;
            }

            if (propertyValue.ValueKind == JsonValueKind.String &&
                int.TryParse(propertyValue.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
        }

        value = 0;
        return false;
    }
}
