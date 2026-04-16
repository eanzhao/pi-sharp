using System.Text;
using PiSharp.Agent;

namespace PiSharp.Pods;

public delegate ValueTask PodOutputHandler(string text, bool isError, CancellationToken cancellationToken);

public sealed class PodSetupRequest
{
    public string? MountCommand { get; init; }

    public string? ModelsPath { get; init; }

    public string VllmVersion { get; init; } = PodsDefaults.VllmRelease;

    public string? HuggingFaceToken { get; init; }

    public string? ApiKey { get; init; }
}

public sealed record PodSetupResult(string PodName, PodDefinition Pod);

public sealed class PodStartRequest
{
    public required string ModelId { get; init; }

    public required string Name { get; init; }

    public string? PodName { get; init; }

    public string? Memory { get; init; }

    public string? ContextWindow { get; init; }

    public int? GpuCount { get; init; }

    public IReadOnlyList<string> CustomVllmArguments { get; init; } = Array.Empty<string>();

    public bool FollowStartupLogs { get; init; } = true;

    public int LogCreationDelayMilliseconds { get; init; } = 500;

    public string? HuggingFaceToken { get; init; }

    public string? ApiKey { get; init; }
}

public sealed record PodStartResult(
    string PodName,
    string Host,
    int ProcessId,
    ModelDeploymentPlan Plan,
    Uri BaseUri);

public sealed record PodModelStatus(
    string Name,
    string PodName,
    string Host,
    ModelDeployment Deployment,
    string Status);

public sealed class PodService
{
    private const string SetupScriptRemotePath = "/tmp/pisharp_pod_setup.sh";

    private readonly PodsConfigurationStore _store;
    private readonly IPodSshTransport _sshTransport;
    private readonly PodDeploymentPlanner _planner;
    private readonly PodEndpointResolver _endpointResolver;
    private readonly Func<string, string?> _getEnvironmentVariable;

    public PodService(
        PodsConfigurationStore? store = null,
        IPodSshTransport? sshTransport = null,
        PodDeploymentPlanner? planner = null,
        PodEndpointResolver? endpointResolver = null,
        Func<string, string?>? getEnvironmentVariable = null)
    {
        _store = store ?? new PodsConfigurationStore();
        _sshTransport = sshTransport ?? new ProcessPodSshTransport();
        _planner = planner ?? new PodDeploymentPlanner();
        _endpointResolver = endpointResolver ?? new PodEndpointResolver();
        _getEnvironmentVariable = getEnvironmentVariable ?? Environment.GetEnvironmentVariable;
    }

    public PodsConfigurationStore Store => _store;

    public PodsConfiguration LoadConfiguration() => _store.Load();

