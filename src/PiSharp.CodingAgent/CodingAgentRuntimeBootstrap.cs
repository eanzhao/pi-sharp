using Anthropic;
using Azure.AI.OpenAI;
using Google.GenAI;
using Microsoft.Extensions.AI;
using OpenAI;
using OpenAI.Chat;
using PiSharp.Agent;
using PiSharp.Ai;

namespace PiSharp.CodingAgent;

public sealed record CodingAgentChatClientCreationContext(
    string ModelId,
    string ApiKey,
    string? Endpoint);

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

    public string? EndpointEnvironmentVariable { get; init; }

    public required Func<string, string, IChatClient> CreateChatClient
    {
        get => _createChatClient;
        init => _createChatClient = value ?? throw new ArgumentNullException(nameof(value));
    }

    public Func<CodingAgentChatClientCreationContext, IChatClient>? CreateChatClientWithContext { get; init; }

    public IChatClient Create(string modelId, string apiKey, string? endpoint = null) =>
        CreateChatClientWithContext is null
            ? _createChatClient(modelId, apiKey)
            : CreateChatClientWithContext(new CodingAgentChatClientCreationContext(modelId, apiKey, endpoint));

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
            CreateAzureOpenAiProvider(),
            CreateAnthropicProvider(),
            CreateGoogleProvider(),
            CreateGroqProvider(),
            CreateTogetherProvider(),
            CreateMistralProvider(),
            CreateDeepSeekProvider(),
            CreateFireworksProvider(),
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
                Endpoint: new Uri("https://api.openai.com/v1"),
                DefaultModelId: "gpt-4.1-mini",
                ApiKeyEnvironmentVariable: "OPENAI_API_KEY"),
            KnownModels =
            [
                CreateModel("gpt-4.1-mini", "GPT-4.1 mini", ApiId.OpenAi, ProviderId.OpenAi, pricing: new ModelPricing(0.40m, 1.60m, 0.10m, 0.40m)),
                CreateModel("gpt-4.1", "GPT-4.1", ApiId.OpenAi, ProviderId.OpenAi, pricing: new ModelPricing(2.00m, 8.00m, 0.50m, 2.00m)),
                CreateModel("gpt-4.1-nano", "GPT-4.1 nano", ApiId.OpenAi, ProviderId.OpenAi, pricing: new ModelPricing(0.10m, 0.40m, 0.025m, 0.10m)),
            ],
            CreateChatClient = static (modelId, apiKey) => new ChatClient(modelId, apiKey).AsIChatClient(),
        };

    private static CodingAgentProviderFactory CreateAzureOpenAiProvider() =>
        new()
        {
            Configuration = new ProviderConfiguration(
                ProviderId.AzureOpenAi,
                ApiId.OpenAi,
                "Azure OpenAI",
                DefaultModelId: "gpt-4.1-mini",
                ApiKeyEnvironmentVariable: "AZURE_OPENAI_API_KEY"),
            EndpointEnvironmentVariable = "AZURE_OPENAI_ENDPOINT",
            KnownModels =
            [
                CreateModel("gpt-4.1-mini", "GPT-4.1 mini (Azure deployment)", ApiId.OpenAi, ProviderId.AzureOpenAi, pricing: new ModelPricing(0.40m, 1.60m, 0.10m, 0.40m)),
                CreateModel("gpt-4.1", "GPT-4.1 (Azure deployment)", ApiId.OpenAi, ProviderId.AzureOpenAi, pricing: new ModelPricing(2.00m, 8.00m, 0.50m, 2.00m)),
            ],
            CreateChatClient = static (_, _) => throw new InvalidOperationException("Azure OpenAI requires an endpoint."),
            CreateChatClientWithContext = static context =>
            {
                if (string.IsNullOrWhiteSpace(context.Endpoint))
                {
                    throw new InvalidOperationException("Azure OpenAI requires AZURE_OPENAI_ENDPOINT.");
                }

                return new AzureOpenAIClient(
                        new Uri(context.Endpoint),
                        new System.ClientModel.ApiKeyCredential(context.ApiKey))
                    .GetChatClient(context.ModelId)
                    .AsIChatClient();
            },
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
                CreateModel("claude-3-7-sonnet-latest", "Claude 3.7 Sonnet", ApiId.Anthropic, ProviderId.Anthropic, 200_000, 8_192, new ModelPricing(3.00m, 15.00m)),
                CreateModel("claude-3-5-haiku-latest", "Claude 3.5 Haiku", ApiId.Anthropic, ProviderId.Anthropic, 200_000, 8_192, new ModelPricing(0.80m, 4.00m)),
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
                CreateModel("gemini-2.0-flash", "Gemini 2.0 Flash", ApiId.Google, ProviderId.Google, pricing: new ModelPricing(
                    new TokenPricing(0.10m, 128_000, 0.20m),
                    new TokenPricing(0.40m, 128_000, 0.80m))),
                CreateModel("gemini-2.0-flash-lite", "Gemini 2.0 Flash-Lite", ApiId.Google, ProviderId.Google, pricing: new ModelPricing(
                    new TokenPricing(0.075m, 128_000, 0.15m),
                    new TokenPricing(0.30m, 128_000, 0.60m))),
            ],
            CreateChatClient = static (modelId, apiKey) =>
                new Client(apiKey: apiKey).AsIChatClient(modelId),
        };

    private static CodingAgentProviderFactory CreateGroqProvider() =>
        CreateOpenAiCompatibleProvider(
            ProviderId.Groq,
            "Groq",
            new Uri("https://api.groq.com/openai/v1"),
            "GROQ_API_KEY",
            "llama-3.3-70b-versatile",
            [
                CreateModel("llama-3.3-70b-versatile", "Llama 3.3 70B Versatile", ApiId.OpenAi, ProviderId.Groq, 128_000, 8_192, new ModelPricing(0.59m, 0.79m)),
                CreateModel("llama-3.1-8b-instant", "Llama 3.1 8B Instant", ApiId.OpenAi, ProviderId.Groq, 128_000, 8_192, new ModelPricing(0.05m, 0.08m)),
            ]);

    private static CodingAgentProviderFactory CreateTogetherProvider() =>
        CreateOpenAiCompatibleProvider(
            ProviderId.Together,
            "Together AI",
            new Uri("https://api.together.xyz/v1"),
            "TOGETHER_API_KEY",
            "meta-llama/Llama-3.3-70B-Instruct-Turbo",
            [
                CreateModel("meta-llama/Llama-3.3-70B-Instruct-Turbo", "Llama 3.3 70B Turbo", ApiId.OpenAi, ProviderId.Together, 128_000, 8_192, new ModelPricing(0.88m, 0.88m)),
                CreateModel("Qwen/Qwen2.5-Coder-32B-Instruct", "Qwen2.5-Coder-32B", ApiId.OpenAi, ProviderId.Together, 128_000, 8_192, new ModelPricing(0.80m, 0.80m)),
            ]);

    private static CodingAgentProviderFactory CreateMistralProvider() =>
        CreateOpenAiCompatibleProvider(
            ProviderId.Mistral,
            "Mistral",
            new Uri("https://api.mistral.ai/v1"),
            "MISTRAL_API_KEY",
            "mistral-large-latest",
            [
                CreateModel("mistral-large-latest", "Mistral Large", ApiId.OpenAi, ProviderId.Mistral, 128_000, 8_192, new ModelPricing(2.00m, 6.00m)),
                CreateModel("codestral-latest", "Codestral", ApiId.OpenAi, ProviderId.Mistral, 256_000, 8_192, new ModelPricing(0.30m, 0.90m)),
                CreateModel("mistral-small-latest", "Mistral Small", ApiId.OpenAi, ProviderId.Mistral, 128_000, 8_192, new ModelPricing(0.20m, 0.60m)),
            ]);

    private static CodingAgentProviderFactory CreateDeepSeekProvider() =>
        CreateOpenAiCompatibleProvider(
            ProviderId.DeepSeek,
            "DeepSeek",
            new Uri("https://api.deepseek.com/v1"),
            "DEEPSEEK_API_KEY",
            "deepseek-chat",
            [
                CreateModel("deepseek-chat", "DeepSeek V3", ApiId.OpenAi, ProviderId.DeepSeek, 128_000, 8_192, new ModelPricing(0.27m, 1.10m)),
                CreateModel("deepseek-reasoner", "DeepSeek R1", ApiId.OpenAi, ProviderId.DeepSeek, 128_000, 8_192, new ModelPricing(0.55m, 2.19m)),
            ]);

    private static CodingAgentProviderFactory CreateFireworksProvider() =>
        CreateOpenAiCompatibleProvider(
            ProviderId.Fireworks,
            "Fireworks",
            new Uri("https://api.fireworks.ai/inference/v1"),
            "FIREWORKS_API_KEY",
            "accounts/fireworks/models/llama-v3p3-70b-instruct",
            [
                CreateModel("accounts/fireworks/models/llama-v3p3-70b-instruct", "Llama 3.3 70B Instruct", ApiId.OpenAi, ProviderId.Fireworks, 128_000, 8_192, new ModelPricing(0.90m, 0.90m)),
                CreateModel("accounts/fireworks/models/deepseek-v3", "DeepSeek V3", ApiId.OpenAi, ProviderId.Fireworks, 128_000, 8_192, new ModelPricing(0.90m, 0.90m)),
            ]);

    private static CodingAgentProviderFactory CreateOpenAiCompatibleProvider(
        ProviderId providerId,
        string displayName,
        Uri endpoint,
        string apiKeyEnvironmentVariable,
        string defaultModelId,
        IReadOnlyList<ModelMetadata> knownModels) =>
        new()
        {
            Configuration = new ProviderConfiguration(
                providerId,
                ApiId.OpenAi,
                displayName,
                Endpoint: endpoint,
                DefaultModelId: defaultModelId,
                ApiKeyEnvironmentVariable: apiKeyEnvironmentVariable),
            KnownModels = knownModels,
            CreateChatClient = static (_, _) => throw new InvalidOperationException("OpenAI-compatible providers require an endpoint."),
            CreateChatClientWithContext = static context => CreateOpenAiCompatibleChatClient(context.ModelId, context.ApiKey, context.Endpoint),
        };

    private static IChatClient CreateOpenAiCompatibleChatClient(string modelId, string apiKey, string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("The provider endpoint was not configured.");
        }

        var clientOptions = new OpenAIClientOptions
        {
            Endpoint = new Uri(endpoint),
        };

        return new ChatClient(
                modelId,
                new System.ClientModel.ApiKeyCredential(apiKey),
                clientOptions)
            .AsIChatClient();
    }

    private static ModelMetadata CreateModel(
        string id,
        string name,
        ApiId apiId,
        ProviderId providerId,
        int contextWindow = 1_000_000,
        int maxOutputTokens = 32_768,
        ModelPricing? pricing = null) =>
        new(
            id,
            name,
            apiId,
            providerId,
            contextWindow,
            maxOutputTokens,
            ModelCapability.TextInput | ModelCapability.Streaming | ModelCapability.ToolCalling,
            pricing ?? ModelPricing.Free);
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
    string? ProviderEndpoint,
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

        var providerEndpoint = providerFactory.Configuration.Endpoint?.ToString();
        if (string.IsNullOrWhiteSpace(providerEndpoint) &&
            !string.IsNullOrWhiteSpace(providerFactory.EndpointEnvironmentVariable))
        {
            providerEndpoint = environmentVariableProvider(providerFactory.EndpointEnvironmentVariable);
        }

        if (!string.IsNullOrWhiteSpace(providerFactory.EndpointEnvironmentVariable) &&
            string.IsNullOrWhiteSpace(providerEndpoint))
        {
            throw new InvalidOperationException(
                $"Missing endpoint for provider '{providerFactory.Configuration.ProviderId.Value}'. Set {providerFactory.EndpointEnvironmentVariable}.");
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
            providerEndpoint,
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
