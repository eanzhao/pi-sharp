using System.Text.Json.Serialization;

namespace PiSharp.Pods;

public static class PodsDefaults
{
    public const string DefaultConfigDirectoryName = ".pi";
    public const string ConfigurationFileName = "pods.json";
    public const string PiConfigDirectoryEnvironmentVariable = "PI_CONFIG_DIR";
    public const string PiApiKeyEnvironmentVariable = "PI_API_KEY";
    public const string VllmRelease = "release";
    public const string VllmNightly = "nightly";
    public const string VllmGptOss = "gpt-oss";
    public const int StartingPort = 8001;
}

public enum PodApiKind
{
    ChatCompletions,
    Responses,
}

public sealed record GpuInfo
{
    [JsonPropertyName("id")]
    public int Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("memory")]
    public string Memory { get; init; } = string.Empty;
}

public sealed record ModelDeployment
{
    [JsonPropertyName("model")]
    public string ModelId { get; init; } = string.Empty;

    [JsonPropertyName("port")]
    public int Port { get; init; }

    [JsonPropertyName("gpu")]
    public List<int> GpuIds { get; init; } = [];

    [JsonPropertyName("pid")]
    public int ProcessId { get; init; }
}

public sealed record PodDefinition
{
    [JsonPropertyName("ssh")]
    public string SshCommand { get; init; } = string.Empty;

    [JsonPropertyName("gpus")]
    public List<GpuInfo> Gpus { get; init; } = [];

    [JsonPropertyName("models")]
    public Dictionary<string, ModelDeployment> Models { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("modelsPath")]
    public string? ModelsPath { get; init; }

    [JsonPropertyName("vllmVersion")]
    public string VllmVersion { get; init; } = PodsDefaults.VllmRelease;
}

public sealed record PodsConfiguration
{
    [JsonPropertyName("pods")]
    public Dictionary<string, PodDefinition> Pods { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("active")]
    public string? Active { get; init; }
}

public sealed record PodReference(string Name, PodDefinition Pod);

public sealed record PodEndpoint(
    string PodName,
    string DeploymentName,
    string ModelId,
    Uri BaseUri,
    string ApiKey,
    PodApiKind ApiKind);

public sealed record ModelDeploymentRequest
{
    public required string ModelId { get; init; }

    public required string Name { get; init; }

    public string? Memory { get; init; }

    public string? ContextWindow { get; init; }

    public int? GpuCount { get; init; }

    public IReadOnlyList<string> CustomVllmArguments { get; init; } = Array.Empty<string>();
}

public sealed record ModelDeploymentPlan
{
    public required string Name { get; init; }

    public required string ModelId { get; init; }

    public required string DisplayName { get; init; }

    public required int Port { get; init; }

    public required IReadOnlyList<int> GpuIds { get; init; }

    public required IReadOnlyList<string> VllmArguments { get; init; }

    public required IReadOnlyDictionary<string, string> EnvironmentVariables { get; init; }

    public bool IsKnownModel { get; init; }

    public string? Notes { get; init; }
}

public sealed record KnownModelConfiguration(
    int GpuCount,
    IReadOnlyList<string> GpuTypes,
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    string? Notes);

public sealed record KnownModelDefinition(
    string Id,
    string Name,
    IReadOnlyList<KnownModelConfiguration> Configurations,
    string? Notes);

public sealed record KnownModelSelection(
    IReadOnlyList<string> Arguments,
    IReadOnlyDictionary<string, string> EnvironmentVariables,
    string? Notes);

public sealed class PodPromptToolOptions
{
    public int ReadMaxLines { get; init; } = 200;

    public int ReadMaxCharacters { get; init; } = 32_000;

    public int LsMaxEntries { get; init; } = 500;

    public int GlobMaxResults { get; init; } = 200;

    public int SearchMaxResults { get; init; } = 200;
}

public sealed class PodAgentFactoryOptions
{
    public string WorkingDirectory { get; init; } = Directory.GetCurrentDirectory();

    public string? ApiKey { get; init; }

    public string? SystemPrompt { get; init; }

    public PodPromptToolOptions ToolOptions { get; init; } = new();

    public PiSharp.Agent.ThinkingLevel ThinkingLevel { get; init; } = PiSharp.Agent.ThinkingLevel.Off;

    public Func<PodEndpoint, string, Microsoft.Extensions.AI.IChatClient> CreateChatClient { get; init; } =
        PodAgentFactory.CreateDefaultChatClient;
}
