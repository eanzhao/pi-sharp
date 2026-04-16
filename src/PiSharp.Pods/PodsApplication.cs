using System.Reflection;
using Microsoft.Extensions.AI;
using PiSharp.Agent;
using PiSharp.Ai;
using PiSharp.Pods.Providers;
using PiSharp.Tui;

namespace PiSharp.Pods;

public sealed class PodsConsoleEnvironment
{
    private readonly IReadOnlyDictionary<string, string?>? _environmentVariables;
    private readonly Func<bool, ConsoleKeyInfo>? _readKey;

    public PodsConsoleEnvironment(
        TextReader input,
        TextWriter output,
        TextWriter error,
        string currentDirectory,
        bool isInputRedirected,
        bool isOutputRedirected = false,
        IReadOnlyDictionary<string, string?>? environmentVariables = null,
        ITerminal? terminal = null,
        Func<bool, ConsoleKeyInfo>? readKey = null)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Output = output ?? throw new ArgumentNullException(nameof(output));
        Error = error ?? throw new ArgumentNullException(nameof(error));
        CurrentDirectory = Path.GetFullPath(currentDirectory ?? throw new ArgumentNullException(nameof(currentDirectory)));
        IsInputRedirected = isInputRedirected;
        IsOutputRedirected = isOutputRedirected;
        _environmentVariables = environmentVariables;
        Terminal = terminal ?? new ProcessTerminal(output);
        _readKey = readKey;
    }

    public TextReader Input { get; }

    public TextWriter Output { get; }

    public TextWriter Error { get; }

    public string CurrentDirectory { get; }

    public bool IsInputRedirected { get; }

    public bool IsOutputRedirected { get; }

    public bool IsInteractiveTerminal => !IsInputRedirected && !IsOutputRedirected;

    public ITerminal Terminal { get; }

    public string? GetEnvironmentVariable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_environmentVariables is not null && _environmentVariables.TryGetValue(name, out var value))
        {
            return value;
        }

        return Environment.GetEnvironmentVariable(name);
    }

    public ConsoleKeyInfo ReadKey(bool intercept = true) =>
        _readKey is not null
            ? _readKey(intercept)
            : Console.ReadKey(intercept);

    public static PodsConsoleEnvironment CreateProcessEnvironment() =>
        new(
            Console.In,
            Console.Out,
            Console.Error,
            Directory.GetCurrentDirectory(),
            Console.IsInputRedirected,
            Console.IsOutputRedirected,
            terminal: new ProcessTerminal(Console.Out),
            readKey: static intercept => Console.ReadKey(intercept));
}

