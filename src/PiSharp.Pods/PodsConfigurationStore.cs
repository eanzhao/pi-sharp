using System.Text.Json;

namespace PiSharp.Pods;

public sealed class PodsConfigurationStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
    };

    public PodsConfigurationStore(string? rootDirectory = null)
    {
        RootDirectory = ResolveRootDirectory(rootDirectory);
        ConfigurationFilePath = Path.Combine(RootDirectory, PodsDefaults.ConfigurationFileName);
    }

    public string RootDirectory { get; }

    public string ConfigurationFilePath { get; }

    public PodsConfiguration Load()
    {
        Directory.CreateDirectory(RootDirectory);
        if (!File.Exists(ConfigurationFilePath))
        {
            return CreateEmptyConfiguration();
        }

        using var stream = File.OpenRead(ConfigurationFilePath);
        var configuration = JsonSerializer.Deserialize<PodsConfiguration>(stream, SerializerOptions)
            ?? CreateEmptyConfiguration();

        return Normalize(configuration);
    }

    public void Save(PodsConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        Directory.CreateDirectory(RootDirectory);

        var normalizedConfiguration = Normalize(configuration);
        var temporaryPath = $"{ConfigurationFilePath}.{Guid.NewGuid():N}.tmp";

        using (var stream = File.Create(temporaryPath))
        {
            JsonSerializer.Serialize(stream, normalizedConfiguration, SerializerOptions);
        }

        File.Move(temporaryPath, ConfigurationFilePath, overwrite: true);
    }

    public PodsConfiguration AddOrUpdatePod(string name, PodDefinition pod, bool setActiveWhenMissing = true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(pod);

        var configuration = Load();
        var pods = ClonePods(configuration.Pods);
        pods[name] = NormalizePod(pod);

        var updated = new PodsConfiguration
        {
            Pods = pods,
            Active = string.IsNullOrWhiteSpace(configuration.Active) && setActiveWhenMissing
                ? name
                : configuration.Active,
        };

        Save(updated);
        return updated;
    }

    public PodsConfiguration RemovePod(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var configuration = Load();
        if (!configuration.Pods.ContainsKey(name))
        {
            return configuration;
        }

        var pods = ClonePods(configuration.Pods);
        pods.Remove(name);

        var updated = new PodsConfiguration
        {
            Pods = pods,
            Active = string.Equals(configuration.Active, name, StringComparison.Ordinal)
                ? null
                : configuration.Active,
        };

        Save(updated);
        return updated;
    }

    public PodsConfiguration SetActivePod(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var configuration = Load();
        if (!configuration.Pods.ContainsKey(name))
        {
            throw new KeyNotFoundException($"Pod '{name}' was not found.");
        }

        var updated = configuration with { Active = name };
        Save(updated);
        return updated;
    }

    public PodReference? GetActivePod()
    {
        var configuration = Load();
        if (string.IsNullOrWhiteSpace(configuration.Active))
        {
            return null;
        }

        return configuration.Pods.TryGetValue(configuration.Active, out var pod)
            ? new PodReference(configuration.Active, pod)
            : null;
    }

    internal static PodsConfiguration Normalize(PodsConfiguration? configuration)
    {
        if (configuration is null)
        {
            return CreateEmptyConfiguration();
        }

        var pods = ClonePods(configuration.Pods);
        var active = configuration.Active;

        if (!string.IsNullOrWhiteSpace(active) && !pods.ContainsKey(active))
        {
            active = null;
        }

        return new PodsConfiguration
        {
            Pods = pods,
            Active = active,
        };
    }

    internal static PodDefinition NormalizePod(PodDefinition? pod)
    {
        if (pod is null)
        {
            return new PodDefinition();
        }

        var models = new Dictionary<string, ModelDeployment>(StringComparer.Ordinal);
        foreach (var (name, deployment) in pod.Models ?? new Dictionary<string, ModelDeployment>(StringComparer.Ordinal))
        {
            models[name] = NormalizeDeployment(deployment);
        }

        return new PodDefinition
        {
            SshCommand = pod.SshCommand ?? string.Empty,
            Gpus = pod.Gpus?.Select(gpu => gpu with { }).ToList() ?? [],
            Models = models,
            ModelsPath = pod.ModelsPath,
            VllmVersion = string.IsNullOrWhiteSpace(pod.VllmVersion)
                ? PodsDefaults.VllmRelease
                : pod.VllmVersion,
        };
    }

    private static ModelDeployment NormalizeDeployment(ModelDeployment? deployment) =>
        deployment is null
            ? new ModelDeployment()
            : deployment with
            {
                ModelId = deployment.ModelId ?? string.Empty,
                GpuIds = deployment.GpuIds?.ToList() ?? [],
            };

    private static Dictionary<string, PodDefinition> ClonePods(IReadOnlyDictionary<string, PodDefinition>? pods)
    {
        var clone = new Dictionary<string, PodDefinition>(StringComparer.Ordinal);
        if (pods is null)
        {
            return clone;
        }

        foreach (var (name, pod) in pods)
        {
            clone[name] = NormalizePod(pod);
        }

        return clone;
    }

    private static PodsConfiguration CreateEmptyConfiguration() => new()
    {
        Pods = new Dictionary<string, PodDefinition>(StringComparer.Ordinal),
    };

    private static string ResolveRootDirectory(string? rootDirectory)
    {
        if (!string.IsNullOrWhiteSpace(rootDirectory))
        {
            return Path.GetFullPath(rootDirectory);
        }

        var configuredPath = Environment.GetEnvironmentVariable(PodsDefaults.PiConfigDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Path.GetFullPath(configuredPath);
        }

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, PodsDefaults.DefaultConfigDirectoryName);
    }
}
