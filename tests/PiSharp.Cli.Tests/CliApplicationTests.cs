using System.Diagnostics;
using Microsoft.Extensions.AI;
using PiSharp.Ai;
using PiSharp.CodingAgent;
using PiSharp.Tui;
using PiSharp.Cli.Tests.Support;

namespace PiSharp.Cli.Tests;

public sealed class CliApplicationTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-cli-app-{Guid.NewGuid():N}");

    [Fact]
    public void GetHelpText_DescribesMomStatsChannelSelectors()
    {
        var help = CliArgumentsParser.GetHelpText();

        Assert.Contains("mom stats [--json] [--channel <id|name>] <workspace-directory>", help);
        Assert.Contains("mom stats --channel general ./mom-data", help);
        Assert.Contains("--otel-endpoint <url>", help);
    }

    [Fact]
    public async Task RunAsync_DelegatesPodsNamespaceCommands()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        IReadOnlyList<string>? forwardedArgs = null;

        var application = new CliApplication(
            new CliEnvironment(
                new StringReader(string.Empty),
                output,
                error,
                _rootDirectory,
                isInputRedirected: false),
            runPodsCommand: (args, _) =>
            {
                forwardedArgs = args.ToArray();
                return Task.FromResult(0);
            });

        var exitCode = await application.RunAsync(["pods", "start", "Qwen/Qwen2.5-Coder-32B-Instruct", "--name", "qwen"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["start", "Qwen/Qwen2.5-Coder-32B-Instruct", "--name", "qwen"], forwardedArgs);
    }

    [Fact]
    public async Task RunAsync_DelegatesPodsRootCommand()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        IReadOnlyList<string>? forwardedArgs = null;

        var application = new CliApplication(
            new CliEnvironment(
                new StringReader(string.Empty),
                output,
                error,
                _rootDirectory,
                isInputRedirected: false),
            runPodsCommand: (args, _) =>
            {
                forwardedArgs = args.ToArray();
                return Task.FromResult(0);
            });

        var exitCode = await application.RunAsync(["pods"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["pods"], forwardedArgs);
    }

    [Fact]
    public async Task RunAsync_DelegatesMomNamespaceCommands()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        IReadOnlyList<string>? forwardedArgs = null;

        var application = new CliApplication(
            new CliEnvironment(
                new StringReader(string.Empty),
                output,
                error,
                _rootDirectory,
                isInputRedirected: false),
            runMomCommand: (args, _) =>
            {
                forwardedArgs = args.ToArray();
                return Task.FromResult(0);
            });

        var exitCode = await application.RunAsync(["mom", "./mom-data", "--provider", "anthropic"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["./mom-data", "--provider", "anthropic"], forwardedArgs);
    }

    [Fact]
    public async Task RunAsync_DelegatesMomStatsNamespaceCommands()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        IReadOnlyList<string>? forwardedArgs = null;

        var application = new CliApplication(
            new CliEnvironment(
                new StringReader(string.Empty),
                output,
                error,
                _rootDirectory,
                isInputRedirected: false),
            runMomCommand: (args, _) =>
            {
                forwardedArgs = args.ToArray();
                return Task.FromResult(0);
            });

        var exitCode = await application.RunAsync(["mom", "stats", "--json", "--channel", "C123", "./mom-data"]);

        Assert.Equal(0, exitCode);
        Assert.Equal(["stats", "--json", "--channel", "C123", "./mom-data"], forwardedArgs);
    }

    [Fact]
    public async Task RunAsync_PrintsAssistantTextAndInjectsContextFilesIntoPrompt()
    {
        var repoDirectory = Path.Combine(_rootDirectory, "repo");
        Directory.CreateDirectory(repoDirectory);
        File.WriteAllText(Path.Combine(repoDirectory, "AGENTS.md"), "Repository rules.");

        var output = new StringWriter();
        var error = new StringWriter();
        var fakeClient = new FakeChatClient(
            [
                CreateUpdate(new TextContent("done"), ChatFinishReason.Stop),
            ]);

        var providerCatalog = new CodingAgentProviderCatalog(
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
                    CreateChatClient = (_, _) => fakeClient,
                },
            ]);

        var environment = new CliEnvironment(
            new StringReader(string.Empty),
            output,
            error,
            repoDirectory,
            isInputRedirected: false,
            environmentVariables: new Dictionary<string, string?>
            {
                ["OPENAI_API_KEY"] = "test-key",
            });

        var application = new CliApplication(environment, providerCatalog);

        var exitCode = await application.RunAsync(["Summarize", "this", "repo"]);

        Assert.Equal(0, exitCode);
        Assert.Equal($"done{Environment.NewLine}", output.ToString());
        Assert.Contains("Repository rules.", fakeClient.Options[0].Instructions);
        Assert.Equal(4, fakeClient.Options[0].Tools!.Count);
    }

    [Fact]
    public async Task RunAsync_LoadsImageFileArgumentsAsDataContent()
    {
        var repoDirectory = Path.Combine(_rootDirectory, "image-repo");
        Directory.CreateDirectory(repoDirectory);
        File.WriteAllText(Path.Combine(repoDirectory, "notes.txt"), "Release notes.");
        File.WriteAllBytes(Path.Combine(repoDirectory, "diagram.png"), [0x89, 0x50, 0x4E, 0x47]);

        var output = new StringWriter();
        var error = new StringWriter();
        var fakeClient = new FakeChatClient(
            [
                CreateUpdate(new TextContent("done"), ChatFinishReason.Stop),
            ]);

        var environment = new CliEnvironment(
            new StringReader(string.Empty),
            output,
            error,
            repoDirectory,
            isInputRedirected: false,
            environmentVariables: new Dictionary<string, string?>
            {
                ["OPENAI_API_KEY"] = "test-key",
            });

        var application = new CliApplication(environment, CreateProviderCatalog(fakeClient));

        var exitCode = await application.RunAsync(["@notes.txt", "@diagram.png", "Describe", "this", "image"]);

        Assert.Equal(0, exitCode);

        var userMessage = Assert.Single(fakeClient.Requests[0].Where(message => message.Role == ChatRole.User));
        Assert.Collection(
            userMessage.Contents,
            content =>
            {
                var text = Assert.IsType<TextContent>(content);
                Assert.Contains("# File: notes.txt", text.Text);
                Assert.Contains("Release notes.", text.Text);
            },
            content =>
            {
                var image = Assert.IsType<DataContent>(content);
                Assert.Equal("image/png", image.MediaType);
                Assert.Equal("diagram.png", image.Name);
            },
            content =>
            {
                var text = Assert.IsType<TextContent>(content);
                Assert.Equal("Describe this image", text.Text);
            });
    }

    [Fact]
    public async Task RunAsync_CreatesOpenTelemetryActivitiesWithoutExporterEndpoint()
    {
        var repoDirectory = Path.Combine(_rootDirectory, "otel-repo");
        Directory.CreateDirectory(repoDirectory);

        var output = new StringWriter();
        var error = new StringWriter();
        var fakeClient = new FakeChatClient(
            [
                CreateUpdate(new TextContent("done"), ChatFinishReason.Stop),
            ]);

        var environment = new CliEnvironment(
            new StringReader(string.Empty),
            output,
            error,
            repoDirectory,
            isInputRedirected: false,
            environmentVariables: new Dictionary<string, string?>
            {
                ["OPENAI_API_KEY"] = "test-key",
            });

        var activities = new List<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = static source => source.Name == "PiSharp.Cli.ChatClient",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activities.Add,
        };

        ActivitySource.AddActivityListener(listener);

        var application = new CliApplication(environment, CreateProviderCatalog(fakeClient));
        var exitCode = await application.RunAsync(["hello"]);

        Assert.Equal(0, exitCode);
        Assert.NotEmpty(activities);
    }

    [Fact]
    public async Task RunAsync_ListModels_PrintsKnownModels()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var providerCatalog = new CodingAgentProviderCatalog(
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
                    CreateChatClient = (_, _) => throw new NotSupportedException(),
                },
            ]);

        var environment = new CliEnvironment(
            new StringReader(string.Empty),
            output,
            error,
            _rootDirectory,
            isInputRedirected: false);

        var application = new CliApplication(environment, providerCatalog);

        var exitCode = await application.RunAsync(["--list-models"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("provider", output.ToString());
        Assert.Contains("gpt-4.1-mini", output.ToString());
    }

    [Fact]
    public async Task RunAsync_ListModels_PrintsDiscoveredRemoteModels()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var providerCatalog = new CodingAgentProviderCatalog(
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
                    CreateChatClient = (_, _) => throw new NotSupportedException(),
                },
            ]);

        var environment = new CliEnvironment(
            new StringReader(string.Empty),
            output,
            error,
            _rootDirectory,
            isInputRedirected: false,
            environmentVariables: new Dictionary<string, string?>
            {
                ["OPENAI_API_KEY"] = "test-key",
            });

        var discoveryService = new ProviderModelDiscoveryService(
            Path.Combine(_rootDirectory, "model-cache"),
            new HttpClient(new StubHttpMessageHandler((request, _) =>
            {
                Assert.Equal("https://api.openai.com/v1/models", request.RequestUri?.ToString());
                Assert.Equal("Bearer", request.Headers.Authorization?.Scheme);
                Assert.Equal("test-key", request.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[{"id":"gpt-4.1-mini"},{"id":"gpt-5-mini"}]}"""),
                };
            })),
            new ManualTimeProvider(new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero)));

        var application = new CliApplication(environment, providerCatalog, discoveryService);

        var exitCode = await application.RunAsync(["--list-models"]);

        Assert.Equal(0, exitCode);
        Assert.Contains("gpt-4.1-mini", output.ToString());
        Assert.Contains("gpt-5-mini", output.ToString());
        Assert.DoesNotContain("Warning:", error.ToString());
    }

    [Fact]
    public async Task RunAsync_ResumesPersistedSessionFromId()
    {
        var repoDirectory = Path.Combine(_rootDirectory, "resume-repo");
        Directory.CreateDirectory(repoDirectory);

        var output = new StringWriter();
        var error = new StringWriter();
        var fakeClient = new FakeChatClient(
            [CreateUpdate(new TextContent("first"), ChatFinishReason.Stop)],
            [CreateUpdate(new TextContent("second"), ChatFinishReason.Stop)]);

        var environment = new CliEnvironment(
            new StringReader(string.Empty),
            output,
            error,
            repoDirectory,
            isInputRedirected: false,
            environmentVariables: new Dictionary<string, string?>
            {
                ["OPENAI_API_KEY"] = "test-key",
            });

        var application = new CliApplication(environment, CreateProviderCatalog(fakeClient));

        var firstExitCode = await application.RunAsync(["hello"]);
        Assert.Equal(0, firstExitCode);

        var sessionDirectory = Path.Combine(repoDirectory, ".pi-sharp", "sessions");
        var sessionFile = Assert.Single(Directory.GetFiles(sessionDirectory, "*.jsonl"));

        var manager = new SessionManager(sessionDirectory, repoDirectory);
        await manager.LoadSessionAsync(sessionFile);

        output.GetStringBuilder().Clear();
        var secondExitCode = await application.RunAsync(["--resume", manager.Header!.Id, "again"]);

        Assert.Equal(0, secondExitCode);
        Assert.Equal(2, fakeClient.Requests.Count);
        Assert.Contains(fakeClient.Requests[1], message => message.Role == ChatRole.User && message.Text == "hello");
        Assert.Contains(fakeClient.Requests[1], message => message.Role == ChatRole.Assistant && message.Text == "first");
    }

    [Fact]
    public async Task RunAsync_ForksPersistedSessionIntoNewFile()
    {
        var repoDirectory = Path.Combine(_rootDirectory, "fork-repo");
        Directory.CreateDirectory(repoDirectory);

        var output = new StringWriter();
        var error = new StringWriter();
        var fakeClient = new FakeChatClient(
            [CreateUpdate(new TextContent("root"), ChatFinishReason.Stop)],
            [CreateUpdate(new TextContent("branch"), ChatFinishReason.Stop)]);

        var environment = new CliEnvironment(
            new StringReader(string.Empty),
            output,
            error,
            repoDirectory,
            isInputRedirected: false,
            environmentVariables: new Dictionary<string, string?>
            {
                ["OPENAI_API_KEY"] = "test-key",
            });

        var application = new CliApplication(environment, CreateProviderCatalog(fakeClient));

        Assert.Equal(0, await application.RunAsync(["seed"]));

        var sessionDirectory = Path.Combine(repoDirectory, ".pi-sharp", "sessions");
        var originalSessionFile = Assert.Single(Directory.GetFiles(sessionDirectory, "*.jsonl"));
        var sourceManager = new SessionManager(sessionDirectory, repoDirectory);
        await sourceManager.LoadSessionAsync(originalSessionFile);

        Assert.Equal(0, await application.RunAsync(["--fork", sourceManager.Header!.Id, "branch"]));

        var sessionFiles = Directory.GetFiles(sessionDirectory, "*.jsonl");
        Assert.Equal(2, sessionFiles.Length);

        var forkedSessionFile = sessionFiles.Single(path => !string.Equals(path, originalSessionFile, StringComparison.Ordinal));
        var forkedManager = new SessionManager(sessionDirectory, repoDirectory);
        await forkedManager.LoadSessionAsync(forkedSessionFile);

        Assert.Equal(sourceManager.Header.Id, forkedManager.Header!.ParentSession);

        var context = forkedManager.BuildContext();
        Assert.Contains(context.Messages, message => message.Role == ChatRole.User && message.Text == "seed");
        Assert.Contains(context.Messages, message => message.Role == ChatRole.Assistant && message.Text == "root");
        Assert.Contains(context.Messages, message => message.Role == ChatRole.User && message.Text == "branch");
    }

    [Fact]
    public async Task RunAsync_StartsInteractiveModeWhenTerminalHasNoInitialPrompt()
    {
        var repoDirectory = Path.Combine(_rootDirectory, "interactive-repo");
        Directory.CreateDirectory(repoDirectory);

        var terminal = new FakeTerminal(80, 12);
        var output = new StringWriter();
        var error = new StringWriter();
        var keys = new Queue<ConsoleKeyInfo>(
        [
            new ConsoleKeyInfo('h', ConsoleKey.H, false, false, false),
            new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false),
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
            new ConsoleKeyInfo('/', ConsoleKey.Oem2, false, false, false),
            new ConsoleKeyInfo('e', ConsoleKey.E, false, false, false),
            new ConsoleKeyInfo('x', ConsoleKey.X, false, false, false),
            new ConsoleKeyInfo('i', ConsoleKey.I, false, false, false),
            new ConsoleKeyInfo('t', ConsoleKey.T, false, false, false),
            new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false),
        ]);

        var fakeClient = new FakeChatClient(
            [
                CreateUpdate(new TextContent("he")),
                CreateUpdate(new TextContent("llo"), ChatFinishReason.Stop),
            ]);

        var environment = new CliEnvironment(
            new StringReader(string.Empty),
            output,
            error,
            repoDirectory,
            isInputRedirected: false,
            isOutputRedirected: false,
            environmentVariables: new Dictionary<string, string?>
            {
                ["OPENAI_API_KEY"] = "test-key",
            },
            terminal: terminal,
            readKey: _ => keys.Dequeue());

        var application = new CliApplication(environment, CreateProviderCatalog(fakeClient));

        var exitCode = await application.RunAsync(Array.Empty<string>());

        Assert.Equal(0, exitCode);
        Assert.Single(fakeClient.Requests);
        Assert.Contains(terminal.Writes, write => write.Contains("PiSharp", StringComparison.Ordinal));
        Assert.Contains(terminal.Writes, write => write.Contains("Assistant> hello", StringComparison.Ordinal));
    }

    [Fact]
    public async Task RunAsync_ReturnsErrorForMissingResumeSession()
    {
        var repoDirectory = Path.Combine(_rootDirectory, "missing-resume-repo");
        Directory.CreateDirectory(repoDirectory);

        var output = new StringWriter();
        var error = new StringWriter();
        var environment = new CliEnvironment(
            new StringReader(string.Empty),
            output,
            error,
            repoDirectory,
            isInputRedirected: false,
            environmentVariables: new Dictionary<string, string?>
            {
                ["OPENAI_API_KEY"] = "test-key",
            });

        var application = new CliApplication(environment, CreateProviderCatalog(new FakeChatClient()));
        var exitCode = await application.RunAsync(["--resume", "missing", "hello"]);

        Assert.Equal(1, exitCode);
        Assert.Contains("not found", error.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static CodingAgentProviderCatalog CreateProviderCatalog(FakeChatClient fakeClient) =>
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
                CreateChatClient = (_, _) => fakeClient,
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
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private sealed class FakeTerminal(int columns, int rows) : ITerminal
    {
        public TerminalSize Size { get; } = new(columns, rows);

        public List<string> Writes { get; } = [];

        public ValueTask WriteAsync(string output, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Writes.Add(output);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(handler(request, cancellationToken));
    }
}
