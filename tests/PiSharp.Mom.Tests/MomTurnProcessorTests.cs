using System.Net;
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

        var store = new MomChannelStore(_workspaceDirectory);
        await store.LogIncomingEventAsync(new SlackIncomingEvent(
            "C123",
            "U123",
            "<@B123> summarize this repo",
            "12345.6789",
            "app_mention",
            IsDirectMessage: false));

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
        Assert.Contains(request, message => message.Role == ChatRole.User && message.Text == "[U123]: summarize this repo");

        var sessionDirectory = Path.Combine(_workspaceDirectory, "C123", ".pi-sharp", "sessions");
        Assert.Single(Directory.GetFiles(sessionDirectory, "*.jsonl"));

        var logPath = Path.Combine(_workspaceDirectory, "C123", "log.jsonl");
        var logLines = File.ReadAllLines(logPath);
        Assert.Equal(2, logLines.Length);
        Assert.Contains("summarize this repo", logLines[0]);
        Assert.Contains("done", logLines[1]);
    }

    [Fact]
    public async Task ProcessAsync_IncludesDownloadedAttachmentsInPromptAndLog()
    {
        var chatClient = new FakeChatClient(
            [
                CreateUpdate(new TextContent("processed"), ChatFinishReason.Stop),
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

        using var httpClient = new HttpClient(new StubHttpMessageHandler(static _ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("notes body"),
            }));

        using var store = new MomChannelStore(_workspaceDirectory, "xoxb-test", httpClient);
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
            slackClient,
            store);

        var incomingEvent = new SlackIncomingEvent(
            "C123",
            "U123",
            "<@B123> review the attachment",
            "12345.6789",
            "app_mention",
            IsDirectMessage: false,
            Files:
            [
                new SlackFileReference("notes.txt", "https://example.com/notes.txt"),
            ]);

        await store.LogIncomingEventAsync(incomingEvent);
        await processor.ProcessAsync(incomingEvent);

        var request = Assert.Single(chatClient.Requests);
        Assert.Contains(request, message =>
            message.Role == ChatRole.User &&
            message.Text is not null &&
            message.Text.Contains("[U123]: review the attachment", StringComparison.Ordinal) &&
            message.Text.Contains("notes.txt => attachments/12345678_notes.txt", StringComparison.Ordinal));

        var attachmentPath = Path.Combine(_workspaceDirectory, "C123", "attachments", "12345678_notes.txt");
        Assert.True(File.Exists(attachmentPath));
        Assert.Equal("notes body", await File.ReadAllTextAsync(attachmentPath));

        var logLines = File.ReadAllLines(Path.Combine(_workspaceDirectory, "C123", "log.jsonl"));
        Assert.Contains("\"original\":\"notes.txt\"", logLines[0]);
        Assert.Contains("\"local\":\"attachments/12345678_notes.txt\"", logLines[0]);
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

    [Fact]
    public async Task ProcessAsync_DeletesStatusMessageForSilentResponses()
    {
        var chatClient = new FakeChatClient(
            [
                CreateUpdate(new TextContent("[SILENT]"), ChatFinishReason.Stop),
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
            "EVENT",
            "[EVENT:daily.json:periodic:0 9 * * 1-5] Check inbox",
            "12345.6789",
            "event",
            IsDirectMessage: false,
            QueueIfBusy: true,
            StatusText: "_Starting event: daily.json_"));

        Assert.Single(slackClient.Posts);
        Assert.Equal("_Starting event: daily.json_", slackClient.Posts[0].Text);
        Assert.Empty(slackClient.Updates);
        Assert.Single(slackClient.Deletes);
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

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> createResponse) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(createResponse(request));
    }
}
