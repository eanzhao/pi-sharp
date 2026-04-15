using System.Reflection;
using Microsoft.Extensions.AI;
using PiSharp.Agent;
using PiSharp.Ai;
using PiSharp.CodingAgent;

namespace PiSharp.Cli;

public sealed class CliEnvironment
{
    private readonly IReadOnlyDictionary<string, string?>? _environmentVariables;

    public CliEnvironment(
        TextReader input,
        TextWriter output,
        TextWriter error,
        string currentDirectory,
        bool isInputRedirected,
        IReadOnlyDictionary<string, string?>? environmentVariables = null)
    {
        Input = input ?? throw new ArgumentNullException(nameof(input));
        Output = output ?? throw new ArgumentNullException(nameof(output));
        Error = error ?? throw new ArgumentNullException(nameof(error));
        CurrentDirectory = Path.GetFullPath(currentDirectory ?? throw new ArgumentNullException(nameof(currentDirectory)));
        IsInputRedirected = isInputRedirected;
        _environmentVariables = environmentVariables;
    }

    public TextReader Input { get; }

    public TextWriter Output { get; }

    public TextWriter Error { get; }

    public string CurrentDirectory { get; }

    public bool IsInputRedirected { get; }

    public string? GetEnvironmentVariable(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (_environmentVariables is not null && _environmentVariables.TryGetValue(name, out var value))
        {
            return value;
        }

        return Environment.GetEnvironmentVariable(name);
    }

    public static CliEnvironment CreateProcessEnvironment() =>
        new(
            Console.In,
            Console.Out,
            Console.Error,
            Directory.GetCurrentDirectory(),
            Console.IsInputRedirected);
}

public sealed class CliApplication
{
    private readonly CliEnvironment _environment;
    private readonly CliProviderCatalog _providerCatalog;

    public CliApplication(CliEnvironment? environment = null, CliProviderCatalog? providerCatalog = null)
    {
        _environment = environment ?? CliEnvironment.CreateProcessEnvironment();
        _providerCatalog = providerCatalog ?? CliProviderCatalog.CreateDefault();
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        var parsed = CliArgumentsParser.Parse(args);
        ReportDiagnostics(parsed.Diagnostics.Where(static diagnostic => diagnostic.Severity == CliDiagnosticSeverity.Warning));

        var errors = parsed.Diagnostics.Where(static diagnostic => diagnostic.Severity == CliDiagnosticSeverity.Error).ToArray();
        if (errors.Length > 0)
        {
            ReportDiagnostics(errors);
            return 1;
        }

        if (parsed.Help)
        {
            await _environment.Output.WriteLineAsync(CliArgumentsParser.GetHelpText()).ConfigureAwait(false);
            return 0;
        }

        if (parsed.Version)
        {
            await _environment.Output.WriteLineAsync(GetVersionText()).ConfigureAwait(false);
            return 0;
        }

        if (parsed.ListModels)
        {
            await ListModelsAsync(parsed.ListModelsFilter).ConfigureAwait(false);
            return 0;
        }

        var workingDirectory = Path.GetFullPath(parsed.WorkingDirectory ?? _environment.CurrentDirectory);
        var providerName = parsed.Provider ?? ProviderId.OpenAi.Value;
        if (!_providerCatalog.TryGet(providerName, out var providerFactory) || providerFactory is null)
        {
            await _environment.Error.WriteLineAsync($"Unknown provider '{providerName}'.").ConfigureAwait(false);
            return 1;
        }

        var resolvedProviderFactory = providerFactory;

        var modelId = parsed.Model ?? resolvedProviderFactory.Configuration.DefaultModelId;
        if (string.IsNullOrWhiteSpace(modelId))
        {
            await _environment.Error.WriteLineAsync(
                $"No model configured for provider '{resolvedProviderFactory.Configuration.ProviderId.Value}'. Use --model.")
                .ConfigureAwait(false);
            return 1;
        }

        var apiKey = ResolveApiKey(parsed, resolvedProviderFactory);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            var envVar = resolvedProviderFactory.Configuration.ApiKeyEnvironmentVariable ?? "provider-specific environment variable";
            await _environment.Error.WriteLineAsync(
                    $"Missing API key for provider '{resolvedProviderFactory.Configuration.ProviderId.Value}'. Use --api-key or set {envVar}.")
                .ConfigureAwait(false);
            return 1;
        }

