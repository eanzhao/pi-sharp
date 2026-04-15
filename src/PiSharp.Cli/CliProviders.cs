using Microsoft.Extensions.AI;
using OpenAI;
using PiSharp.Ai;

namespace PiSharp.Cli;

public sealed class CliProviderFactory
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

public sealed class CliProviderCatalog
{
    private readonly Dictionary<string, CliProviderFactory> _providers;

    public CliProviderCatalog(IEnumerable<CliProviderFactory> providers)
    {
        ArgumentNullException.ThrowIfNull(providers);

        _providers = providers.ToDictionary(
            provider => provider.Configuration.ProviderId.Value,
            StringComparer.OrdinalIgnoreCase);
    }

    public static CliProviderCatalog CreateDefault() => new([CreateOpenAiProvider()]);

    public IReadOnlyCollection<CliProviderFactory> GetAll() =>
        _providers.Values.OrderBy(provider => provider.Configuration.ProviderId.Value, StringComparer.OrdinalIgnoreCase).ToArray();

    public bool TryGet(string providerName, out CliProviderFactory? providerFactory) =>
        _providers.TryGetValue(providerName, out providerFactory);

    private static CliProviderFactory CreateOpenAiProvider() =>
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
                new ModelMetadata(
                    "gpt-4.1",
                    "GPT-4.1",
                    ApiId.OpenAi,
                    ProviderId.OpenAi,
                    1_000_000,
                    32_768,
                    ModelCapability.TextInput | ModelCapability.Streaming | ModelCapability.ToolCalling,
                    ModelPricing.Free),
            ],
            CreateChatClient = static (modelId, apiKey) =>
            {
                var client = new OpenAIClient(apiKey);
                return client.GetChatClient(modelId).AsIChatClient();
            },
        };
}
