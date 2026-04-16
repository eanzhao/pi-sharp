using Microsoft.Extensions.AI;
using PiSharp.CodingAgent;

namespace PiSharp.CodingAgent.Tests;

public sealed class CompactionTests
{
    [Fact]
    public void EstimateTokens_UsesCharsDiv4Heuristic()
    {
        var message = new ChatMessage(ChatRole.User, "hello world");

        var tokens = TokenEstimation.EstimateTokens(message);

        Assert.True(tokens > 0);
        Assert.True(tokens <= 20);
    }

    [Fact]
    public void EstimateTokens_HandlesEmptyMessage()
    {
        var message = new ChatMessage(ChatRole.User, "");

        var tokens = TokenEstimation.EstimateTokens(message);

        Assert.Equal(4, tokens);
    }

    [Fact]
    public void EstimateTokens_SumsMultipleMessages()
    {
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "hello"),
            new ChatMessage(ChatRole.Assistant, "world"),
        };

        var total = TokenEstimation.EstimateTokens(messages);

        Assert.True(total > TokenEstimation.EstimateTokens(messages[0]));
    }

    [Fact]
    public void ShouldCompact_ReturnsFalse_WhenDisabled()
    {
        var settings = new CompactionSettings(Enabled: false);

        Assert.False(TokenEstimation.ShouldCompact(100_000, 128_000, settings));
    }

    [Fact]
    public void ShouldCompact_ReturnsFalse_WhenUnderThreshold()
    {
        var settings = new CompactionSettings(ReserveTokens: 16_000);

        Assert.False(TokenEstimation.ShouldCompact(50_000, 128_000, settings));
    }

    [Fact]
    public void ShouldCompact_ReturnsTrue_WhenOverThreshold()
    {
        var settings = new CompactionSettings(ReserveTokens: 16_000);

        Assert.True(TokenEstimation.ShouldCompact(120_000, 128_000, settings));
    }

    [Fact]
    public void FindCutPoint_ReturnsZero_ForSingleMessage()
    {
        var service = new CompactionService(new StubChatClient());
        var messages = new[] { new ChatMessage(ChatRole.User, "hello") };

        var result = service.FindCutPoint(messages);

        Assert.Equal(0, result.FirstKeptIndex);
    }

    [Fact]
    public void FindCutPoint_FindsValidCutAtTurnBoundary()
    {
        var service = new CompactionService(new StubChatClient(), new CompactionSettings(KeepRecentTokens: 20));
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, new string('a', 100)),
            new ChatMessage(ChatRole.Assistant, new string('b', 100)),
            new ChatMessage(ChatRole.User, new string('c', 40)),
            new ChatMessage(ChatRole.Assistant, new string('d', 40)),
        };

        var result = service.FindCutPoint(messages);

        Assert.True(result.FirstKeptIndex > 0);
        Assert.True(result.FirstKeptIndex < messages.Length);
    }

    [Fact]
    public async Task TryCompactAsync_ReturnsNull_WhenNotNeeded()
    {
        var service = new CompactionService(new StubChatClient());
        var messages = new[] { new ChatMessage(ChatRole.User, "short") };

        var result = await service.TryCompactAsync(messages, contextWindow: 128_000);

        Assert.Null(result);
    }

    [Fact]
    public void ExtractFileOperations_CollectsReadAndModifiedFiles()
    {
        var messages = new[]
        {
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "call-read",
                        BuiltInToolNames.Read,
                        new Dictionary<string, object?>
                        {
                            ["path"] = "README.md",
                        }),
                ]),
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "call-write",
                        BuiltInToolNames.Write,
                        new Dictionary<string, object?>
                        {
                            ["path"] = "notes.txt",
                        }),
                ]),
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "call-edit-diff",
                        BuiltInToolNames.EditDiff,
                        new Dictionary<string, object?>
                        {
                            ["diff"] = """
                                --- a/src/old.cs
                                +++ b/src/new.cs
                                @@ -1 +1 @@
                                -old
                                +new
                                """,
                        }),
                ]),
        };

        var details = CompactionService.ExtractFileOperations(messages);

        Assert.Equal(["README.md"], details.ReadFiles);
        Assert.Equal(["notes.txt", "src/new.cs"], details.ModifiedFiles);
    }

    [Fact]
    public async Task GenerateSummaryAsync_IncludesPreviousSummaryAndFileSections()
    {
        var chatClient = new StubChatClient();
        var service = new CompactionService(chatClient);
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, "Please update the file."),
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "call-read",
                        BuiltInToolNames.Read,
                        new Dictionary<string, object?>
                        {
                            ["path"] = "README.md",
                        }),
                    new FunctionCallContent(
                        "call-edit",
                        BuiltInToolNames.Edit,
                        new Dictionary<string, object?>
                        {
                            ["path"] = "README.md",
                        }),
                ]),
        };

        var summary = await service.GenerateSummaryAsync(messages, previousSummary: "Previous summary here.");

        Assert.Contains("Summary of conversation.", summary);
        Assert.Contains("<read-files>\nREADME.md\n</read-files>", summary);
        Assert.Contains("<modified-files>\nREADME.md\n</modified-files>", summary);
        Assert.NotNull(chatClient.LastPrompt);
        Assert.Contains("Previous summary here.", chatClient.LastPrompt);
    }

    [Fact]
    public async Task TryCompactAsync_PopulatesCompactionDetails()
    {
        var service = new CompactionService(new StubChatClient(), new CompactionSettings(ReserveTokens: 1, KeepRecentTokens: 20));
        var messages = new[]
        {
            new ChatMessage(ChatRole.User, new string('a', 100)),
            new ChatMessage(
                ChatRole.Assistant,
                [
                    new FunctionCallContent(
                        "call-read",
                        BuiltInToolNames.Read,
                        new Dictionary<string, object?>
                        {
                            ["path"] = "README.md",
                        }),
                ]),
            new ChatMessage(ChatRole.User, new string('b', 60)),
            new ChatMessage(ChatRole.Assistant, new string('c', 60)),
        };

        var result = await service.TryCompactAsync(messages, contextWindow: 40);

        Assert.NotNull(result);
        Assert.NotNull(result!.Details);
        Assert.Equal(["README.md"], result.Details!.ReadFiles);
        Assert.Empty(result.Details.ModifiedFiles);
    }

    private sealed class StubChatClient : IChatClient
    {
        public string? LastPrompt { get; private set; }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateResponse(messages));

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }

        private ChatResponse CreateResponse(IEnumerable<ChatMessage> messages)
        {
            LastPrompt = messages.LastOrDefault()?.Text;
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "Summary of conversation."));
        }
    }
}