        var initialPrompt = await BuildInitialPromptAsync(parsed, workingDirectory).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(initialPrompt))
        {
            await _environment.Error.WriteLineAsync("No input provided. Pass a message argument, @file, or pipe stdin.").ConfigureAwait(false);
            return 1;
        }

        var contextFiles = parsed.NoContextFiles
            ? Array.Empty<CodingAgentContextFile>()
            : CliContextLoader.Load(workingDirectory);

        var appendSystemPrompt = BuildAppendSystemPrompt(parsed.AppendSystemPromptInputs, workingDirectory);
        var activeToolNames = ResolveActiveToolNames(parsed);
        var model = resolvedProviderFactory.ResolveModel(modelId);

        try
        {
            var reporter = new StreamingConsoleReporter(_environment.Output, _environment.Error, parsed.Verbose);
            using var session = await CodingAgentSession.CreateAsync(
                    resolvedProviderFactory.Create(modelId, apiKey),
                    new CodingAgentSessionOptions
                    {
                        Model = model,
                        WorkingDirectory = workingDirectory,
                        ThinkingLevel = parsed.ThinkingLevel,
                        ActiveToolNames = activeToolNames,
                        ContextFiles = contextFiles,
                        CustomSystemPrompt = parsed.SystemPrompt,
                        AppendSystemPrompt = appendSystemPrompt,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            session.Subscribe(reporter.OnAgentEventAsync);
            await session.PromptAsync(initialPrompt, cancellationToken: cancellationToken).ConfigureAwait(false);
            await reporter.FlushAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            await _environment.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private async Task ListModelsAsync(string? filter)
    {
        var models = _providerCatalog.GetAll()
            .SelectMany(static provider => provider.KnownModels)
            .Where(model =>
                string.IsNullOrWhiteSpace(filter) ||
                $"{model.ProviderId.Value}/{model.Id}".Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                model.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(model => model.ProviderId.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(model => model.Id, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (models.Length == 0)
        {
            await _environment.Output.WriteLineAsync(
                    string.IsNullOrWhiteSpace(filter)
                        ? "No known models are registered."
                        : $"No models matching '{filter}'.")
                .ConfigureAwait(false);
            return;
        }

        var providerWidth = Math.Max("provider".Length, models.Max(model => model.ProviderId.Value.Length));
        var modelWidth = Math.Max("model".Length, models.Max(model => model.Id.Length));
        var contextWidth = Math.Max("context".Length, models.Max(model => FormatTokenCount(model.ContextWindow).Length));
        var outputWidth = Math.Max("max-out".Length, models.Max(model => FormatTokenCount(model.MaxOutputTokens).Length));

        await _environment.Output.WriteLineAsync(
                $"{Pad("provider", providerWidth)}  {Pad("model", modelWidth)}  {Pad("context", contextWidth)}  {Pad("max-out", outputWidth)}")
            .ConfigureAwait(false);

        foreach (var model in models)
        {
            await _environment.Output.WriteLineAsync(
                    $"{Pad(model.ProviderId.Value, providerWidth)}  {Pad(model.Id, modelWidth)}  {Pad(FormatTokenCount(model.ContextWindow), contextWidth)}  {Pad(FormatTokenCount(model.MaxOutputTokens), outputWidth)}")
                .ConfigureAwait(false);
        }
    }

    private async Task<string> BuildInitialPromptAsync(CliArguments parsed, string workingDirectory)
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

        if (parsed.FileArguments.Count > 0)
        {
            parts.Add(CliContextLoader.LoadFileArgumentText(parsed.FileArguments, workingDirectory));
        }

        if (parsed.Messages.Count > 0)
        {
            parts.Add(string.Join(' ', parsed.Messages));
        }

        return string.Join("\n\n", parts.Where(static part => !string.IsNullOrWhiteSpace(part)));
    }

    private string? BuildAppendSystemPrompt(IEnumerable<string> promptInputs, string workingDirectory)
    {
        var resolvedInputs = promptInputs
            .Select(promptInput => CliContextLoader.ResolvePromptInput(promptInput, workingDirectory))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return resolvedInputs.Length == 0
            ? null
            : string.Join("\n\n", resolvedInputs);
    }

    private static IReadOnlyList<string> ResolveActiveToolNames(CliArguments parsed)
    {
        if (parsed.NoTools)
        {
            return Array.Empty<string>();
        }

        return parsed.Tools is { Count: > 0 }
            ? parsed.Tools
            : BuiltInToolNames.Default;
    }

    private string? ResolveApiKey(CliArguments parsed, CliProviderFactory providerFactory)
    {
        if (!string.IsNullOrWhiteSpace(parsed.ApiKey))
        {
            return parsed.ApiKey;
        }

        if (string.IsNullOrWhiteSpace(providerFactory.Configuration.ApiKeyEnvironmentVariable))
        {
            return null;
        }

        return _environment.GetEnvironmentVariable(providerFactory.Configuration.ApiKeyEnvironmentVariable);
    }

    private void ReportDiagnostics(IEnumerable<CliDiagnostic> diagnostics)
    {
        foreach (var diagnostic in diagnostics)
        {
            _environment.Error.WriteLine(
                diagnostic.Severity == CliDiagnosticSeverity.Warning
                    ? $"Warning: {diagnostic.Message}"
                    : $"Error: {diagnostic.Message}");
        }
    }

    private static string GetVersionText() =>
        typeof(CliApplication).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CliApplication).Assembly.GetName().Version?.ToString()
        ?? "0.0.0";

    private static string Pad(string value, int width) => value.PadRight(width);

    private static string FormatTokenCount(int value)
    {
        if (value >= 1_000_000)
        {
            var millions = value / 1_000_000d;
            return millions % 1 == 0
                ? $"{millions:0}M"
                : $"{millions:0.#}M";
        }

        if (value >= 1_000)
        {
            var thousands = value / 1_000d;
            return thousands % 1 == 0
                ? $"{thousands:0}K"
                : $"{thousands:0.#}K";
        }

        return value.ToString();
    }

    private sealed class StreamingConsoleReporter(TextWriter output, TextWriter error, bool verbose)
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

                case AgentEvent.MessageCompleted messageCompleted when messageCompleted.Message.Role == ChatRole.Assistant:
                    if (_assistantLineOpen)
                    {
                        output.WriteLine();
                        _assistantLineOpen = false;
                    }

                    break;

                case AgentEvent.ToolExecutionStarted toolExecutionStarted when verbose:
                    WriteDiagnostic($"[tool] {toolExecutionStarted.ToolName} started");
                    break;

                case AgentEvent.ToolExecutionCompleted toolExecutionCompleted when verbose || toolExecutionCompleted.IsError:
                    WriteDiagnostic(
                        toolExecutionCompleted.IsError
                            ? $"[tool:error] {toolExecutionCompleted.ToolName} failed"
                            : $"[tool] {toolExecutionCompleted.ToolName} completed");
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

        private void WriteDiagnostic(string message)
        {
            if (_assistantLineOpen)
            {
                output.WriteLine();
                _assistantLineOpen = false;
            }

            error.WriteLine(message);
        }
    }
}
