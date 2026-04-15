using System.Globalization;

namespace PiSharp.Pods;

public sealed class PodPlanningException(string message) : InvalidOperationException(message);

public sealed class PodDeploymentPlanner
{
    public PodDeploymentPlanner(KnownModelCatalog? knownModelCatalog = null)
    {
        KnownModelCatalog = knownModelCatalog ?? KnownModelCatalog.LoadDefault();
    }

    public KnownModelCatalog KnownModelCatalog { get; }

    public ModelDeploymentPlan Plan(PodDefinition pod, ModelDeploymentRequest request)
    {
        ArgumentNullException.ThrowIfNull(pod);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ModelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Name);

        if (pod.Models.ContainsKey(request.Name))
        {
            throw new PodPlanningException($"A deployment named '{request.Name}' already exists on the selected pod.");
        }

        var port = GetNextPort(pod);
        var gpuIds = Array.Empty<int>();
        var vllmArguments = new List<string>();
        var environmentVariables = new Dictionary<string, string>(StringComparer.Ordinal);
        string? notes = null;
        var isKnownModel = KnownModelCatalog.IsKnownModel(request.ModelId);

        if (request.CustomVllmArguments.Count > 0)
        {
            vllmArguments.AddRange(request.CustomVllmArguments);
            notes = "Using custom vLLM arguments; GPU allocation is delegated to vLLM.";
        }
        else if (isKnownModel)
        {
            var selection = ResolveKnownModelSelection(pod, request, out gpuIds);
            vllmArguments.AddRange(selection.Arguments);

            foreach (var (key, value) in selection.EnvironmentVariables)
            {
                environmentVariables[key] = value;
            }

            notes = selection.Notes;
        }
        else
        {
            if (request.GpuCount is not null)
            {
                throw new PodPlanningException("--gpus can only be used with predefined models.");
            }

            gpuIds = SelectGpus(pod, 1);
            notes = "Unknown model; defaulting to the least-used single GPU.";
        }

        if (request.CustomVllmArguments.Count == 0)
        {
            ApplyMemoryOverride(vllmArguments, request.Memory);
            ApplyContextOverride(vllmArguments, request.ContextWindow);
        }

