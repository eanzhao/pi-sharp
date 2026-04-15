using Anthropic;
using Google.GenAI;
using Microsoft.Extensions.AI;
using OpenAI.Chat;
using PiSharp.Agent;
using PiSharp.Ai;

namespace PiSharp.CodingAgent;

public sealed class CodingAgentProviderFactory
{
    private readonly Func<string, string, IChatClient> _createChatClient =
        static (_, _) => throw new InvalidOperationException("No chat client factory was configured.");

    public required ProviderConfiguration Configuration { get; init; }

    public IReadOnlyList<ModelMetadata> KnownModels { get; init; } = Array.Empty<ModelMetadata>();

    public ModelCapability DefaultCapabilities { get; init; } =
        ModelCapability.TextInput | ModelCapability.Streaming | ModelCapability.ToolCalling;

    public int DefaultContextWindow { get; init; } = 1_000_000;

    public int DefaultMaxOutputTokens { get; init; } = 32_768;

    public ModelPricing DefaultPricing { get; init; } = ModelPricing.Free;

    public required Func<string, string, IChatClient> CreateChatClient
    {
        get => _createChatClient;
        init => _createChatClient = value ?? throw new ArgumentNullException(nameof(value));
    }

    public IChatClient Create(string modelId, string apiKey) => _createChatClient(modelId, apiKey);

    public ModelMetadata ResolveModel(string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        return KnownModels.FirstOrDefault(model => string.Equals(model.Id, modelId, StringComparison.OrdinalIgnoreCase))
            ?? new ModelMetadata(
                modelId,
                modelId,
                Configuration.ApiId,
                Configuration.ProviderId,
                DefaultContextWindow,
                DefaultMaxOutputTokens,
                DefaultCapabilities,
                DefaultPricing);
    }
}

public sealed class CodingAgentProviderCatalog
{
    private readonly Dictionary<string, CodingAgentProviderFactory> _providers;

    public CodingAgentProviderCatalog(IEnumerable<CodingAgentProviderFactory> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = providers.ToDictionary(
            provider => provider.Configuration.ProviderId.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public static CodingAgentProviderCatalog CreateDefault() =>
        new(
        [
            CreateOpenAiProvider(),
            CreateAnthropicProvider(),
            CreateGoogleProvider(),
        ]);

    public IReadOnlyCollection<CodingAgentProviderFactory> GetAll() =>
        _providers.Values.OrderBy(provider => provider.Configuration.ProviderId.Value, StringComparer.OrdinalIgnoreCase).ToArray();

    public bool TryGet(string providerName, out CodingAgentProviderFactory? providerFactory) =>
        _providers.TryGetValue(providerName, out providerFactory);

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
                CreateModel("gpt-4.1-mini", "GPT-4.1 mini", ApiId.OpenAi, ProviderId.OpenAi),
                CreateModel("gpt-4.1", "GPT-4.1", ApiId.OpenAi, ProviderId.OpenAi),
            ],
            CreateChatClient = static (modelId, apiKey) =>
                new ChatClient(modelId, apiKey).AsIChatClient(),
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
            KnownModels =
            [
                CreateModel("claude-3-7-sonnet-latest", "Claude 3.7 Sonnet", ApiId.Anthropic, ProviderId.Anthropic, 200_000, 8_192),
                CreateModel("claude-3-5-haiku-latest", "Claude 3.5 Haiku", ApiId.Anthropic, ProviderId.Anthropic, 200_000, 8_192),
            ],
            CreateChatClient = static (modelId, apiKey) =>
                new AnthropicClient(new Anthropic.Core.ClientOptions { ApiKey = apiKey }).AsIChatClient(modelId),
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
            KnownModels =
            [
                CreateModel("gemini-2.0-flash", "Gemini 2.0 Flash", ApiId.Google, ProviderId.Google),
                CreateModel("gemini-2.0-flash-lite", "Gemini 2.0 Flash-Lite", ApiId.Google, ProviderId.Google),
            ],
            CreateChatClient = static (modelId, apiKey) =>
                new Client(apiKey: apiKey).AsIChatClient(modelId),
        };

    private static ModelMetadata CreateModel(
        string id,
        string name,
        ApiId apiId,
        ProviderId providerId,
        int contextWindow = 1_000_000,
        int maxOutputTokens = 32_768) =>
        new(
            id,
            name,
            apiId,
            providerId,
            contextWindow,
            maxOutputTokens,
            ModelCapability.TextInput | ModelCapability.Streaming | ModelCapability.ToolCalling,
            ModelPricing.Free);
}

public sealed record CodingAgentBootstrapRequest
{
    public required string WorkingDirectory { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }

    public string? ApiKey { get; init; }

    public ThinkingLevel? ThinkingLevel { get; init; }

    public string? SessionDirectory { get; init; }

    public SessionContext? ExistingSession { get; init; }

    public bool LoadContextFiles { get; init; } = true;
}

public sealed record CodingAgentRunConfiguration(
    string WorkingDirectory,
    string SessionDirectory,
    CodingAgentProviderFactory ProviderFactory,
    ModelMetadata Model,
    string ApiKey,
    ThinkingLevel ThinkingLevel,
    IReadOnlyList<CodingAgentContextFile> ContextFiles);

public sealed class CodingAgentRuntimeBootstrap
{
    private static readonly IReadOnlyDictionary<string, ThinkingLevel> ThinkingLevels =
        new Dictionary<string, ThinkingLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["off"] = ThinkingLevel.Off,
            ["minimal"] = ThinkingLevel.Minimal,
            ["low"] = ThinkingLevel.Low,
            ["medium"] = ThinkingLevel.Medium,
            ["high"] = ThinkingLevel.High,
            ["xhigh"] = ThinkingLevel.ExtraHigh,
        };

    public CodingAgentRuntimeBootstrap(
        CodingAgentProviderCatalog providerCatalog,
        SettingsManager settingsManager)
    {
        ProviderCatalog = providerCatalog ?? throw new ArgumentNullException(nameof(providerCatalog));
        SettingsManager = settingsManager ?? throw new ArgumentNullException(nameof(settingsManager));
    }

    public CodingAgentProviderCatalog ProviderCatalog { get; }

    public SettingsManager SettingsManager { get; }

    public CodingAgentRunConfiguration Resolve(
        CodingAgentBootstrapRequest request,
        Func<string, string?> environmentVariableProvider)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(environmentVariableProvider);

        var workingDirectory = Path.GetFullPath(request.WorkingDirectory);
        var sessionDirectory = ResolveSessionDirectory(request.SessionDirectory, workingDirectory);
        var settings = SettingsManager.Settings;
        var sessionContext = request.ExistingSession;

        var providerName =
            request.Provider
            ?? sessionContext?.ProviderId
            ?? settings.DefaultProvider
            ?? ProviderId.OpenAi.Value;

        if (!ProviderCatalog.TryGet(providerName, out var providerFactory) || providerFactory is null)
        {
            throw new InvalidOperationException($"Unknown provider '{providerName}'.");
        }

        var modelId =
            request.Model
            ?? sessionContext?.ModelId
            ?? settings.DefaultModel
            ?? providerFactory.Configuration.DefaultModelId;

        if (string.IsNullOrWhiteSpace(modelId))
        {
            throw new InvalidOperationException(
                $"No model configured for provider '{providerFactory.Configuration.ProviderId.Value}'.");
        }

        var apiKey = request.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey) &&
            !string.IsNullOrWhiteSpace(providerFactory.Configuration.ApiKeyEnvironmentVariable))
        {
            apiKey = environmentVariableProvider(providerFactory.Configuration.ApiKeyEnvironmentVariable);
        }

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var envVar = providerFactory.Configuration.ApiKeyEnvironmentVariable ?? "provider-specific environment variable";
            throw new InvalidOperationException(
                $"Missing API key for provider '{providerFactory.Configuration.ProviderId.Value}'. Use --api-key or set {envVar}.");
        }

        var thinkingLevel =
            request.ThinkingLevel
            ?? TryParseThinkingLevel(sessionContext?.ThinkingLevel)
            ?? TryParseThinkingLevel(settings.DefaultThinkingLevel)
            ?? ThinkingLevel.Off;

        var contextFiles = request.LoadContextFiles
            ? CodingAgentContextLoader.Load(workingDirectory)
            : Array.Empty<CodingAgentContextFile>();

        return new CodingAgentRunConfiguration(
            workingDirectory,
            sessionDirectory,
            providerFactory,
            providerFactory.ResolveModel(modelId),
            apiKey,
            thinkingLevel,
            contextFiles);
    }

    public string ResolveSessionDirectory(string? sessionDirectoryOverride, string workingDirectory)
    {
        var candidate =
            sessionDirectoryOverride
            ?? SettingsManager.Settings.SessionDir
            ?? Path.Combine(Path.GetFullPath(workingDirectory), ".pi-sharp", "sessions");

        return Path.GetFullPath(candidate);
    }

    public static ThinkingLevel? TryParseThinkingLevel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ThinkingLevels.TryGetValue(value, out var thinkingLevel)
            ? thinkingLevel
            : null;
    }
}