public sealed class PodsApplication
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

    private readonly PodsConsoleEnvironment _environment;
    private readonly PodService _podService;
    private readonly IPodAgentFactory _podAgentFactory;
    private readonly IPodShellLauncher _podShellLauncher;
    private readonly GpuProviderRegistry _gpuProviderRegistry;
    private readonly string _appName;
    private readonly bool _namespaced;

    public PodsApplication(
        PodsConsoleEnvironment? environment = null,
        PodService? podService = null,
        IPodAgentFactory? podAgentFactory = null,
        IPodShellLauncher? podShellLauncher = null,
        GpuProviderRegistry? gpuProviderRegistry = null,
        string appName = "pisharp-pods",
        bool namespaced = false)
    {
        _environment = environment ?? PodsConsoleEnvironment.CreateProcessEnvironment();
        _podService = podService ?? new PodService(getEnvironmentVariable: _environment.GetEnvironmentVariable);
        _podAgentFactory = podAgentFactory ?? new PodAgentFactory();
        _podShellLauncher = podShellLauncher ?? new ProcessPodShellLauncher();
        _gpuProviderRegistry = gpuProviderRegistry ?? new GpuProviderRegistry();
        _appName = string.IsNullOrWhiteSpace(appName) ? "pisharp-pods" : appName.Trim();
        _namespaced = namespaced;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Count == 0 || IsHelp(args[0]))
        {
            await _environment.Output.WriteLineAsync(GetHelpText(_appName, _namespaced)).ConfigureAwait(false);
            return 0;
        }

        if (IsVersion(args[0]))
        {
            await _environment.Output.WriteLineAsync(GetVersionText()).ConfigureAwait(false);
            return 0;
        }

        try
        {
            return args[0] switch
            {
                "pods" => await RunPodsAsync(args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false),
                "setup" => await RunPodsSetupAsync(args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false),
                "active" => await RunPodsActiveAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "remove" => await RunPodsRemoveAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "start" => await RunStartAsync(args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false),
                "stop" => await RunStopAsync(args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false),
                "list" => await RunListAsync(args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false),
                "doctor" => await RunDoctorAsync(args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false),
                "logs" => await RunLogsAsync(args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false),
                "agent" => await RunAgentAsync(args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false),
                "ssh" => await RunSshAsync(args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false),
                "shell" => await RunShellAsync(args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false),
                _ => await UnknownCommandAsync(args[0]).ConfigureAwait(false),
            };
        }
        catch (Exception exception)
        {
            await _environment.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    public static string GetHelpText(string appName = "pisharp-pods", bool namespaced = false)
    {
        var rootCommand = namespaced ? $"{appName} pods" : appName;
        var podCommandRoot = namespaced ? rootCommand : $"{appName} pods";

        return
        $"""
{rootCommand} - PiSharp GPU pod CLI

Usage:
  {podCommandRoot}
  {podCommandRoot} setup <name> "<ssh>" [--provider <name>] [--mount <command>] [--models-path <path>] [--vllm release|nightly|gpt-oss]
  {podCommandRoot} active <name>
  {podCommandRoot} remove <name>
  {rootCommand} start <model> --name <name> [--memory <percent>] [--context <size>] [--gpus <count>] [--pod <name>] [--detach] [--vllm <args...>]
  {rootCommand} stop [<name>] [--pod <name>]
  {rootCommand} list [--pod <name>] [--no-verify]
  {rootCommand} doctor [<name>] [--no-verify]
  {rootCommand} logs <name> [--pod <name>] [--tail <lines>] [--no-follow]
  {rootCommand} agent <name> [message...] [--pod <name>] [--api-key <key>] [--cwd <dir>] [--thinking <level>] [-i|--interactive]
  {rootCommand} ssh [<name>] "<command>" [-t|--tty]
  {rootCommand} shell [<name>]

Examples:
  {podCommandRoot} setup dc1 "ssh root@1.2.3.4" --models-path /workspace
  {podCommandRoot} setup runpod1 "ssh root@1.2.3.4" --provider runpod
  {rootCommand} start Qwen/Qwen2.5-Coder-32B-Instruct --name qwen --detach
  {rootCommand} list
  {rootCommand} doctor
  {rootCommand} agent qwen "Summarize the repository"
  {rootCommand} logs qwen --tail 200 --no-follow
  {rootCommand} ssh "nvidia-smi" --tty
  {rootCommand} shell dc1

Providers:
  datacrunch, runpod, vastai
""";
    }

    private async Task<int> RunPodsAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            await _environment.Output.WriteLineAsync(_podService.FormatPods()).ConfigureAwait(false);
            return 0;
        }

        return args[0] switch
        {
            "setup" => await RunPodsSetupAsync(args.Skip(1).ToArray(), cancellationToken).ConfigureAwait(false),
            "active" => await RunPodsActiveAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            "remove" => await RunPodsRemoveAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
            _ => await UnknownSubcommandAsync("pods", args[0]).ConfigureAwait(false),
        };
    }

    private async Task<int> RunPodsSetupAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            throw new InvalidOperationException("Usage: pods setup <name> \"<ssh>\" [--provider <name>] [--mount <command>] [--models-path <path>] [--vllm release|nightly|gpt-oss]");
        }

        var name = args[0];
        var sshCommand = args[1];
        string? providerName = null;
        string? mountCommand = null;
        string? modelsPath = null;
        var vllmVersion = PodsDefaults.VllmRelease;

        for (var index = 2; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--provider":
                    providerName = ReadRequiredValue(args, ref index, "--provider");
                    break;
                case "--mount":
                    mountCommand = ReadRequiredValue(args, ref index, "--mount");
                    break;
                case "--models-path":
                    modelsPath = ReadRequiredValue(args, ref index, "--models-path");
                    break;
                case "--vllm":
                    vllmVersion = ReadRequiredValue(args, ref index, "--vllm");
                    break;
                default:
                    throw new InvalidOperationException($"Unknown option '{args[index]}'.");
            }
        }

        IGpuProvider? provider = null;
        if (!string.IsNullOrWhiteSpace(providerName))
        {
            provider = _gpuProviderRegistry.GetRequired(providerName);
            mountCommand ??= provider.DefaultMountCommand;
            modelsPath ??= provider.DefaultModelsPath;

            await _environment.Output.WriteLineAsync($"Using provider defaults for '{provider.Name}'.").ConfigureAwait(false);
            await _environment.Output.WriteLineAsync($"Volume: {provider.RecommendedVolumeConfig}").ConfigureAwait(false);
            await _environment.Output.WriteLineAsync(provider.SetupInstructions).ConfigureAwait(false);
        }

        var result = await _podService.SetupPodAsync(
                name,
                sshCommand,
                new PodSetupRequest
                {
                    MountCommand = mountCommand,
                    ModelsPath = modelsPath,
                    VllmVersion = vllmVersion,
                },
                WritePodOutputAsync,
                cancellationToken)
            .ConfigureAwait(false);

        await _environment.Output.WriteLineAsync($"Pod '{result.PodName}' setup complete and set as active.").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> RunPodsActiveAsync(IReadOnlyList<string> args)
    {
        if (args.Count != 1)
        {
            throw new InvalidOperationException("Usage: pods active <name>");
        }

        _podService.SetActivePod(args[0]);
        await _environment.Output.WriteLineAsync($"Active pod set to '{args[0]}'").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> RunPodsRemoveAsync(IReadOnlyList<string> args)
    {
        if (args.Count != 1)
        {
            throw new InvalidOperationException("Usage: pods remove <name>");
        }

        _podService.RemovePod(args[0]);
        await _environment.Output.WriteLineAsync($"Removed pod '{args[0]}' from local configuration.").ConfigureAwait(false);
        return 0;
    }

    private async Task<int> RunStartAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            await _environment.Output.WriteLineAsync(_podService.FormatKnownModels()).ConfigureAwait(false);
            return 0;
        }

        var modelId = args[0];
        string? name = null;
        string? podName = null;
        string? memory = null;
        string? contextWindow = null;
        int? gpuCount = null;
        var followStartupLogs = true;
        var customVllmArguments = Array.Empty<string>();

        for (var index = 1; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--name":
                    name = ReadRequiredValue(args, ref index, "--name");
                    break;
                case "--pod":
                    podName = ReadRequiredValue(args, ref index, "--pod");
                    break;
                case "--memory":
                    memory = ReadRequiredValue(args, ref index, "--memory");
                    break;
                case "--context":
                    contextWindow = ReadRequiredValue(args, ref index, "--context");
                    break;
                case "--gpus":
                    gpuCount = int.Parse(ReadRequiredValue(args, ref index, "--gpus"));
                    break;
                case "--detach":
                case "--no-follow":
                    followStartupLogs = false;
                    break;
                case "--vllm":
                    customVllmArguments = args.Skip(index + 1).ToArray();
                    index = args.Count;
                    break;
                default:
                    throw new InvalidOperationException($"Unknown option '{args[index]}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidOperationException("Usage: start <model> --name <name> [options]");
        }

        var result = await _podService.StartModelAsync(
                new PodStartRequest
                {
                    ModelId = modelId,
                    Name = name,
                    PodName = podName,
                    Memory = memory,
                    ContextWindow = contextWindow,
                    GpuCount = gpuCount,
                    FollowStartupLogs = followStartupLogs,
                    CustomVllmArguments = customVllmArguments,
                },
                WritePodOutputAsync,
                cancellationToken)
            .ConfigureAwait(false);

        await _environment.Output.WriteLineAsync().ConfigureAwait(false);
        await _environment.Output.WriteLineAsync($"Base URL: {result.BaseUri}").ConfigureAwait(false);
        await _environment.Output.WriteLineAsync($"Model: {result.Plan.ModelId}").ConfigureAwait(false);
        await _environment.Output.WriteLineAsync($"PID: {result.ProcessId}").ConfigureAwait(false);
        if (!followStartupLogs)
        {
            await _environment.Output.WriteLineAsync($"Logs: {GetRootCommand()} logs {result.Plan.Name}").ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> RunStopAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        string? name = null;
        string? podName = null;

        for (var index = 0; index < args.Count; index++)
        {
            if (args[index] == "--pod")
            {
                podName = ReadRequiredValue(args, ref index, "--pod");
                continue;
            }

            if (name is null)
            {
                name = args[index];
                continue;
            }

            throw new InvalidOperationException($"Unexpected argument '{args[index]}'.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            await _podService.StopAllModelsAsync(podName, cancellationToken).ConfigureAwait(false);
            await _environment.Output.WriteLineAsync("Stopped all models on the selected pod.").ConfigureAwait(false);
        }
        else
        {
            await _podService.StopModelAsync(name, podName, cancellationToken).ConfigureAwait(false);
            await _environment.Output.WriteLineAsync($"Stopped model '{name}'.").ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> RunListAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        string? podName = null;
        var verifyProcesses = true;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--pod":
                    podName = ReadRequiredValue(args, ref index, "--pod");
                    break;
                case "--no-verify":
                    verifyProcesses = false;
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected argument '{args[index]}'.");
            }
        }

        var models = await _podService.GetModelStatusesAsync(podName, verifyProcesses, cancellationToken).ConfigureAwait(false);
        if (models.Count == 0)
        {
            await _environment.Output.WriteLineAsync("No models running on the selected pod.").ConfigureAwait(false);
            return 0;
        }

        await _environment.Output.WriteLineAsync($"Models on pod '{models[0].PodName}':").ConfigureAwait(false);
        foreach (var model in models)
        {
            var gpuText = model.Deployment.GpuIds.Count switch
            {
                0 => "GPU unknown",
                1 => $"GPU {model.Deployment.GpuIds[0]}",
                _ => $"GPUs {string.Join(",", model.Deployment.GpuIds)}",
            };

            await _environment.Output.WriteLineAsync(
                    $"  {model.Name} - Port {model.Deployment.Port} - {gpuText} - PID {model.Deployment.ProcessId} - {model.Status}")
                .ConfigureAwait(false);
            await _environment.Output.WriteLineAsync($"    Model: {model.Deployment.ModelId}").ConfigureAwait(false);
            await _environment.Output.WriteLineAsync($"    URL: http://{model.Host}:{model.Deployment.Port}/v1").ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> RunDoctorAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        string? podName = null;
        var verifyProcesses = true;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--no-verify":
                    verifyProcesses = false;
                    break;
                default:
                    if (args[index].StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Unknown option '{args[index]}'.");
                    }

                    if (podName is null)
                    {
                        podName = args[index];
                        break;
                    }

                    throw new InvalidOperationException($"Unexpected argument '{args[index]}'.");
            }
        }

        var report = await _podService.RunDoctorAsync(
            new PodDoctorRequest
            {
                PodName = podName,
                VerifyProcesses = verifyProcesses,
            },
            cancellationToken).ConfigureAwait(false);

        await _environment.Output.WriteLineAsync(FormatDoctorReport(report)).ConfigureAwait(false);
        return report.HasFailures ? 1 : 0;
    }

    private async Task<int> RunLogsAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            throw new InvalidOperationException("Usage: logs <name> [--pod <name>] [--tail <lines>] [--no-follow]");
        }

        var name = args[0];
        string? podName = null;
        int? tailLines = null;
        var follow = true;

        for (var index = 1; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--pod":
                    podName = ReadRequiredValue(args, ref index, "--pod");
                    break;
                case "--tail":
                case "-n":
                    tailLines = int.Parse(ReadRequiredValue(args, ref index, args[index]));
                    break;
                case "--no-follow":
                    follow = false;
                    break;
                default:
                    throw new InvalidOperationException($"Unexpected argument '{args[index]}'.");
            }
        }

        await _podService.StreamLogsAsync(
            new PodLogsRequest
            {
                Name = name,
                PodName = podName,
                TailLines = tailLines,
                Follow = follow,
            },
            WritePodOutputAsync,
            cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> RunAgentAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count == 0)
        {
            throw new InvalidOperationException("Usage: agent <name> [message...] [--pod <name>] [--api-key <key>] [--cwd <dir>] [--thinking <level>]");
        }

        var deploymentName = args[0];
        string? podName = null;
        string? apiKey = null;
        string? workingDirectory = null;
        var thinkingLevel = ThinkingLevel.Off;
        var interactive = false;
        var messages = new List<string>();

        for (var index = 1; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--pod":
                    podName = ReadRequiredValue(args, ref index, "--pod");
                    break;
                case "--api-key":
                    apiKey = ReadRequiredValue(args, ref index, "--api-key");
                    break;
                case "--cwd":
                case "--working-directory":
                    workingDirectory = ReadRequiredValue(args, ref index, args[index]);
                    break;
                case "--thinking":
                    var level = ReadRequiredValue(args, ref index, "--thinking");
                    if (!ThinkingLevels.TryGetValue(level, out thinkingLevel))
                    {
                        throw new InvalidOperationException($"Invalid thinking level '{level}'.");
                    }

                    break;
                case "-i":
                case "--interactive":
                    interactive = true;
                    break;
                default:
                    if (args[index].StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Unknown option '{args[index]}'.");
                    }

                    messages.Add(args[index]);
                    break;
            }
        }

        var prompt = await BuildPromptAsync(messages).ConfigureAwait(false);
        if (interactive && !_environment.IsInteractiveTerminal)
        {
            throw new InvalidOperationException("Interactive mode requires an attached terminal.");
        }

        interactive = interactive || (string.IsNullOrWhiteSpace(prompt) && _environment.IsInteractiveTerminal);

        if (!interactive && string.IsNullOrWhiteSpace(prompt))
        {
            throw new InvalidOperationException("No prompt provided. Pass a message, pipe stdin, or use -i.");
        }

        var endpoint = _podService.ResolveEndpoint(deploymentName, podName, apiKey);
        var agent = _podAgentFactory.Create(
            endpoint,
            new PodAgentFactoryOptions
            {
                ApiKey = apiKey,
                WorkingDirectory = workingDirectory ?? _environment.CurrentDirectory,
                ThinkingLevel = thinkingLevel,
            });

        return interactive
            ? await RunInteractiveAgentAsync(agent, endpoint, prompt, cancellationToken).ConfigureAwait(false)
            : await RunPrintModeAgentAsync(agent, prompt!, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RunSshAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var positionalArguments = new List<string>();
        var forceTty = false;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "-t":
                case "--tty":
                    forceTty = true;
                    break;
                default:
                    if (args[index].StartsWith("-", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Unknown option '{args[index]}'.");
                    }

                    positionalArguments.Add(args[index]);
                    break;
            }
        }

        if (positionalArguments.Count == 0 || positionalArguments.Count > 2)
        {
            throw new InvalidOperationException("Usage: ssh [<name>] \"<command>\" [-t|--tty]");
        }

        var podName = positionalArguments.Count == 2 ? positionalArguments[0] : null;
        var command = positionalArguments.Count == 2 ? positionalArguments[1] : positionalArguments[0];

        return await _podService.ExecuteSshCommandAsync(
            command,
            podName,
            WritePodOutputAsync,
            forceTty,
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> RunShellAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (args.Count > 1)
        {
            throw new InvalidOperationException("Usage: shell [<name>]");
        }

        var podReference = _podService.ResolvePodReference(args.Count == 1 ? args[0] : null);
        return await _podShellLauncher.LaunchAsync(podReference.Pod.SshCommand, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> BuildPromptAsync(IReadOnlyList<string> messages)
    {
        var parts = new List<string>();

        if (_environment.IsInputRedirected)
        {
            var stdin = await _environment.Input.ReadToEndAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stdin))
            {
                parts.Add(stdin.Trim());
            }
        }

        if (messages.Count > 0)
        {
            parts.Add(string.Join(' ', messages));
        }

        return string.Join("\n\n", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private async Task<int> UnknownCommandAsync(string command)
    {
        await _environment.Error.WriteLineAsync($"Unknown command '{command}'.").ConfigureAwait(false);
        return 1;
    }

    private async Task<int> RunPrintModeAgentAsync(PiSharp.Agent.Agent agent, string prompt, CancellationToken cancellationToken)
    {
        var reporter = new StreamingConsoleReporter(_environment.Output, _environment.Error);
        agent.Subscribe(reporter.OnAgentEventAsync);
        await agent.PromptAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        await reporter.FlushAsync().ConfigureAwait(false);
        return 0;
    }

    private async Task<int> RunInteractiveAgentAsync(
        PiSharp.Agent.Agent agent,
        PodEndpoint endpoint,
        string? initialPrompt,
        CancellationToken cancellationToken)
    {
        var app = new TuiApplication(_environment.Terminal);
        var view = new PodsInteractiveView();
        var controller = new PodsInteractiveController(agent, view, endpoint);

        agent.Subscribe(
            async (@event, ct) =>
            {
                await controller.OnAgentEventAsync(@event, ct).ConfigureAwait(false);
                await app.RenderAsync(cancellationToken: ct).ConfigureAwait(false);
            });

        app.AddChild(view);
        app.SetFocus(view);
        await app.RenderAsync(forceFullRedraw: true, cancellationToken: cancellationToken).ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(initialPrompt))
        {
            await controller.SubmitAsync(initialPrompt, cancellationToken).ConfigureAwait(false);
            await app.RenderAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        while (!controller.ShouldExit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var keyInfo = _environment.ReadKey(intercept: true);
            if (PodsConsoleKeyMapper.IsExitKey(keyInfo))
            {
                break;
            }

            var rawInput = PodsConsoleKeyMapper.ToRawInput(keyInfo);
            if (string.IsNullOrEmpty(rawInput))
            {
                continue;
            }

            string? submittedPrompt = null;
            void HandleSubmitted(string prompt) => submittedPrompt = prompt;

            view.Submitted += HandleSubmitted;
            try
            {
                await app.HandleInputAsync(rawInput, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                view.Submitted -= HandleSubmitted;
            }

            if (submittedPrompt is null)
            {
                continue;
            }

            await controller.SubmitAsync(submittedPrompt, cancellationToken).ConfigureAwait(false);
            await app.RenderAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await _environment.Terminal.WriteAsync($"{Ansi.ShowCursor}{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> UnknownSubcommandAsync(string command, string subcommand)
    {
        await _environment.Error.WriteLineAsync($"Unknown subcommand '{command} {subcommand}'.").ConfigureAwait(false);
        return 1;
    }

    private static bool IsHelp(string value) => value is "--help" or "-h";

    private static bool IsVersion(string value) => value is "--version" or "-v";

    private static string ReadRequiredValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        if (index + 1 >= args.Count)
        {
            throw new InvalidOperationException($"Missing value for '{optionName}'.");
        }

        return args[++index];
    }

    private static string GetVersionText() =>
        typeof(PodsApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(PodsApplication).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private string GetRootCommand() => _namespaced ? $"{_appName} pods" : _appName;

    private static string FormatDoctorReport(PodDoctorReport report)
    {
        var lines = new List<string>
        {
            $"Doctor report for pod '{report.PodName}' ({report.Host})",
            $"SSH: {report.SshCommand}",
            $"Models path: {report.ModelsPath ?? "(not configured)"}",
            $"vLLM channel: {report.VllmVersion}",
            string.Empty,
        };

        foreach (var check in report.Checks)
        {
            lines.Add($"[{FormatDoctorStatus(check.Status)}] {check.Name}: {check.Summary}");
            if (!string.IsNullOrWhiteSpace(check.Details))
            {
                foreach (var detailLine in check.Details.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    lines.Add($"  {detailLine}");
                }
            }
        }

        if (report.Deployments.Count > 0)
        {
            lines.Add(string.Empty);
            lines.Add("Deployments:");
            foreach (var deployment in report.Deployments)
            {
                lines.Add(
                    $"  {deployment.Name} - {deployment.Status} - {deployment.Deployment.ModelId} - http://{deployment.Host}:{deployment.Deployment.Port}/v1");
            }
        }

        lines.Add(string.Empty);
        lines.Add(report.HasFailures ? "Overall: issues detected" : report.HasWarnings ? "Overall: healthy with warnings" : "Overall: healthy");
        return string.Join(Environment.NewLine, lines);
    }

    private static string FormatDoctorStatus(PodDoctorCheckStatus status) =>
        status switch
        {
            PodDoctorCheckStatus.Pass => "pass",
            PodDoctorCheckStatus.Warning => "warn",
            PodDoctorCheckStatus.Fail => "fail",
            _ => "unknown",
        };

    private async ValueTask WritePodOutputAsync(string text, bool isError, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var writer = isError ? _environment.Error : _environment.Output;
        await writer.WriteAsync(text).ConfigureAwait(false);
        await writer.FlushAsync().ConfigureAwait(false);
    }

    private sealed class StreamingConsoleReporter(TextWriter output, TextWriter error)
    {
        private bool _assistantLineOpen;

        public ValueTask OnAgentEventAsync(AgentEvent @event, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (@event)
            {
                case AgentEvent.MessageUpdated { AssistantMessageEvent: AssistantMessageEvent.TextDelta textDelta }:
                    output.Write(textDelta.Text);
                    _assistantLineOpen = true;
                    break;

                case AgentEvent.MessageCompleted completed when completed.Message.Role == ChatRole.Assistant:
                    if (_assistantLineOpen)
                    {
                        output.WriteLine();
                        _assistantLineOpen = false;
                    }

                    break;

                case AgentEvent.ToolExecutionCompleted toolExecutionCompleted when toolExecutionCompleted.IsError:
                    if (_assistantLineOpen)
                    {
                        output.WriteLine();
                        _assistantLineOpen = false;
                    }

                    error.WriteLine($"[tool:error] {toolExecutionCompleted.ToolName} failed");
                    break;
            }

            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync()
        {
            if (_assistantLineOpen)
            {
                output.WriteLine();
                _assistantLineOpen = false;
            }

            output.Flush();
            error.Flush();
            return ValueTask.CompletedTask;
        }
    }
}
