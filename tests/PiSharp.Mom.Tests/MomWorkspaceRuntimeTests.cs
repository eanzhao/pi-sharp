using System.Net;
using Microsoft.Extensions.AI;
using PiSharp.Ai;
using PiSharp.CodingAgent;
using PiSharp.Mom;
using PiSharp.Mom.Tests.Support;

namespace PiSharp.Mom.Tests;

public sealed class MomWorkspaceRuntimeTests : IDisposable
{
    private readonly string _workspaceDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-mom-runtime-{Guid.NewGuid():N}");

    [Fact]
    public async Task DispatchAsync_LogsChannelChatterAndSyncsItIntoNextTurn()
    {
        var chatClient = new FakeChatClient(
            [
                CreateUpdate(new TextContent("ack"), ChatFinishReason.Stop),
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

        var store = new MomChannelStore(_workspaceDirectory);
        var turnProcessor = new MomTurnProcessor(
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
        var runtime = new MomWorkspaceRuntime(turnProcessor, slackClient, store);

        await runtime.DispatchAsync(new SlackIncomingEvent(
            "C123",
            "U234",
            "Earlier channel message",
            "12345.1000",
            "message",
            IsDirectMessage: false,
            RequiresResponse: false));

        await runtime.DispatchAsync(new SlackIncomingEvent(
            "C123",
            "U123",
            "<@B123> summarize the channel",
            "12345.2000",
            "app_mention",
            IsDirectMessage: false));

        await runtime.WaitForIdleAsync("C123");

        Assert.Single(chatClient.Requests);
        var request = chatClient.Requests[0];
        Assert.Contains(request, message => message.Role == ChatRole.User && message.Text == "[U234]: Earlier channel message");
        Assert.Contains(request, message => message.Role == ChatRole.User && message.Text == "[U123]: summarize the channel");

        var logLines = File.ReadAllLines(Path.Combine(_workspaceDirectory, "C123", "log.jsonl"));
        Assert.Equal(3, logLines.Length);
        Assert.Contains("Earlier channel message", logLines[0]);
        Assert.Contains("summarize the channel", logLines[1]);
        Assert.Contains("ack", logLines[2]);
    }

    [Fact]
    public async Task DispatchAsync_UsesResolvedUserNamesInSyncedContext()
    {
        var chatClient = new FakeChatClient(
            [
                CreateUpdate(new TextContent("ack"), ChatFinishReason.Stop),
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

        var workspaceIndex = new MomSlackWorkspaceIndex(
            users:
            [
                new SlackUserInfo("U234", "alice", "Alice Example"),
                new SlackUserInfo("U123", "bob", "Bob Example"),
            ],
            channels:
            [
                new SlackChannelInfo("C123", "general"),
            ]);

        using var store = new MomChannelStore(_workspaceDirectory, workspaceIndex: workspaceIndex);
        var turnProcessor = new MomTurnProcessor(
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
        var runtime = new MomWorkspaceRuntime(turnProcessor, slackClient, store);

        await runtime.DispatchAsync(new SlackIncomingEvent(
            "C123",
            "U234",
            "Earlier channel message",
            "12345.1000",
            "message",
            IsDirectMessage: false,
            RequiresResponse: false));

        await runtime.DispatchAsync(new SlackIncomingEvent(
            "C123",
            "U123",
            "<@B123> summarize the channel",
            "12345.2000",
            "app_mention",
            IsDirectMessage: false));

        await runtime.WaitForIdleAsync("C123");

        var request = Assert.Single(chatClient.Requests);
        Assert.Contains(request, message => message.Role == ChatRole.User && message.Text == "[alice]: Earlier channel message");
        Assert.Contains(request, message => message.Role == ChatRole.User && message.Text == "[bob]: summarize the channel");
    }

    [Fact]
    public async Task DispatchAsync_RefreshesSlackMetadataBeforeLoggingAndPrompting()
    {
        var chatClient = new FakeChatClient(
            [
                CreateUpdate(new TextContent("ack"), ChatFinishReason.Stop),
            ]);

        var slackClient = new FakeSlackMessagingClient();
        var metadataClient = new FakeSlackWorkspaceMetadataClient();
        metadataClient.EnqueueSnapshot(
            users:
            [
                new SlackUserInfo("U234", "alice", "Alice Example"),
                new SlackUserInfo("U123", "bob", "Bob Example"),
            ],
            channels:
            [
                new SlackChannelInfo("D123", "DM:alice"),
            ]);

        var environment = new MomConsoleEnvironment(
            new StringReader(string.Empty),
            new StringWriter(),
            new StringWriter(),
            _workspaceDirectory,
            new Dictionary<string, string?>
            {
                ["OPENAI_API_KEY"] = "env-key",
            });

        var workspaceIndex = new MomSlackWorkspaceIndex();
        using var metadataService = new MomSlackMetadataService(metadataClient, workspaceIndex);
        using var store = new MomChannelStore(_workspaceDirectory, workspaceIndex: workspaceIndex);
        var turnProcessor = new MomTurnProcessor(
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
        var runtime = new MomWorkspaceRuntime(turnProcessor, slackClient, store, metadataService);

        await runtime.DispatchAsync(new SlackIncomingEvent(
            "D123",
            "U234",
            "Earlier direct message",
            "12345.1000",
            "message",
            IsDirectMessage: true,
            RequiresResponse: false));

        await runtime.DispatchAsync(new SlackIncomingEvent(
            "D123",
            "U123",
            "summarize the DM",
            "12345.2000",
            "message",
            IsDirectMessage: true));

        await runtime.WaitForIdleAsync("D123");

        var loggedMessage = store.ReadLoggedMessage("D123", "12345.1000");
        Assert.Equal("alice", loggedMessage?.UserName);
        Assert.Equal("Alice Example", loggedMessage?.DisplayName);

        var request = Assert.Single(chatClient.Requests);
        Assert.Contains(request, message => message.Role == ChatRole.User && message.Text == "[alice]: Earlier direct message");
        Assert.Contains(request, message => message.Role == ChatRole.User && message.Text == "[bob]: summarize the DM");
        Assert.Equal(1, metadataClient.GetUsersCallCount);
        Assert.Equal(1, metadataClient.GetChannelsCallCount);
    }

    [Fact]
    public async Task DispatchAsync_SyncsLoggedAttachmentsIntoNextTurn()
    {
        var chatClient = new FakeChatClient(
            [
                CreateUpdate(new TextContent("ack"), ChatFinishReason.Stop),
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
                Content = new StringContent("attachment body"),
            }));

        using var store = new MomChannelStore(_workspaceDirectory, "xoxb-test", httpClient);
        var turnProcessor = new MomTurnProcessor(
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
        var runtime = new MomWorkspaceRuntime(turnProcessor, slackClient, store);

        await runtime.DispatchAsync(new SlackIncomingEvent(
            "C123",
            "U234",
            string.Empty,
            "12345.1000",
            "message",
            IsDirectMessage: false,
            Files:
            [
                new SlackFileReference("notes.txt", "https://example.com/notes.txt"),
            ],
            RequiresResponse: false));

        await runtime.DispatchAsync(new SlackIncomingEvent(
            "C123",
            "U123",
            "<@B123> summarize attachments",
            "12345.2000",
            "app_mention",
            IsDirectMessage: false));

        await runtime.WaitForIdleAsync("C123");

        Assert.Single(chatClient.Requests);
        var request = chatClient.Requests[0];
        Assert.Contains(request, message =>
            message.Role == ChatRole.User &&
            message.Text is not null &&
            message.Text.Contains("[U234]: shared attachments", StringComparison.Ordinal) &&
            message.Text.Contains("notes.txt => attachments/12345100_notes.txt", StringComparison.Ordinal));
        Assert.Contains(request, message => message.Role == ChatRole.User && message.Text == "[U123]: summarize attachments");

        var attachmentPath = Path.Combine(_workspaceDirectory, "C123", "attachments", "12345100_notes.txt");
        Assert.True(File.Exists(attachmentPath));
    }

    [Fact]
    public async Task DispatchAsync_BackfillsRecentHistoryForFirstSeenChannel()
    {
        var chatClient = new FakeChatClient(
            [
                CreateUpdate(new TextContent("ack"), ChatFinishReason.Stop),
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

        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/conversations.history", StringComparison.Ordinal) == true)
            {
                var response =
                    """
                    {
                      "ok": true,
                      "messages": [
                        {
                          "user": "U234",
                          "text": "Earlier missing channel message",
                          "ts": "12345.1000"
                        }
                      ],
                      "response_metadata": {
                        "next_cursor": ""
                      }
                    }
                    """;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json"),
                };
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }));

        using var historyClient = new SlackWebApiClient("xoxb-test", httpClient);
        using var store = new MomChannelStore(_workspaceDirectory, "xoxb-test", httpClient);
        var backfiller = new MomLogBackfiller(historyClient, store);
        var turnProcessor = new MomTurnProcessor(
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
        var runtime = new MomWorkspaceRuntime(
            turnProcessor,
            slackClient,
            store,
            backfiller: backfiller,
            botUserId: "B123");

        await runtime.DispatchAsync(new SlackIncomingEvent(
            "C123",
            "U123",
            "<@B123> summarize the channel",
            "12345.2000",
            "app_mention",
            IsDirectMessage: false));

        await runtime.WaitForIdleAsync("C123");

        var request = Assert.Single(chatClient.Requests);
        Assert.Contains(request, message => message.Role == ChatRole.User && message.Text == "[U234]: Earlier missing channel message");
        Assert.Contains(request, message => message.Role == ChatRole.User && message.Text == "[U123]: summarize the channel");

        var logLines = File.ReadAllLines(Path.Combine(_workspaceDirectory, "C123", "log.jsonl"));
        Assert.Equal(3, logLines.Length);
        Assert.Contains("Earlier missing channel message", logLines[0]);
        Assert.Contains("summarize the channel", logLines[1]);
        Assert.Contains("ack", logLines[2]);
    }

    [Fact]
    public async Task DispatchAsync_BackfillsReconnectGapForExistingChannel()
    {
        var chatClient = new FakeChatClient(
            [
                CreateUpdate(new TextContent("ack"), ChatFinishReason.Stop),
            ]);

        var slackClient = new FakeSlackMessagingClient();
        var historyRequestCount = 0;
        var environment = new MomConsoleEnvironment(
            new StringReader(string.Empty),
            new StringWriter(),
            new StringWriter(),
            _workspaceDirectory,
            new Dictionary<string, string?>
            {
                ["OPENAI_API_KEY"] = "env-key",
            });

        using var httpClient = new HttpClient(new StubHttpMessageHandler(request =>
        {
            if (request.RequestUri?.AbsolutePath.EndsWith("/conversations.history", StringComparison.Ordinal) == true)
            {
                historyRequestCount++;
                var response =
                    """
                    {
                      "ok": true,
                      "messages": [
                        {
                          "user": "U234",
                          "text": "Missed during reconnect",
                          "ts": "12345.2000"
                        }
                      ],
                      "response_metadata": {
                        "next_cursor": ""
                      }
                    }
                    """;

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(response, System.Text.Encoding.UTF8, "application/json"),
                };
            }

            throw new InvalidOperationException($"Unexpected request: {request.RequestUri}");
        }));

        using var historyClient = new SlackWebApiClient("xoxb-test", httpClient);
        using var store = new MomChannelStore(_workspaceDirectory, "xoxb-test", httpClient);
        await store.LogIncomingEventAsync(new SlackIncomingEvent(
            "C123",
            "U999",
            "Existing local context",
            "12345.1000",
            "message",
            IsDirectMessage: false,
            RequiresResponse: false));

        var backfiller = new MomLogBackfiller(historyClient, store);
        var turnProcessor = new MomTurnProcessor(
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
        var runtime = new MomWorkspaceRuntime(
            turnProcessor,
            slackClient,
            store,
            backfiller: backfiller,
            botUserId: "B123");

        await runtime.DispatchAsync(new SlackIncomingEvent(
            "C123",
            "U123",
            "<@B123> summarize reconnect gap",
            "12345.3000",
            "app_mention",
            IsDirectMessage: false,
            ConnectionGeneration: 1));

        await runtime.WaitForIdleAsync("C123");

        Assert.Equal(1, historyRequestCount);
        var request = Assert.Single(chatClient.Requests);
        Assert.Contains(request, message => message.Role == ChatRole.User && message.Text == "[U999]: Existing local context");
        Assert.Contains(request, message => message.Role == ChatRole.User && message.Text == "[U234]: Missed during reconnect");
        Assert.Contains(request, message => message.Role == ChatRole.User && message.Text == "[U123]: summarize reconnect gap");

        var logLines = File.ReadAllLines(Path.Combine(_workspaceDirectory, "C123", "log.jsonl"));
        Assert.Equal(4, logLines.Length);
        Assert.Contains("Existing local context", logLines[0]);
        Assert.Contains("Missed during reconnect", logLines[1]);
        Assert.Contains("summarize reconnect gap", logLines[2]);
        Assert.Contains("ack", logLines[3]);
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
