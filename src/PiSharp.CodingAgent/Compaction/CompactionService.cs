using Microsoft.Extensions.AI;

namespace PiSharp.CodingAgent;

public sealed record CompactionSettings(
    bool Enabled = true,
    int ReserveTokens = 16_384,
    int KeepRecentTokens = 20_000);

public sealed record CutPointResult(
    int FirstKeptIndex,
    bool SplitsTurn);

public sealed class CompactionService
{
    private readonly IChatClient _chatClient;
    private readonly CompactionSettings _settings;

    public CompactionService(IChatClient chatClient, CompactionSettings? settings = null)
    {
        _chatClient = chatClient ?? throw new ArgumentNullException(nameof(chatClient));
        _settings = settings ?? new CompactionSettings();
    }

    public CompactionSettings Settings => _settings;

    public CutPointResult FindCutPoint(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count <= 1)
        {
            return new CutPointResult(0, false);
        }

        var accumulatedTokens = 0;
        var lastValidCut = messages.Count;

        for (var i = messages.Count - 1; i >= 0; i--)
        {
            accumulatedTokens += TokenEstimation.EstimateTokens(messages[i]);

            if (accumulatedTokens >= _settings.KeepRecentTokens)
            {
                break;
            }

            if (IsTurnBoundary(messages, i))
            {
                lastValidCut = i;
            }
        }

        if (lastValidCut <= 1)
        {
            return new CutPointResult(0, false);
        }

        var splitsTurn = lastValidCut < messages.Count
            && lastValidCut > 0
            && messages[lastValidCut].Role == ChatRole.Tool;

        return new CutPointResult(lastValidCut, splitsTurn);
    }

    public async Task<string> GenerateSummaryAsync(
        IReadOnlyList<ChatMessage> messagesToSummarize,
        CancellationToken ct = default)
    {
        if (messagesToSummarize.Count == 0)
        {
            return string.Empty;
        }

        var conversationText = FormatConversation(messagesToSummarize);
        var prompt = $"""
            Summarize the following conversation concisely. Focus on:
            - Key decisions made
            - Files read, written, or modified
            - Current state of the task
            - Any unresolved issues

            Conversation:
            {conversationText}

            Provide a concise summary:
            """;

        var response = await _chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, prompt)],
            cancellationToken: ct).ConfigureAwait(false);

        return response.Text ?? string.Empty;
    }

    public async Task<CompactionEntry?> TryCompactAsync(
        IReadOnlyList<ChatMessage> messages,
        int contextWindow,
        CancellationToken ct = default)
    {
        var contextTokens = TokenEstimation.EstimateTokens(messages);
        if (!TokenEstimation.ShouldCompact(contextTokens, contextWindow, _settings))
        {
            return null;
        }

        var cutPoint = FindCutPoint(messages);
        if (cutPoint.FirstKeptIndex <= 0)
        {
            return null;
        }

        var messagesToSummarize = messages.Take(cutPoint.FirstKeptIndex).ToArray();
        var summary = await GenerateSummaryAsync(messagesToSummarize, ct).ConfigureAwait(false);

        return new CompactionEntry
        {
            Id = SessionEntry.NewId(),
            Timestamp = SessionEntry.Now(),
            Summary = summary,
            FirstKeptEntryId = cutPoint.FirstKeptIndex < messages.Count
                ? $"index:{cutPoint.FirstKeptIndex}"
                : string.Empty,
            TokensBefore = contextTokens,
        };
    }

    private static bool IsTurnBoundary(IReadOnlyList<ChatMessage> messages, int index)
    {
        if (index <= 0 || index >= messages.Count)
        {
            return false;
        }

        var role = messages[index].Role;
        return role == ChatRole.User || role == ChatRole.Assistant;
    }

    private static string FormatConversation(IReadOnlyList<ChatMessage> messages)
    {
        var parts = new List<string>();
        foreach (var message in messages)
        {
            var role = message.Role == ChatRole.User ? "User"
                : message.Role == ChatRole.Assistant ? "Assistant"
                : "Tool";
            var text = message.Text ?? "(non-text content)";
            parts.Add($"[{role}]: {text}");
        }

        return string.Join("\n\n", parts);
    }
}
