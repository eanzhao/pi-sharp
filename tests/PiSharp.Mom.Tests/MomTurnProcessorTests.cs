using Microsoft.Extensions.AI;
using PiSharp.Ai;
using PiSharp.CodingAgent;
using PiSharp.Mom;
using PiSharp.Mom.Tests.Support;

namespace PiSharp.Mom.Tests;

public sealed class MomTurnProcessorTests : IDisposable
{
    private readonly string _workspaceDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-mom-{Guid.NewGuid():N}");

    [Fact]
    public async Task ProcessAsync_RunsAgentAndPersistsLogAndSession()
    {
        var chatClient = new FakeChatClient(
            [
                CreateUpdate(new TextContent("**done** [docs](https://example.com)"), ChatFinishReason.Stop),
            ]);

        var slackClient = new FakeSlackMessagingClient();
        var environment = new MomConsoleEnvironment(
            new StringReader(string.Empty),
            new StringWriter(),
            new StringWriter(),
            _workspaceDirectory,
            new Dictionary<string, string?>
            {
                ["OPENAI_API_KEY"] = "env-key",
            });

        var processor = new MomTurnProcessor(
            environment,
            new MomRuntimeOptions
            {
                WorkspaceDirectory = _workspaceDirectory,
                Provider = "openai",
                Model = "gpt-4.1-mini",
                ApiKey = "test-key",
            },
            CreateProviderCatalog(chatClient),
            static (_, _) => SettingsManager.InMemory(),
            slackClient);

        await processor.ProcessAsync(new SlackIncomingEvent(
            "C123",
            "U123",
            "<@B123> summarize this repo",
            "12345.6789",
            "app_mention",
            IsDirectMessage: false));

        Assert.Single(slackClient.Posts);
        Assert.Equal("_Thinking..._", slackClient.Posts[0].Text);
        Assert.Single(slackClient.Updates);
        Assert.Equal("*done* <https://example.com|docs>", slackClient.Updates[0].Text);

        var request = Assert.Single(chatClient.Requests);
        Assert.Contains(request, message => message.Role == ChatRole.User && message.Text == "summarize this repo");

        var sessionDirectory = Path.Combine(_workspaceDirectory, "C123", ".pi-sharp", "sessions");
        Assert.Single(Directory.GetFiles(sessionDirectory, "*.jsonl"));

        var logPath = Path.Combine(_workspaceDirectory, "C123", "log.jsonl");
        var logLines = File.ReadAllLines(logPath);
        Assert.Equal(2, logLines.Length);
        Assert.Contains("summarize this repo", logLines[0]);
        Assert.Contains("done", logLines[1]);
    }

    [Fact]
    public async Task ProcessAsync_HandlesEmptyMentionWithoutCreatingSession()
    {
        var slackClient = new FakeSlackMessagingClient();
        var environment = new MomConsoleEnvironment(
            new StringReader(string.Empty),
            new StringWriter(),
            new StringWriter(),
            _workspaceDirectory);

        var processor = new MomTurnProcessor(
            environment,
            new MomRuntimeOptions
            {
                WorkspaceDirectory = _workspaceDirectory,
                Provider = "openai",
                Model = "gpt-4.1-mini",
                ApiKey = "test-key",
            },
            CreateProviderCatalog(new FakeChatClient()),
            static (_, _) => SettingsManager.InMemory(),
            slackClient);

        await processor.ProcessAsync(new SlackIncomingEvent(
            "C123",
            "U123",
            "<@B123>",
            "12345.6789",
            "app_mention",
            IsDirectMessage: false));

        Assert.Single(slackClient.Posts);
        Assert.Equal("What do you need me to do?", slackClient.Posts[0].Text);
        Assert.Empty(slackClient.Updates);
    }

    private static CodingAgentProviderCatalog CreateProviderCatalog(FakeChatClient chatClient) =>
        new(
        [
            new CodingAgentProviderFactory
            {
                Configuration = new ProviderConfiguration(
                    ProviderId.OpenAi,
                    ApiId.OpenAi,
                    "OpenAI",
                    DefaultModelId: "gpt-4.1-mini",
                    ApiKeyEnvironmentVariable: "OPENAI_API_KEY"),
                KnownModels =
                [
                    new ModelMetadata(
                        "gpt-4.1-mini",
                        "GPT-4.1 mini",
                        ApiId.OpenAi,
                        ProviderId.OpenAi,
                        1_000_000,
                        32_768,
                        ModelCapability.TextInput | ModelCapability.Streaming | ModelCapability.ToolCalling,
                        ModelPricing.Free),
                ],
                CreateChatClient = (_, _) => chatClient,
            },
        ]);

    private static ChatResponseUpdate CreateUpdate(
        AIContent content,
        ChatFinishReason? finishReason = null) =>
        new(ChatRole.Assistant, [content])
        {
            FinishReason = finishReason,
        };

    public void Dispose()
    {
        if (Directory.Exists(_workspaceDirectory))
        {
            Directory.Delete(_workspaceDirectory, recursive: true);
        }
    }
}
