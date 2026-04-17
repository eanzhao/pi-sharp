using Microsoft.Extensions.AI;
using PiSharp.Agent;
using PiSharp.Ai;
using PiSharp.CodingAgent;

namespace PiSharp.CodingAgent.Tests;

public sealed class CodingAgentRuntimeBootstrapTests : IDisposable
{
    private readonly string _rootDirectory = Path.Combine(Path.GetTempPath(), $"pisharp-bootstrap-{Guid.NewGuid():N}");

    [Fact]
    public void Resolve_UsesSessionMetadataAndEnvironmentApiKey()
    {
        var settingsManager = SettingsManager.InMemory(
            new CodingAgentSettings
            {
                DefaultProvider = "google",
                DefaultModel = "gemini-2.0-flash",
                DefaultThinkingLevel = "minimal",
            });
        var bootstrap = new CodingAgentRuntimeBootstrap(CreateProviderCatalog(), settingsManager);

        var configuration = bootstrap.Resolve(
            new CodingAgentBootstrapRequest
            {
                WorkingDirectory = _rootDirectory,
                ExistingSession = new SessionContext(
                    Array.Empty<ChatMessage>(),
                    "high",
                    "anthropic",
                    "claude-3-7-sonnet-latest",
                    null,
                    Array.Empty<string>()),
            },
            name => name switch
            {
                "ANTHROPIC_API_KEY" => "anthropic-key",
                _ => null,
            });

        Assert.Equal("anthropic", configuration.ProviderFactory.Configuration.ProviderId.Value);
        Assert.Equal("claude-3-7-sonnet-latest", configuration.Model.Id);
        Assert.Equal(ThinkingLevel.High, configuration.ThinkingLevel);
        Assert.Equal("anthropic-key", configuration.ApiKey);
    }

    [Fact]
    public void Resolve_LoadsContextFilesAndSessionDirectory()
    {
        var repoDirectory = Path.Combine(_rootDirectory, "repo");
        var nestedDirectory = Path.Combine(repoDirectory, "src", "feature");
        Directory.CreateDirectory(nestedDirectory);
        File.WriteAllText(Path.Combine(_rootDirectory, "AGENTS.md"), "global");
        File.WriteAllText(Path.Combine(repoDirectory, "CLAUDE.md"), "repo");

        var settingsManager = SettingsManager.InMemory(
            new CodingAgentSettings
            {
                SessionDir = Path.Combine(_rootDirectory, "custom-sessions"),
            });
        var bootstrap = new CodingAgentRuntimeBootstrap(CreateProviderCatalog(), settingsManager);

        var configuration = bootstrap.Resolve(
            new CodingAgentBootstrapRequest
            {
                WorkingDirectory = nestedDirectory,
            },
            name => name == "OPENAI_API_KEY" ? "openai-key" : null);

        Assert.Equal(Path.Combine(_rootDirectory, "custom-sessions"), configuration.SessionDirectory);
        Assert.Equal(2, configuration.ContextFiles.Count);
        Assert.Equal("global", configuration.ContextFiles[0].Content);
        Assert.Equal("repo", configuration.ContextFiles[1].Content);
    }

    [Fact]
    public void Resolve_UsesEndpointEnvironmentVariableForAzureCompatibleProviders()
    {
        var settingsManager = SettingsManager.InMemory(new CodingAgentSettings());
        var providerCatalog = new CodingAgentProviderCatalog(
        [
            CreateProvider(
                ProviderId.AzureOpenAi,
                ApiId.OpenAi,
                "AZURE_OPENAI_API_KEY",
                "gpt-4.1-mini",
                endpointEnvironmentVariable: "AZURE_OPENAI_ENDPOINT"),
        ]);
        var bootstrap = new CodingAgentRuntimeBootstrap(providerCatalog, settingsManager);

        var configuration = bootstrap.Resolve(
            new CodingAgentBootstrapRequest
            {
                WorkingDirectory = _rootDirectory,
                Provider = "azure-openai",
            },
            name => name switch
            {
                "AZURE_OPENAI_API_KEY" => "azure-key",
                "AZURE_OPENAI_ENDPOINT" => "https://example-resource.openai.azure.com/",
                _ => null,
            });

        Assert.Equal("azure-key", configuration.ApiKey);
        Assert.Equal("https://example-resource.openai.azure.com/", configuration.ProviderEndpoint);
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }
    }

    private static CodingAgentProviderCatalog CreateProviderCatalog() =>
        new(
        [
            CreateProvider(
                ProviderId.OpenAi,
                ApiId.OpenAi,
                "OPENAI_API_KEY",
                "gpt-4.1-mini"),
            CreateProvider(
                ProviderId.Anthropic,
                ApiId.Anthropic,
                "ANTHROPIC_API_KEY",
                "claude-3-7-sonnet-latest"),
            CreateProvider(
                ProviderId.Google,
                ApiId.Google,
                "GOOGLE_API_KEY",
                "gemini-2.0-flash"),
        ]);

    private static CodingAgentProviderFactory CreateProvider(
        ProviderId providerId,
        ApiId apiId,
        string apiKeyEnvironmentVariable,
        string defaultModelId,
        string? endpointEnvironmentVariable = null) =>
        new()
        {
            Configuration = new ProviderConfiguration(
                providerId,
                apiId,
                providerId.Value,
                DefaultModelId: defaultModelId,
                ApiKeyEnvironmentVariable: apiKeyEnvironmentVariable),
            EndpointEnvironmentVariable = endpointEnvironmentVariable,
            KnownModels =
            [
                new ModelMetadata(
                    defaultModelId,
                    defaultModelId,
                    apiId,
                    providerId,
                    1_000_000,
                    32_768,
                    ModelCapability.TextInput | ModelCapability.Streaming | ModelCapability.ToolCalling,
                    ModelPricing.Free),
            ],
            CreateChatClient = static (_, _) => new StubChatClient(),
        };

    private sealed class StubChatClient : IChatClient
    {
        public void Dispose()
        {
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
