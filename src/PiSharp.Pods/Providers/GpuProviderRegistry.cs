namespace PiSharp.Pods.Providers;

public sealed class GpuProviderRegistry
{
    private readonly IReadOnlyDictionary<string, IGpuProvider> _providers;

    public GpuProviderRegistry(IEnumerable<IGpuProvider>? providers = null)
    {
        _providers = (providers ?? CreateDefaultProviders())
            .SelectMany(static provider => CreateAliases(provider).Select(alias => KeyValuePair.Create(alias, provider)))
            .ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    public IReadOnlyList<IGpuProvider> GetAll() =>
        _providers.Values
            .DistinctBy(static provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static provider => provider.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public bool TryGet(string name, out IGpuProvider? provider) =>
        _providers.TryGetValue(name, out provider);

    public IGpuProvider GetRequired(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (TryGet(name, out var provider))
        {
            return provider!;
        }

        throw new KeyNotFoundException(
            $"Unknown GPU provider '{name}'. Available providers: {string.Join(", ", GetAll().Select(static provider => provider.Name))}.");
    }

    private static IEnumerable<IGpuProvider> CreateDefaultProviders()
    {
        yield return new DataCrunchProvider();
        yield return new RunPodProvider();
        yield return new VastAiProvider();
    }

    private static IEnumerable<string> CreateAliases(IGpuProvider provider)
    {
        yield return provider.Name;

        if (string.Equals(provider.Name, "datacrunch", StringComparison.OrdinalIgnoreCase))
        {
            yield return "data-crunch";
        }

        if (string.Equals(provider.Name, "vastai", StringComparison.OrdinalIgnoreCase))
        {
            yield return "vast.ai";
        }
    }
}