        return new ModelDeploymentPlan
        {
            Name = request.Name,
            ModelId = request.ModelId,
            DisplayName = KnownModelCatalog.GetDisplayName(request.ModelId),
            Port = port,
            GpuIds = gpuIds,
            VllmArguments = vllmArguments,
            EnvironmentVariables = environmentVariables,
            IsKnownModel = isKnownModel,
            Notes = notes,
        };
    }

    public static int GetNextPort(PodDefinition pod)
    {
        ArgumentNullException.ThrowIfNull(pod);

        var usedPorts = pod.Models.Values.Select(model => model.Port).ToHashSet();
        var port = PodsDefaults.StartingPort;

        while (usedPorts.Contains(port))
        {
            port++;
        }

        return port;
    }

    public static int[] SelectGpus(PodDefinition pod, int count)
    {
        ArgumentNullException.ThrowIfNull(pod);

        if (count < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "GPU count must be at least 1.");
        }

        if (pod.Gpus.Count < count)
        {
            throw new PodPlanningException($"The pod only has {pod.Gpus.Count} GPU(s), but {count} were requested.");
        }

        if (count == pod.Gpus.Count)
        {
            return pod.Gpus.Select(gpu => gpu.Id).ToArray();
        }

        var usageByGpu = pod.Gpus.ToDictionary(gpu => gpu.Id, _ => 0);
        foreach (var deployment in pod.Models.Values)
        {
            foreach (var gpuId in deployment.GpuIds)
            {
                if (usageByGpu.TryGetValue(gpuId, out var usageCount))
                {
                    usageByGpu[gpuId] = usageCount + 1;
                }
            }
        }

        return usageByGpu
            .OrderBy(pair => pair.Value)
            .ThenBy(pair => pair.Key)
            .Take(count)
            .Select(pair => pair.Key)
            .ToArray();
    }

    private KnownModelSelection ResolveKnownModelSelection(
        PodDefinition pod,
        ModelDeploymentRequest request,
        out int[] gpuIds)
    {
        var requestedGpuCount = request.GpuCount;
        if (requestedGpuCount is not null)
        {
            if (requestedGpuCount > pod.Gpus.Count)
            {
                throw new PodPlanningException(
                    $"Requested {requestedGpuCount} GPU(s), but the pod only has {pod.Gpus.Count}.");
            }

            var explicitSelection = KnownModelCatalog.GetConfiguration(request.ModelId, pod.Gpus, requestedGpuCount.Value);
            if (explicitSelection is null)
            {
                var availableCounts = KnownModelCatalog.GetAvailableGpuCounts(request.ModelId);
                var countsText = availableCounts.Count == 0
                    ? "none"
                    : string.Join(", ", availableCounts);

                throw new PodPlanningException(
                    $"Model '{KnownModelCatalog.GetDisplayName(request.ModelId)}' has no configuration for {requestedGpuCount} GPU(s). Available counts: {countsText}.");
            }

            gpuIds = SelectGpus(pod, requestedGpuCount.Value);
            return explicitSelection;
        }

        for (var candidateGpuCount = pod.Gpus.Count; candidateGpuCount >= 1; candidateGpuCount--)
        {
            var selection = KnownModelCatalog.GetConfiguration(request.ModelId, pod.Gpus, candidateGpuCount);
            if (selection is null)
            {
                continue;
            }

            gpuIds = SelectGpus(pod, candidateGpuCount);
            return selection;
        }

        throw new PodPlanningException(
            $"Model '{KnownModelCatalog.GetDisplayName(request.ModelId)}' is not compatible with the selected pod GPUs.");
    }

    private static void ApplyMemoryOverride(List<string> arguments, string? memory)
    {
        if (string.IsNullOrWhiteSpace(memory))
        {
            return;
        }

        var fraction = ParseMemoryFraction(memory);
        RemoveFlag(arguments, "--gpu-memory-utilization", removeFollowingValue: true);
        arguments.Add("--gpu-memory-utilization");
        arguments.Add(fraction.ToString("0.###", CultureInfo.InvariantCulture));
    }

    private static void ApplyContextOverride(List<string> arguments, string? contextWindow)
    {
        if (string.IsNullOrWhiteSpace(contextWindow))
        {
            return;
        }

        var maxTokens = ParseContextWindow(contextWindow);
        RemoveFlag(arguments, "--max-model-len", removeFollowingValue: true);
        arguments.Add("--max-model-len");
        arguments.Add(maxTokens.ToString(CultureInfo.InvariantCulture));
    }

    private static void RemoveFlag(List<string> arguments, string flag, bool removeFollowingValue)
    {
        for (var index = 0; index < arguments.Count; index++)
        {
            if (!string.Equals(arguments[index], flag, StringComparison.Ordinal))
            {
                continue;
            }

            arguments.RemoveAt(index);
            if (removeFollowingValue && index < arguments.Count)
            {
                arguments.RemoveAt(index);
            }

            index--;
        }
    }

    private static decimal ParseMemoryFraction(string value)
    {
        var trimmed = value.Trim();
        var isPercentage = trimmed.EndsWith('%');
        var rawNumber = isPercentage ? trimmed[..^1] : trimmed;

        if (!decimal.TryParse(rawNumber, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
        {
            throw new PodPlanningException($"Invalid memory value '{value}'.");
        }

        var fraction = isPercentage ? parsed / 100m : parsed;
        if (fraction <= 0m || fraction > 1m)
        {
            throw new PodPlanningException($"Memory value '{value}' must resolve to a fraction between 0 and 1.");
        }

        return fraction;
    }

    private static int ParseContextWindow(string value)
    {
        return value.Trim().ToLowerInvariant() switch
        {
            "4k" => 4_096,
            "8k" => 8_192,
            "16k" => 16_384,
            "32k" => 32_768,
            "64k" => 65_536,
            "128k" => 131_072,
            _ when int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0 => parsed,
            _ => throw new PodPlanningException($"Invalid context window '{value}'."),
        };
    }
}
