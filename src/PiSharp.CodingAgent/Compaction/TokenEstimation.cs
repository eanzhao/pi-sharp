using Microsoft.Extensions.AI;

namespace PiSharp.CodingAgent;

public static class TokenEstimation
{
    private const int CharsPerToken = 4;
    private const int ImageTokenEstimate = 1200;

    public static int EstimateTokens(ChatMessage message)
    {
        var total = 0;

        foreach (var content in message.Contents)
        {
            total += content switch
            {
                TextContent tc => EstimateTextTokens(tc.Text),
                TextReasoningContent rc => EstimateTextTokens(rc.Text),
                DataContent => ImageTokenEstimate,
                FunctionCallContent fc => EstimateTextTokens(fc.Name) + EstimateArgumentTokens(fc.Arguments),
                FunctionResultContent fr => EstimateTextTokens(fr.Result?.ToString()),
                _ => 0,
            };
        }

        total += 4;
        return total;
    }

    public static int EstimateTokens(IEnumerable<ChatMessage> messages)
    {
        var total = 0;
        foreach (var message in messages)
        {
            total += EstimateTokens(message);
        }

        return total;
    }

    public static bool ShouldCompact(int contextTokens, int contextWindow, CompactionSettings settings)
    {
        if (!settings.Enabled)
        {
            return false;
        }

        var availableTokens = contextWindow - settings.ReserveTokens;
        return availableTokens > 0 && contextTokens > availableTokens;
    }

    private static int EstimateTextTokens(string? text) =>
        string.IsNullOrEmpty(text) ? 0 : (text.Length + CharsPerToken - 1) / CharsPerToken;

    private static int EstimateArgumentTokens(IDictionary<string, object?>? arguments)
    {
        if (arguments is null || arguments.Count == 0)
        {
            return 0;
        }

        var total = 0;
        foreach (var kvp in arguments)
        {
            total += EstimateTextTokens(kvp.Key);
            total += EstimateTextTokens(kvp.Value?.ToString());
        }

        return total;
    }
}
