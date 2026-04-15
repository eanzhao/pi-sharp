using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PiSharp.Pods;

public sealed class KnownModelCatalog
{
    private const string EmbeddedResourceName = "PiSharp.Pods.Models.models.json";
    private readonly Dictionary<string, KnownModelDefinition> _models;

    public KnownModelCatalog(IEnumerable<KnownModelDefinition> models)
    {
        ArgumentNullException.ThrowIfNull(models);

        _models = models.ToDictionary(model => model.Id, StringComparer.Ordinal);
    }

    public static KnownModelCatalog LoadDefault()
    {
        var assembly = typeof(KnownModelCatalog).Assembly;
        using var stream = assembly.GetManifestResourceStream(EmbeddedResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{EmbeddedResourceName}' was not found.");

        return Load(stream);
    }

    public static KnownModelCatalog Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var document = JsonSerializer.Deserialize(stream, KnownModelCatalogJsonContext.Default.KnownModelsDocument)
            ?? throw new InvalidOperationException("Failed to deserialize the bundled model catalog.");

        return new KnownModelCatalog(document.Models.Select(static entry =>
            new KnownModelDefinition(
                entry.Key,
                entry.Value.Name,
                entry.Value.Configurations.Select(static configuration =>
                    new KnownModelConfiguration(
                        configuration.GpuCount,
                        configuration.GpuTypes?.ToArray() ?? Array.Empty<string>(),
                        configuration.Arguments?.ToArray() ?? Array.Empty<string>(),
                        configuration.EnvironmentVariables is null
                            ? new Dictionary<string, string>(StringComparer.Ordinal)
                            : new Dictionary<string, string>(configuration.EnvironmentVariables, StringComparer.Ordinal),
                        configuration.Notes)).ToArray(),
                entry.Value.Notes)));
    }

    public IReadOnlyCollection<KnownModelDefinition> GetAll() =>
        _models.Values.OrderBy(model => model.Id, StringComparer.Ordinal).ToArray();

    public bool IsKnownModel(string modelId) => _models.ContainsKey(modelId);

    public bool TryGet(string modelId, out KnownModelDefinition? model) => _models.TryGetValue(modelId, out model);

    public string GetDisplayName(string modelId) =>
        _models.TryGetValue(modelId, out var model)
            ? model.Name
            : modelId;

    public IReadOnlyList<int> GetAvailableGpuCounts(string modelId)
    {
        if (!_models.TryGetValue(modelId, out var model))
        {
            return Array.Empty<int>();
        }

        return model.Configurations
            .Select(configuration => configuration.GpuCount)
            .Distinct()
            .OrderBy(count => count)
            .ToArray();
    }

    public KnownModelSelection? GetConfiguration(
        string modelId,
        IReadOnlyList<GpuInfo> gpus,
        int requestedGpuCount)
    {
        if (!_models.TryGetValue(modelId, out var model))
        {
            return null;
        }

        var primaryGpuType = ExtractPrimaryGpuType(gpus);
        var exactMatch = model.Configurations.FirstOrDefault(configuration =>
            configuration.GpuCount == requestedGpuCount &&
            IsGpuTypeCompatible(configuration.GpuTypes, primaryGpuType));

        var fallbackMatch = exactMatch ?? model.Configurations.FirstOrDefault(configuration => configuration.GpuCount == requestedGpuCount);
        if (fallbackMatch is null)
        {
            return null;
        }

        return new KnownModelSelection(
            fallbackMatch.Arguments.ToArray(),
            new Dictionary<string, string>(fallbackMatch.EnvironmentVariables, StringComparer.Ordinal),
            fallbackMatch.Notes ?? model.Notes);
    }

    internal static string ExtractPrimaryGpuType(IReadOnlyList<GpuInfo> gpus)
    {
        var rawName = gpus.FirstOrDefault()?.Name ?? string.Empty;
        var withoutVendor = rawName.Replace("NVIDIA", string.Empty, StringComparison.OrdinalIgnoreCase).Trim();
        var firstToken = withoutVendor.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return firstToken ?? withoutVendor;
    }

    private static bool IsGpuTypeCompatible(IReadOnlyList<string> gpuTypes, string primaryGpuType)
    {
        if (gpuTypes.Count == 0)
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(primaryGpuType))
        {
            return false;
        }

        return gpuTypes.Any(type =>
            primaryGpuType.Contains(type, StringComparison.OrdinalIgnoreCase) ||
            type.Contains(primaryGpuType, StringComparison.OrdinalIgnoreCase));
    }

    internal sealed record KnownModelsDocument(Dictionary<string, KnownModelEntry> Models);

    internal sealed record KnownModelEntry(
        string Name,
        [property: JsonPropertyName("configs")] List<KnownModelConfigurationEntry> Configurations,
        string? Notes);

    internal sealed record KnownModelConfigurationEntry(
        int GpuCount,
        List<string>? GpuTypes,
        [property: JsonPropertyName("args")] List<string>? Arguments,
        [property: JsonPropertyName("env")] Dictionary<string, string>? EnvironmentVariables,
        string? Notes);
}

[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(KnownModelCatalog.KnownModelsDocument))]
internal sealed partial class KnownModelCatalogJsonContext : JsonSerializerContext;