    public IReadOnlyList<PodReference> ListPods() =>
        _store.Load().Pods
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => new PodReference(pair.Key, pair.Value))
            .ToArray();

    public PodReference? GetActivePod() => _store.GetActivePod();

    public PodReference ResolvePodReference(string? podName = null) => ResolvePod(podName);

    public PodsConfiguration SetActivePod(string name) => _store.SetActivePod(name);

    public PodsConfiguration RemovePod(string name) => _store.RemovePod(name);

    public async Task<int> ExecuteSshCommandAsync(
        string command,
        string? podName = null,
        PodOutputHandler? outputHandler = null,
        bool forceTty = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);

        var podReference = ResolvePod(podName);
        return await _sshTransport.ExecuteStreamingAsync(
            podReference.Pod.SshCommand,
            command,
            (chunk, ct) => ForwardChunkAsync(chunk, outputHandler, ct),
            new SshStreamingOptions { ForceTty = forceTty },
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<PodSetupResult> SetupPodAsync(
        string name,
        string sshCommand,
        PodSetupRequest request,
        PodOutputHandler? outputHandler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(sshCommand);
        ArgumentNullException.ThrowIfNull(request);

        var huggingFaceToken = ResolveRequiredValue(
            request.HuggingFaceToken,
            "HF_TOKEN",
            "HF_TOKEN environment variable is required for pod setup.");
        var apiKey = ResolveRequiredValue(
            request.ApiKey,
            PodsDefaults.PiApiKeyEnvironmentVariable,
            "PI_API_KEY environment variable is required for pod setup.");
        var modelsPath = ResolveModelsPath(request.ModelsPath, request.MountCommand);

        await WriteInfoAsync(outputHandler, $"Setting up pod '{name}'...", cancellationToken).ConfigureAwait(false);
        await WriteInfoAsync(outputHandler, $"SSH: {sshCommand}", cancellationToken).ConfigureAwait(false);
        await WriteInfoAsync(outputHandler, $"Models path: {modelsPath}", cancellationToken).ConfigureAwait(false);
        await WriteInfoAsync(outputHandler, $"vLLM version: {request.VllmVersion}", cancellationToken).ConfigureAwait(false);

        var testResult = await _sshTransport.ExecuteAsync(
            sshCommand,
            "echo 'SSH OK'",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        if (testResult.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to connect via SSH: {testResult.StandardError.Trim()}");
        }

        await UploadRemoteScriptAsync(
            sshCommand,
            SetupScriptRemotePath,
            PodScriptAssets.LoadPodSetupScript(),
            cancellationToken).ConfigureAwait(false);

        var setupCommand = BuildSetupCommand(modelsPath, request.MountCommand, request.VllmVersion, huggingFaceToken, apiKey);
        var setupExitCode = await _sshTransport.ExecuteStreamingAsync(
            sshCommand,
            setupCommand,
            (chunk, ct) => ForwardChunkAsync(chunk, outputHandler, ct),
            new SshStreamingOptions
            {
                ForceTty = true,
                KeepAlive = true,
            },
            cancellationToken).ConfigureAwait(false);

        if (setupExitCode != 0)
        {
            throw new InvalidOperationException($"Pod setup failed with exit code {setupExitCode}.");
        }

        var gpus = await DetectGpusAsync(sshCommand, cancellationToken).ConfigureAwait(false);
        var pod = new PodDefinition
        {
            SshCommand = sshCommand,
            Gpus = gpus.ToList(),
            Models = new Dictionary<string, ModelDeployment>(StringComparer.Ordinal),
            ModelsPath = modelsPath,
            VllmVersion = request.VllmVersion,
        };

        _store.AddOrUpdatePod(name, pod);
        return new PodSetupResult(name, pod);
    }

    public async Task<PodStartResult> StartModelAsync(
        PodStartRequest request,
        PodOutputHandler? outputHandler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var podReference = ResolvePod(request.PodName);
        var podName = podReference.Name;
        var pod = podReference.Pod;

        if (string.IsNullOrWhiteSpace(pod.ModelsPath))
        {
            throw new InvalidOperationException($"Pod '{podName}' does not have a models path configured.");
        }

        var huggingFaceToken = ResolveRequiredValue(
            request.HuggingFaceToken,
            "HF_TOKEN",
            "HF_TOKEN environment variable is required to start a model.");
        var apiKey = ResolveRequiredValue(
            request.ApiKey,
            PodsDefaults.PiApiKeyEnvironmentVariable,
            "PI_API_KEY environment variable is required to start a model.");

        var plan = _planner.Plan(
            pod,
            new ModelDeploymentRequest
            {
                ModelId = request.ModelId,
                Name = request.Name,
                Memory = request.Memory,
                ContextWindow = request.ContextWindow,
                GpuCount = request.GpuCount,
                CustomVllmArguments = request.CustomVllmArguments,
            });

        await WriteInfoAsync(outputHandler, $"Starting model '{plan.Name}' on pod '{podName}'...", cancellationToken).ConfigureAwait(false);
        await WriteInfoAsync(outputHandler, $"Model: {plan.ModelId}", cancellationToken).ConfigureAwait(false);
        await WriteInfoAsync(outputHandler, $"Port: {plan.Port}", cancellationToken).ConfigureAwait(false);
        await WriteInfoAsync(
            outputHandler,
            $"GPU(s): {(plan.GpuIds.Count > 0 ? string.Join(", ", plan.GpuIds) : "Managed by vLLM")}",
            cancellationToken).ConfigureAwait(false);

        var remoteScriptPath = $"/tmp/pisharp_model_run_{plan.Name}.sh";
        var wrapperScriptPath = $"/tmp/pisharp_model_wrapper_{plan.Name}.sh";

        var modelRunScript = PodScriptAssets.LoadModelRunScript()
            .Replace("{{MODEL_ID}}", plan.ModelId, StringComparison.Ordinal)
            .Replace("{{NAME}}", plan.Name, StringComparison.Ordinal)
            .Replace("{{PORT}}", plan.Port.ToString(), StringComparison.Ordinal)
            .Replace("{{VLLM_ARGS}}", string.Join(' ', plan.VllmArguments), StringComparison.Ordinal);

        await UploadRemoteScriptAsync(
            pod.SshCommand,
            remoteScriptPath,
            modelRunScript,
            cancellationToken).ConfigureAwait(false);

        var exportLines = BuildStartEnvironmentLines(apiKey, huggingFaceToken, plan, plan.GpuIds);
        var wrapperScript = BuildWrapperScript(plan.Name, remoteScriptPath);
        var remoteStartCommand = $$"""
{{string.Join('\n', exportLines)}}
mkdir -p ~/.vllm_logs
cat > {{wrapperScriptPath}} <<'__PI_SHARP_WRAPPER__'
{{wrapperScript}}
__PI_SHARP_WRAPPER__
chmod +x {{wrapperScriptPath}}
setsid {{wrapperScriptPath}} </dev/null >/dev/null 2>&1 &
echo $!
exit 0
""";

        var startResult = await _sshTransport.ExecuteAsync(
            pod.SshCommand,
            remoteStartCommand,
            new SshCommandOptions { KeepAlive = true },
            cancellationToken).ConfigureAwait(false);

        if (!int.TryParse(startResult.StandardOutput.Trim(), out var processId) || processId <= 0)
        {
            throw new InvalidOperationException("Failed to start the remote model runner.");
        }

        var configuration = _store.Load();
        if (!configuration.Pods.TryGetValue(podName, out var storedPod))
        {
            throw new InvalidOperationException($"Pod '{podName}' disappeared while updating local configuration.");
        }

        var updatedModels = new Dictionary<string, ModelDeployment>(storedPod.Models, StringComparer.Ordinal)
        {
            [plan.Name] = new ModelDeployment
            {
                ModelId = plan.ModelId,
                Port = plan.Port,
                GpuIds = plan.GpuIds.ToList(),
                ProcessId = processId,
            },
        };

        var updatedPod = storedPod with { Models = updatedModels };
        _store.AddOrUpdatePod(podName, updatedPod, setActiveWhenMissing: false);

        if (request.FollowStartupLogs)
        {
            try
            {
                await Task.Delay(request.LogCreationDelayMilliseconds, cancellationToken).ConfigureAwait(false);
                await FollowStartupLogsAsync(pod.SshCommand, plan.Name, outputHandler, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                var failedConfiguration = _store.Load();
                if (failedConfiguration.Pods.TryGetValue(podName, out var failedPod) &&
                    failedPod.Models.ContainsKey(plan.Name))
                {
                    var failedModels = new Dictionary<string, ModelDeployment>(failedPod.Models, StringComparer.Ordinal);
                    failedModels.Remove(plan.Name);
                    _store.AddOrUpdatePod(podName, failedPod with { Models = failedModels }, setActiveWhenMissing: false);
                }

                throw;
            }
        }

        var host = SshCommandParser.ExtractHost(pod.SshCommand);
        return new PodStartResult(
            podName,
            host,
            processId,
            plan,
            new Uri($"http://{host}:{plan.Port}/v1", UriKind.Absolute));
    }

    public async Task StopModelAsync(string name, string? podName = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var podReference = ResolvePod(podName);
        if (!podReference.Pod.Models.TryGetValue(name, out var deployment))
        {
            throw new KeyNotFoundException($"Model '{name}' was not found on pod '{podReference.Name}'.");
        }

        var killCommand = $$"""
pkill -TERM -P {{deployment.ProcessId}} 2>/dev/null || true
kill {{deployment.ProcessId}} 2>/dev/null || true
""";

        await _sshTransport.ExecuteAsync(
            podReference.Pod.SshCommand,
            killCommand,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var updatedModels = new Dictionary<string, ModelDeployment>(podReference.Pod.Models, StringComparer.Ordinal);
        updatedModels.Remove(name);
        _store.AddOrUpdatePod(podReference.Name, podReference.Pod with { Models = updatedModels }, setActiveWhenMissing: false);
    }

    public async Task StopAllModelsAsync(string? podName = null, CancellationToken cancellationToken = default)
    {
        var podReference = ResolvePod(podName);
        var deployments = podReference.Pod.Models.Values.ToArray();
        if (deployments.Length == 0)
        {
            return;
        }

        var killScript = new StringBuilder();
        foreach (var deployment in deployments)
        {
            killScript.AppendLine($"pkill -TERM -P {deployment.ProcessId} 2>/dev/null || true");
            killScript.AppendLine($"kill {deployment.ProcessId} 2>/dev/null || true");
        }

        await _sshTransport.ExecuteAsync(
            podReference.Pod.SshCommand,
            killScript.ToString(),
            cancellationToken: cancellationToken).ConfigureAwait(false);

        _store.AddOrUpdatePod(
            podReference.Name,
            podReference.Pod with { Models = new Dictionary<string, ModelDeployment>(StringComparer.Ordinal) },
            setActiveWhenMissing: false);
    }

    public async Task<IReadOnlyList<PodModelStatus>> GetModelStatusesAsync(
        string? podName = null,
        bool verifyProcesses = true,
        CancellationToken cancellationToken = default)
    {
        var podReference = ResolvePod(podName);
        var host = SshCommandParser.ExtractHost(podReference.Pod.SshCommand);
        var results = new List<PodModelStatus>();

        foreach (var (name, deployment) in podReference.Pod.Models.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var status = "unknown";
            if (verifyProcesses)
            {
                var checkCommand = $$"""
if ps -p {{deployment.ProcessId}} > /dev/null 2>&1; then
  if curl -s -f http://localhost:{{deployment.Port}}/health > /dev/null 2>&1; then
    echo running
  else
    if tail -n 20 ~/.vllm_logs/{{name}}.log 2>/dev/null | grep -q "ERROR\|Failed\|Cuda error\|died"; then
      echo crashed
    else
      echo starting
    fi
  fi
else
  echo dead
fi
""";

                var checkResult = await _sshTransport.ExecuteAsync(
                    podReference.Pod.SshCommand,
                    checkCommand,
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                status = checkResult.StandardOutput.Trim();
            }

            results.Add(new PodModelStatus(name, podReference.Name, host, deployment, status));
        }

        return results;
    }

    public async Task StreamLogsAsync(
        string name,
        string? podName = null,
        PodOutputHandler? outputHandler = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var podReference = ResolvePod(podName);
        if (!podReference.Pod.Models.ContainsKey(name))
        {
            throw new KeyNotFoundException($"Model '{name}' was not found on pod '{podReference.Name}'.");
        }

        var exitCode = await _sshTransport.ExecuteStreamingAsync(
            podReference.Pod.SshCommand,
            $"tail -f ~/.vllm_logs/{name}.log",
            (chunk, ct) => ForwardChunkAsync(chunk, outputHandler, ct),
            new SshStreamingOptions { KeepAlive = true },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (exitCode != 0 && exitCode != 130)
        {
            throw new InvalidOperationException($"Log streaming exited with code {exitCode}.");
        }
    }

    public PodEndpoint ResolveEndpoint(string deploymentName, string? podName = null, string? apiKey = null) =>
        _endpointResolver.Resolve(_store.Load(), deploymentName, podName, apiKey);

    public string FormatPods()
    {
        var configuration = _store.Load();
        if (configuration.Pods.Count == 0)
        {
            return "No pods configured. Use 'pods setup' to add a pod.";
        }

        var lines = new List<string> { "Configured pods:" };
        foreach (var (name, pod) in configuration.Pods.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var marker = string.Equals(configuration.Active, name, StringComparison.Ordinal) ? "*" : " ";
            var gpuInfo = pod.Gpus.Count > 0
                ? $"{pod.Gpus.Count}x {pod.Gpus[0].Name}"
                : "no GPUs detected";
            var vllmInfo = string.IsNullOrWhiteSpace(pod.VllmVersion) ? string.Empty : $" (vLLM: {pod.VllmVersion})";
            lines.Add($"{marker} {name} - {gpuInfo}{vllmInfo} - {pod.SshCommand}");
            if (!string.IsNullOrWhiteSpace(pod.ModelsPath))
            {
                lines.Add($"    Models: {pod.ModelsPath}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    public string FormatKnownModels(string? podName = null)
    {
        var activePod = !string.IsNullOrWhiteSpace(podName)
            ? ResolvePod(podName)
            : _store.GetActivePod();

        var lines = new List<string>();
        if (activePod is not null)
        {
            var gpuCount = activePod.Pod.Gpus.Count;
            var gpuType = KnownModelCatalog.ExtractPrimaryGpuType(activePod.Pod.Gpus);
            lines.Add($"Known models for {activePod.Name} ({gpuCount}x {gpuType}):");
            lines.Add(string.Empty);
        }
        else
        {
            lines.Add("Known models:");
            lines.Add("No active pod. Compatible/incompatible filtering is unavailable.");
            lines.Add(string.Empty);
        }

        foreach (var model in _planner.KnownModelCatalog.GetAll())
        {
            var counts = model.Configurations
                .Select(configuration => configuration.GpuCount)
                .Distinct()
                .OrderBy(count => count)
                .ToArray();
            var countsText = counts.Length == 0 ? "unknown" : string.Join(", ", counts);
            lines.Add($"{model.Id}");
            lines.Add($"  Name: {model.Name}");
            lines.Add($"  GPU counts: {countsText}");
            if (!string.IsNullOrWhiteSpace(model.Notes))
            {
                lines.Add($"  Note: {model.Notes}");
            }
        }

        return string.Join(Environment.NewLine, lines);
    }

    private async Task FollowStartupLogsAsync(
        string sshCommand,
        string deploymentName,
        PodOutputHandler? outputHandler,
        CancellationToken cancellationToken)
    {
        var failureReason = string.Empty;
        var startupComplete = false;
        var startupFailed = false;

        using var startupCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var exitCode = await _sshTransport.ExecuteStreamingAsync(
            sshCommand,
            $"tail -f ~/.vllm_logs/{deploymentName}.log",
            async (chunk, ct) =>
            {
                await ForwardChunkAsync(chunk, outputHandler, ct).ConfigureAwait(false);

                if (chunk.Text.Contains("Application startup complete", StringComparison.Ordinal))
                {
                    startupComplete = true;
                    startupCts.Cancel();
                }

                if (chunk.Text.Contains("torch.OutOfMemoryError", StringComparison.Ordinal) ||
                    chunk.Text.Contains("CUDA out of memory", StringComparison.Ordinal))
                {
                    startupFailed = true;
                    failureReason = "Out of GPU memory (OOM).";
                }

                if (chunk.Text.Contains("RuntimeError: Engine core initialization failed", StringComparison.Ordinal))
                {
                    startupFailed = true;
                    failureReason = "vLLM engine initialization failed.";
                    startupCts.Cancel();
                }

                if (chunk.Text.Contains("Model runner exiting with code", StringComparison.Ordinal) &&
                    !chunk.Text.Contains("code 0", StringComparison.Ordinal))
                {
                    startupFailed = true;
                    failureReason = "Model runner failed to start.";
                    startupCts.Cancel();
                }
            },
            new SshStreamingOptions { KeepAlive = true },
            cancellationToken: startupCts.Token).ConfigureAwait(false);

        if (startupFailed)
        {
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(failureReason)
                    ? $"Model '{deploymentName}' failed to start."
                    : $"Model '{deploymentName}' failed to start: {failureReason}");
        }

        if (!startupComplete && exitCode != 130 && exitCode != 0)
        {
            throw new InvalidOperationException($"Startup log streaming exited unexpectedly with code {exitCode}.");
        }
    }

    private PodReference ResolvePod(string? podName)
    {
        var configuration = _store.Load();

        if (!string.IsNullOrWhiteSpace(podName))
        {
            if (!configuration.Pods.TryGetValue(podName, out var namedPod))
            {
                throw new KeyNotFoundException($"Pod '{podName}' was not found.");
            }

            return new PodReference(podName, namedPod);
        }

        if (!string.IsNullOrWhiteSpace(configuration.Active) &&
            configuration.Pods.TryGetValue(configuration.Active, out var activePod))
        {
            return new PodReference(configuration.Active, activePod);
        }

        throw new InvalidOperationException("No active pod is configured.");
    }

    private async Task<IReadOnlyList<GpuInfo>> DetectGpusAsync(string sshCommand, CancellationToken cancellationToken)
    {
        var result = await _sshTransport.ExecuteAsync(
            sshCommand,
            "nvidia-smi --query-gpu=index,name,memory.total --format=csv,noheader",
            cancellationToken: cancellationToken).ConfigureAwait(false);

        var gpus = new List<GpuInfo>();
        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return gpus;
        }

        foreach (var line in result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length < 3 || !int.TryParse(parts[0], out var gpuId))
            {
                continue;
            }

            gpus.Add(
                new GpuInfo
                {
                    Id = gpuId,
                    Name = parts[1],
                    Memory = parts[2],
                });
        }

        return gpus;
    }

    private async Task UploadRemoteScriptAsync(
        string sshCommand,
        string remotePath,
        string content,
        CancellationToken cancellationToken)
    {
        var uploadCommand = $$"""
cat > {{remotePath}} <<'__PI_SHARP_SCRIPT__'
{{content}}
__PI_SHARP_SCRIPT__
chmod +x {{remotePath}}
""";

        var result = await _sshTransport.ExecuteAsync(
            sshCommand,
            uploadCommand,
            cancellationToken: cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Failed to upload remote script to '{remotePath}': {result.StandardError.Trim()}");
        }
    }

    private static IReadOnlyList<string> BuildStartEnvironmentLines(
        string apiKey,
        string huggingFaceToken,
        ModelDeploymentPlan plan,
        IReadOnlyList<int> gpuIds)
    {
        var lines = new List<string>
        {
            $"export HF_TOKEN={ShellQuote(huggingFaceToken)}",
            $"export PI_API_KEY={ShellQuote(apiKey)}",
            "export HF_HUB_ENABLE_HF_TRANSFER=1",
            "export VLLM_NO_USAGE_STATS=1",
            "export PYTORCH_CUDA_ALLOC_CONF=expandable_segments:True",
            "export FORCE_COLOR=1",
            "export TERM=xterm-256color",
        };

        if (gpuIds.Count == 1)
        {
            lines.Add($"export CUDA_VISIBLE_DEVICES={gpuIds[0]}");
        }

        foreach (var (key, value) in plan.EnvironmentVariables)
        {
            lines.Add($"export {key}={ShellQuote(value)}");
        }

        return lines;
    }

    private static string BuildWrapperScript(string deploymentName, string modelRunScriptPath) =>
        $$"""
#!/bin/bash
script -q -f -c "{{modelRunScriptPath}}" ~/.vllm_logs/{{deploymentName}}.log
exit_code=$?
echo "Script exited with code $exit_code" >> ~/.vllm_logs/{{deploymentName}}.log
exit $exit_code
""";

    private static string BuildSetupCommand(
        string modelsPath,
        string? mountCommand,
        string vllmVersion,
        string huggingFaceToken,
        string apiKey)
    {
        var builder = new StringBuilder();
        builder.Append("bash ");
        builder.Append(SetupScriptRemotePath);
        builder.Append(" --models-path ");
        builder.Append(ShellQuote(modelsPath));
        builder.Append(" --hf-token ");
        builder.Append(ShellQuote(huggingFaceToken));
        builder.Append(" --vllm-api-key ");
        builder.Append(ShellQuote(apiKey));
        builder.Append(" --vllm ");
        builder.Append(ShellQuote(vllmVersion));

        if (!string.IsNullOrWhiteSpace(mountCommand))
        {
            builder.Append(" --mount ");
            builder.Append(ShellQuote(mountCommand));
        }

        return builder.ToString();
    }

    private string ResolveRequiredValue(string? explicitValue, string environmentName, string errorMessage)
    {
        var resolved = !string.IsNullOrWhiteSpace(explicitValue)
            ? explicitValue
            : _getEnvironmentVariable(environmentName);

        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException(errorMessage);
        }

        return resolved;
    }

    private static string ResolveModelsPath(string? explicitPath, string? mountCommand)
    {
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return explicitPath;
        }

        if (!string.IsNullOrWhiteSpace(mountCommand))
        {
            var parts = mountCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var lastPart = parts.LastOrDefault();
            if (!string.IsNullOrWhiteSpace(lastPart) && lastPart.StartsWith('/'))
            {
                return lastPart;
            }
        }

        throw new InvalidOperationException("--models-path is required when it cannot be inferred from --mount.");
    }

    private static async ValueTask ForwardChunkAsync(
        SshOutputChunk chunk,
        PodOutputHandler? outputHandler,
        CancellationToken cancellationToken)
    {
        if (outputHandler is not null)
        {
            await outputHandler(
                chunk.Text,
                chunk.Stream == SshOutputStream.StandardError,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private static async ValueTask WriteInfoAsync(
        PodOutputHandler? outputHandler,
        string line,
        CancellationToken cancellationToken)
    {
        if (outputHandler is not null)
        {
            await outputHandler(line + Environment.NewLine, false, cancellationToken).ConfigureAwait(false);
        }
    }

    private static string ShellQuote(string value) =>
        $"'{value.Replace("'", "'\"'\"'", StringComparison.Ordinal)}'";
}
