using PiSharp.Ai;
using PiSharp.Cli.Tests.Support;
using PiSharp.CodingAgent;

namespace PiSharp.Cli.Tests;

public sealed class ProviderModelDiscoveryServiceTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-model-discovery-{Guid.NewGuid():N}");

    [Fact]
    public async Task ListProviderModelsAsync_MergesRemoteModelsAndCachesResults()
    {
        Directory.CreateDirectory(_rootDirectory);
        var provider = CreateOpenAiProvider();
        var handler = new QueueHttpMessageHandler(
        [
            (_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"gpt-4.1-mini"},{"id":"gpt-5-mini"},{"id":"text-embedding-3-large"}]}"""),
            },
        ]);
        var service = new ProviderModelDiscoveryService(
            Path.Combine(_rootDirectory, "cache"),
            new HttpClient(handler),
            new ManualTimeProvider(new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero)));

        var result = await service.ListProviderModelsAsync(provider, "test-key");

        Assert.Contains(result.Models, static model => model.Id == "gpt-4.1-mini");
        var discovered = Assert.Single(result.Models, static model => model.Id == "gpt-5-mini");
        Assert.Equal("gpt-5-mini", discovered.Name);
        Assert.Equal(provider.DefaultContextWindow, discovered.ContextWindow);
        Assert.Equal(provider.DefaultMaxOutputTokens, discovered.MaxOutputTokens);
        Assert.DoesNotContain(result.Models, static model => model.Id == "text-embedding-3-large");
        Assert.True(File.Exists(Path.Combine(_rootDirectory, "cache", "openai.json")));
    }

    [Fact]
    public async Task ListProviderModelsAsync_UsesFreshCacheBeforeNetwork()
    {
        Directory.CreateDirectory(_rootDirectory);
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero));
        var handler = new QueueHttpMessageHandler(
        [
            (_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent("""{"data":[{"id":"gpt-5-mini"}]}"""),
            },
            (_, _) => throw new InvalidOperationException("network should not be called"),
        ]);
        var service = new ProviderModelDiscoveryService(
            Path.Combine(_rootDirectory, "cache"),
            new HttpClient(handler),
            timeProvider);
        var provider = CreateOpenAiProvider();

        var first = await service.ListProviderModelsAsync(provider, "test-key");
        var second = await service.ListProviderModelsAsync(provider, "test-key");

        Assert.Contains(first.Models, static model => model.Id == "gpt-5-mini");
        Assert.Contains(second.Models, static model => model.Id == "gpt-5-mini");
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task ListProviderModelsAsync_UsesCachedResultsWhenRefreshFails()
    {
        Directory.CreateDirectory(_rootDirectory);
        var timeProvider = new ManualTimeProvider(new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero));
        var cacheDirectory = Path.Combine(_rootDirectory, "cache");
        var provider = CreateOpenAiProvider();

        var seedService = new ProviderModelDiscoveryService(
            cacheDirectory,
            new HttpClient(new QueueHttpMessageHandler(
            [
                (_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[{"id":"gpt-5-mini"}]}"""),
                },
            ])),
            timeProvider);

        await seedService.ListProviderModelsAsync(provider, "test-key");

        timeProvider.Advance(ProviderModelDiscoveryService.CacheTtl + TimeSpan.FromSeconds(1));

        var failingService = new ProviderModelDiscoveryService(
            cacheDirectory,
            new HttpClient(new QueueHttpMessageHandler(
            [
                (_, _) => new HttpResponseMessage(System.Net.HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("boom"),
                },
            ])),
            timeProvider);

        var result = await failingService.ListProviderModelsAsync(provider, "test-key");

        Assert.Contains(result.Models, static model => model.Id == "gpt-5-mini");
        Assert.Contains("using cached results", result.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ListProviderModelsAsync_ParsesAnthropicModels()
    {
        Directory.CreateDirectory(_rootDirectory);
        var handler = new QueueHttpMessageHandler(
        [
            (request, _) =>
            {
                Assert.Equal("https://api.anthropic.com/v1/models", request.RequestUri?.ToString());
                Assert.Equal("test-key", Assert.Single(request.Headers.GetValues("x-api-key")));
                Assert.Equal("2023-06-01", Assert.Single(request.Headers.GetValues("anthropic-version")));
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"data":[{"id":"claude-sonnet-4-5","display_name":"Claude Sonnet 4.5"}]}"""),
                };
            },
        ]);
        var service = new ProviderModelDiscoveryService(
            Path.Combine(_rootDirectory, "cache"),
            new HttpClient(handler),
            new ManualTimeProvider(new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero)));

        var result = await service.ListProviderModelsAsync(CreateAnthropicProvider(), "test-key");

        var model = Assert.Single(result.Models, static model => model.Id == "claude-sonnet-4-5");
        Assert.Equal("Claude Sonnet 4.5", model.Name);
    }

    [Fact]
    public async Task ListProviderModelsAsync_ParsesGoogleModelsAndFiltersNonGenerateContentEntries()
    {
        Directory.CreateDirectory(_rootDirectory);
        var handler = new QueueHttpMessageHandler(
        [
            (request, _) =>
            {
                Assert.Contains("https://generativelanguage.googleapis.com/v1beta/models", request.RequestUri?.ToString());
                Assert.Contains("key=test-key", request.RequestUri?.Query);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """
                        {
                          "models": [
                            {
                              "name": "models/gemini-2.5-pro",
                              "displayName": "Gemini 2.5 Pro",
                              "baseModelId": "gemini-2.5-pro",
                              "inputTokenLimit": 1048576,
                              "outputTokenLimit": 65536,
                              "supportedGenerationMethods": ["generateContent", "countTokens"]
                            },
                            {
                              "name": "models/text-embedding-004",
                              "displayName": "Embedding 004",
                              "baseModelId": "text-embedding-004",
                              "supportedGenerationMethods": ["embedContent"]
                            }
                          ]
                        }
                        """),
                };
            },
        ]);
        var service = new ProviderModelDiscoveryService(
            Path.Combine(_rootDirectory, "cache"),
            new HttpClient(handler),
            new ManualTimeProvider(new DateTimeOffset(2026, 4, 16, 0, 0, 0, TimeSpan.Zero)));

        var result = await service.ListProviderModelsAsync(CreateGoogleProvider(), "test-key");

        var model = Assert.Single(result.Models, static model => model.Id == "gemini-2.5-pro");
        Assert.Equal("Gemini 2.5 Pro", model.Name);
        Assert.Equal(1_048_576, model.ContextWindow);
        Assert.Equal(65_536, model.MaxOutputTokens);
        Assert.DoesNotContain(result.Models, static model => model.Id == "text-embedding-004");
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private static CodingAgentProviderFactory CreateOpenAiProvider() =>
        new()
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
        };

    private static CodingAgentProviderFactory CreateAnthropicProvider() =>
        new()
        {
            Configuration = new ProviderConfiguration(
                ProviderId.Anthropic,
                ApiId.Anthropic,
                "Anthropic",
                DefaultModelId: "claude-3-7-sonnet-latest",
                ApiKeyEnvironmentVariable: "ANTHROPIC_API_KEY"),
            KnownModels = [],
            CreateChatClient = (_, _) => throw new NotSupportedException(),
        };

    private static CodingAgentProviderFactory CreateGoogleProvider() =>
        new()
        {
            Configuration = new ProviderConfiguration(
                ProviderId.Google,
                ApiId.Google,
                "Google",
                DefaultModelId: "gemini-2.0-flash",
                ApiKeyEnvironmentVariable: "GOOGLE_API_KEY"),
            KnownModels = [],
            CreateChatClient = (_, _) => throw new NotSupportedException(),
        };

    private sealed class QueueHttpMessageHandler(
        IEnumerable<Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>> responses) : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, CancellationToken, HttpResponseMessage>> _responses = new(responses);

        public int CallCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            if (_responses.Count == 0)
            {
                throw new InvalidOperationException("No queued HTTP response.");
            }

            return Task.FromResult(_responses.Dequeue()(request, cancellationToken));
        }
    }
}
