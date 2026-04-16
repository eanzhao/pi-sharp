using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Trace;
using PiSharp.Agent;
using PiSharp.Ai;
using PiSharp.CodingAgent;
using PiSharp.Mom;
using PiSharp.Pods;
using PiSharp.Tui;

namespace PiSharp.Cli;

public sealed class CliEnvironment
{
    private readonly IReadOnlyDictionary<string, string?>? _environmentVariables;
    private readonly Func<bool, ConsoleKeyInfo>? _readKey;

    public CliEnvironment(
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

    public string GetHomeDirectory()
    {
        var home =
            GetEnvironmentVariable("HOME")
            ?? GetEnvironmentVariable("USERPROFILE")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        return string.IsNullOrWhiteSpace(home)
            ? CurrentDirectory
            : Path.GetFullPath(home);
    }

    public ConsoleKeyInfo ReadKey(bool intercept = true) =>
        _readKey is not null
            ? _readKey(intercept)
            : Console.ReadKey(intercept);

    public static CliEnvironment CreateProcessEnvironment() =>
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

public sealed class CliApplication
{
    private const string OpenTelemetryChatSourceName = "PiSharp.Cli.ChatClient";

    private static readonly string[] PodEnvironmentVariables =
    [
        PodsDefaults.PiApiKeyEnvironmentVariable,
        PodsDefaults.PiConfigDirectoryEnvironmentVariable,
        "HF_TOKEN",
        "HOME",
        "USERPROFILE",
    ];

    private static readonly string[] MomEnvironmentVariables =
    [
        MomDefaults.SlackAppTokenEnvironmentVariable,
        MomDefaults.SlackBotTokenEnvironmentVariable,
        "OPENAI_API_KEY",
        "ANTHROPIC_API_KEY",
        "GOOGLE_API_KEY",
        "HOME",
        "USERPROFILE",
    ];

    private readonly CliEnvironment _environment;
    private readonly CodingAgentProviderCatalog _providerCatalog;
    private readonly ProviderModelDiscoveryService _modelDiscoveryService;
    private readonly Func<string, string, SettingsManager> _createSettingsManager;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<int>> _runPodsCommand;
    private readonly Func<IReadOnlyList<string>, CancellationToken, Task<int>> _runMomCommand;

    public CliApplication(
        CliEnvironment? environment = null,
        CodingAgentProviderCatalog? providerCatalog = null,
        ProviderModelDiscoveryService? modelDiscoveryService = null,
        Func<string, string, SettingsManager>? createSettingsManager = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<int>>? runPodsCommand = null,
        Func<IReadOnlyList<string>, CancellationToken, Task<int>>? runMomCommand = null)
    {
        _environment = environment ?? CliEnvironment.CreateProcessEnvironment();
        _providerCatalog = providerCatalog ?? CodingAgentProviderCatalog.CreateDefault();
        _modelDiscoveryService = modelDiscoveryService
            ?? new ProviderModelDiscoveryService(Path.Combine(_environment.GetHomeDirectory(), ".pi-sharp", "cache", "models"));
        _createSettingsManager = createSettingsManager ?? SettingsManager.Create;
        _runPodsCommand = runPodsCommand ?? RunPodsCommandAsync;
        _runMomCommand = runMomCommand ?? RunMomCommandAsync;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (IsPodsCommand(args))
        {
            return await _runPodsCommand(NormalizePodsArgs(args), cancellationToken).ConfigureAwait(false);
        }

        if (IsMomCommand(args))
        {
            return await _runMomCommand(NormalizeMomArgs(args), cancellationToken).ConfigureAwait(false);
        }

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
            await ListModelsAsync(parsed.ListModelsFilter, cancellationToken).ConfigureAwait(false);
            return 0;
        }

        var workingDirectory = Path.GetFullPath(parsed.WorkingDirectory ?? _environment.CurrentDirectory);
        var agentDirectory = Path.Combine(_environment.GetHomeDirectory(), ".pi-sharp");
        var bootstrap = new CodingAgentRuntimeBootstrap(
            _providerCatalog,
            _createSettingsManager(workingDirectory, agentDirectory));

        try
        {
            var initialPrompt = await BuildInitialPromptAsync(parsed, workingDirectory).ConfigureAwait(false);
            var persistedSource = await LoadSourceSessionAsync(parsed, bootstrap, workingDirectory, cancellationToken).ConfigureAwait(false);

            var reusePersistedSystemPrompt =
                string.IsNullOrWhiteSpace(parsed.SystemPrompt) &&
                parsed.AppendSystemPromptInputs.Count == 0 &&
                !string.IsNullOrWhiteSpace(persistedSource?.Context?.SystemPrompt);

            var runConfiguration = bootstrap.Resolve(
                new CodingAgentBootstrapRequest
                {
                    WorkingDirectory = workingDirectory,
                    Provider = parsed.Provider,
                    Model = parsed.Model,
                    ApiKey = parsed.ApiKey,
                    ThinkingLevel = parsed.ThinkingLevel,
                    SessionDirectory = parsed.SessionDirectory,
                    ExistingSession = persistedSource?.Context,
                    LoadContextFiles = !parsed.NoContextFiles && !reusePersistedSystemPrompt,
                },
                _environment.GetEnvironmentVariable);

            var interactive = ShouldRunInteractive(parsed, initialPrompt);
            if (!interactive && initialPrompt is null)
            {
                await _environment.Error.WriteLineAsync("No input provided. Pass a message argument, @file, or pipe stdin.").ConfigureAwait(false);
                return 1;
            }

            var appendSystemPrompt = BuildAppendSystemPrompt(parsed.AppendSystemPromptInputs, workingDirectory);
            var activeToolNames = ResolveActiveToolNames(parsed, persistedSource?.Context?.ToolNames);

            var rawClient = runConfiguration.ProviderFactory.Create(runConfiguration.Model.Id, runConfiguration.ApiKey);
            var chatClient = WrapWithMiddleware(rawClient, parsed.Verbose, parsed.OtelEndpoint);

            using var session = await CodingAgentSession.CreateAsync(
                    chatClient,
                    new CodingAgentSessionOptions
                    {
                        Model = runConfiguration.Model,
                        WorkingDirectory = workingDirectory,
                        ThinkingLevel = runConfiguration.ThinkingLevel,
                        ActiveToolNames = activeToolNames,
                        ContextFiles = runConfiguration.ContextFiles,
                        Messages = persistedSource?.Context?.Messages,
                        CustomSystemPrompt = parsed.SystemPrompt,
                        AppendSystemPrompt = appendSystemPrompt,
                        OverrideSystemPrompt = reusePersistedSystemPrompt ? persistedSource?.Context?.SystemPrompt : null,
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            var persistenceManager = PreparePersistenceManager(
                parsed,
                workingDirectory,
                runConfiguration.SessionDirectory,
                session,
                runConfiguration,
                persistedSource);

            if (interactive)
            {
                return await RunInteractiveAsync(
                        session,
                        persistenceManager,
                        runConfiguration.ProviderFactory.Configuration.ProviderId.Value,
                        runConfiguration.Model.Id,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            if (parsed.Json)
            {
                return await RunJsonModeAsync(session, persistenceManager, initialPrompt!, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await RunPrintModeAsync(session, persistenceManager, initialPrompt!, parsed.Verbose, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            await _environment.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    private async Task<int> RunJsonModeAsync(
        CodingAgentSession session,
        SessionManager? persistenceManager,
        ChatMessage prompt,
        CancellationToken cancellationToken)
    {
        var persistedMessageCount = session.State.Messages.Count;
        await session.PromptAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        PersistNewMessages(persistenceManager, session, ref persistedMessageCount);

        var assistantMessages = session.State.Messages
            .Where(static message => message.Role == ChatRole.Assistant)
            .ToArray();

        var lastAssistant = assistantMessages.LastOrDefault();
        var jsonResponse = new JsonOutputResponse
        {
            Role = lastAssistant?.Role.Value ?? "assistant",
            Content = lastAssistant?.Text ?? string.Empty,
            Model = session.State.Model?.Id,
            ToolCalls = assistantMessages
                .SelectMany(static message => message.Contents.OfType<FunctionCallContent>())
                .Select(static toolCall => new JsonToolCall
                {
                    Id = toolCall.CallId,
                    Name = toolCall.Name,
                    Arguments = toolCall.Arguments,
                })
                .ToArray(),
            MessageCount = session.State.Messages.Count,
        };

        var json = JsonSerializer.Serialize(jsonResponse, JsonOutputOptions);
        await _environment.Output.WriteLineAsync(json).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> RunPrintModeAsync(
        CodingAgentSession session,
        SessionManager? persistenceManager,
        ChatMessage prompt,
        bool verbose,
        CancellationToken cancellationToken)
    {
        var persistedMessageCount = session.State.Messages.Count;
        var reporter = new StreamingConsoleReporter(_environment.Output, _environment.Error, verbose);
        session.Subscribe(reporter.OnAgentEventAsync);

        await session.PromptAsync(prompt, cancellationToken: cancellationToken).ConfigureAwait(false);
        PersistNewMessages(persistenceManager, session, ref persistedMessageCount);
        await reporter.FlushAsync().ConfigureAwait(false);
        return 0;
    }

    private Task<int> RunPodsCommandAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var podsEnvironmentVariables = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var variableName in PodEnvironmentVariables)
        {
            var value = _environment.GetEnvironmentVariable(variableName);
            if (value is not null)
            {
                podsEnvironmentVariables[variableName] = value;
            }
        }

        var podsApplication = new PodsApplication(
            new PodsConsoleEnvironment(
                _environment.Input,
                _environment.Output,
                _environment.Error,
                _environment.CurrentDirectory,
                _environment.IsInputRedirected,
                isOutputRedirected: _environment.IsOutputRedirected,
                environmentVariables: podsEnvironmentVariables,
                terminal: _environment.Terminal,
                readKey: _environment.ReadKey),
            appName: "pisharp",
            namespaced: true);

        return podsApplication.RunAsync(args, cancellationToken);
    }

    private Task<int> RunMomCommandAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var momEnvironmentVariables = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var variableName in MomEnvironmentVariables)
        {
            var value = _environment.GetEnvironmentVariable(variableName);
            if (value is not null)
            {
                momEnvironmentVariables[variableName] = value;
            }
        }

        var momApplication = new MomApplication(
            new MomConsoleEnvironment(
                _environment.Input,
                _environment.Output,
                _environment.Error,
                _environment.CurrentDirectory,
                momEnvironmentVariables),
            appName: "pisharp",
            namespaced: true,
            providerCatalog: _providerCatalog,
            createSettingsManager: _createSettingsManager);

        return momApplication.RunAsync(args, cancellationToken);
    }

    private static bool IsPodsCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], "pods", StringComparison.Ordinal);

    private static bool IsMomCommand(IReadOnlyList<string> args) =>
        args.Count > 0 && string.Equals(args[0], "mom", StringComparison.Ordinal);

    private static IReadOnlyList<string> NormalizePodsArgs(IReadOnlyList<string> args) =>
        args.Count == 1
            ? ["pods"]
            : args.Skip(1).ToArray();

    private static IReadOnlyList<string> NormalizeMomArgs(IReadOnlyList<string> args) =>
        args.Count == 1
            ? ["mom"]
            : args.Skip(1).ToArray();

    private async Task<int> RunInteractiveAsync(
        CodingAgentSession session,
        SessionManager? persistenceManager,
        string providerName,
        string modelId,
        CancellationToken cancellationToken)
    {
        var persistedMessageCount = session.State.Messages.Count;
        var app = new TuiApplication(_environment.Terminal);
        var view = new CliInteractiveView();
        var controller = new CliInteractiveController(
            session,
            view,
            providerName,
            modelId,
            persistenceManager?.Header?.Id,
            persistenceManager is not null);

        session.Subscribe(
            async (@event, ct) =>
            {
                await controller.OnAgentEventAsync(@event, ct).ConfigureAwait(false);
                await app.RenderAsync(cancellationToken: ct).ConfigureAwait(false);
            });

        app.AddChild(view);
        app.SetFocus(view);
        await app.RenderAsync(forceFullRedraw: true, cancellationToken: cancellationToken).ConfigureAwait(false);

        while (!controller.ShouldExit)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var keyInfo = _environment.ReadKey(intercept: true);
            if (ConsoleKeyMapper.IsExitKey(keyInfo))
            {
                break;
            }

            var rawInput = ConsoleKeyMapper.ToRawInput(keyInfo);
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
            PersistNewMessages(persistenceManager, session, ref persistedMessageCount);
            await app.RenderAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        await _environment.Terminal.WriteAsync($"{Ansi.ShowCursor}{Environment.NewLine}", cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private async Task<PersistedSessionSource?> LoadSourceSessionAsync(
        CliArguments parsed,
        CodingAgentRuntimeBootstrap bootstrap,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        if (parsed.NoSession)
        {
            return null;
        }

        var selector = parsed.ResumeSession ?? parsed.ForkSession;
        if (string.IsNullOrWhiteSpace(selector))
        {
            return null;
        }

        var manager = new SessionManager(
            bootstrap.ResolveSessionDirectory(parsed.SessionDirectory, workingDirectory),
            workingDirectory);
        var sessionFile = manager.ResolveSessionFile(selector);
        await manager.LoadSessionAsync(sessionFile, cancellationToken).ConfigureAwait(false);
        return new PersistedSessionSource(manager, manager.BuildContext());
    }

    private SessionManager? PreparePersistenceManager(
        CliArguments parsed,
        string workingDirectory,
        string sessionDirectory,
        CodingAgentSession session,
        CodingAgentRunConfiguration runConfiguration,
        PersistedSessionSource? source)
    {
        if (parsed.NoSession)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(parsed.ForkSession) && source is not null)
        {
            var forkedManager = new SessionManager(sessionDirectory, workingDirectory);
            forkedManager.NewSession(
                parentSession: source.Manager.Header?.Id,
                providerId: runConfiguration.ProviderFactory.Configuration.ProviderId.Value,
                modelId: runConfiguration.Model.Id,
                thinkingLevel: runConfiguration.ThinkingLevel.ToString().ToLowerInvariant(),
                systemPrompt: session.SystemPrompt,
                toolNames: session.ActiveToolNames);

            foreach (var entry in source.Manager.GetBranch())
            {
                forkedManager.AppendEntry(CloneEntry(entry));
            }

            return forkedManager;
        }

        if (!string.IsNullOrWhiteSpace(parsed.ResumeSession) && source is not null)
        {
            source.Manager.UpdateHeader(header => header with
            {
                Cwd = workingDirectory,
                ProviderId = runConfiguration.ProviderFactory.Configuration.ProviderId.Value,
                ModelId = runConfiguration.Model.Id,
                ThinkingLevel = runConfiguration.ThinkingLevel.ToString().ToLowerInvariant(),
                SystemPrompt = session.SystemPrompt,
                ToolNames = session.ActiveToolNames.ToArray(),
            });

            return source.Manager;
        }

        var manager = new SessionManager(sessionDirectory, workingDirectory);
        manager.NewSession(
            providerId: runConfiguration.ProviderFactory.Configuration.ProviderId.Value,
            modelId: runConfiguration.Model.Id,
            thinkingLevel: runConfiguration.ThinkingLevel.ToString().ToLowerInvariant(),
            systemPrompt: session.SystemPrompt,
            toolNames: session.ActiveToolNames);

        return manager;
    }

    private static void PersistNewMessages(
        SessionManager? persistenceManager,
        CodingAgentSession session,
        ref int persistedMessageCount)
    {
        if (persistenceManager is null)
        {
            return;
        }

        var messages = session.State.Messages;
        for (var index = persistedMessageCount; index < messages.Count; index++)
        {
            persistenceManager.AppendEntry(SessionMessageEntry.FromChatMessage(messages[index]));
        }

        persistedMessageCount = messages.Count;
    }

    private async Task ListModelsAsync(string? filter, CancellationToken cancellationToken)
    {
        var providerResults = new List<ProviderModelDiscoveryService.ProviderModelListingResult>();

        foreach (var provider in _providerCatalog.GetAll())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var apiKey = string.IsNullOrWhiteSpace(provider.Configuration.ApiKeyEnvironmentVariable)
                ? null
                : _environment.GetEnvironmentVariable(provider.Configuration.ApiKeyEnvironmentVariable);
            providerResults.Add(
                await _modelDiscoveryService.ListProviderModelsAsync(provider, apiKey, cancellationToken).ConfigureAwait(false));
        }

        foreach (var warning in providerResults
                     .Select(static result => result.Warning)
                     .Where(static warning => !string.IsNullOrWhiteSpace(warning)))
        {
            await _environment.Error.WriteLineAsync(warning!).ConfigureAwait(false);
        }

        var models = providerResults
            .SelectMany(static result => result.Models)
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
                        ? "No available models were discovered."
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

    private async Task<ChatMessage?> BuildInitialPromptAsync(CliArguments parsed, string workingDirectory)
    {
        var contents = new List<AIContent>();

        if (_environment.IsInputRedirected)
        {
            var stdin = await _environment.Input.ReadToEndAsync().ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(stdin))
            {
                AppendTextContent(contents, stdin.Trim());
            }
        }

        if (parsed.FileArguments.Count > 0)
        {
            AppendContents(
                contents,
                await CodingAgentContextLoader
                    .LoadFileArgumentContentsAsync(parsed.FileArguments, workingDirectory)
                    .ConfigureAwait(false));
        }

        if (parsed.Messages.Count > 0)
        {
            AppendTextContent(contents, string.Join(' ', parsed.Messages));
        }

        return contents.Count == 0
            ? null
            : new ChatMessage(ChatRole.User, contents);
    }

    private string? BuildAppendSystemPrompt(IEnumerable<string> promptInputs, string workingDirectory)
    {
        var resolvedInputs = promptInputs
            .Select(promptInput => CodingAgentContextLoader.ResolvePromptInput(promptInput, workingDirectory))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        return resolvedInputs.Length == 0
            ? null
            : string.Join("\n\n", resolvedInputs);
    }

    private bool ShouldRunInteractive(CliArguments parsed, ChatMessage? initialPrompt) =>
        _environment.IsInteractiveTerminal &&
        !parsed.Print &&
        initialPrompt is null;

    private static IReadOnlyList<string> ResolveActiveToolNames(CliArguments parsed, IReadOnlyList<string>? sessionToolNames)
    {
        if (parsed.NoTools)
        {
            return Array.Empty<string>();
        }

        if (parsed.Tools is { Count: > 0 })
        {
            return parsed.Tools;
        }

        if (sessionToolNames is { Count: > 0 })
        {
            return sessionToolNames;
        }

        return BuiltInToolNames.Default;
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

    private static SessionEntry CloneEntry(SessionEntry entry) =>
        entry switch
        {
            SessionMessageEntry message => message with { },
            ThinkingLevelChangeEntry thinkingLevel => thinkingLevel with { },
            ModelChangeEntry modelChange => modelChange with { },
            CompactionEntry compaction => compaction with { },
            LabelEntry label => label with { },
            _ => throw new InvalidOperationException($"Unsupported session entry type '{entry.GetType().Name}'."),
        };

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

    private static IChatClient WrapWithMiddleware(IChatClient inner, bool verbose, Uri? otelEndpoint)
    {
        var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(verbose ? LogLevel.Debug : LogLevel.Warning);
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss ";
            });
        });

        var telemetryResources = new List<IDisposable>
        {
            loggerFactory,
        };

        if (otelEndpoint is not null)
        {
            telemetryResources.Add(
                Sdk.CreateTracerProviderBuilder()
                    .AddSource(OpenTelemetryChatSourceName)
                    .AddOtlpExporter(options =>
                    {
                        options.Endpoint = BuildOtlpTraceEndpoint(otelEndpoint);
                        options.Protocol = OtlpExportProtocol.HttpProtobuf;
                    })
                    .Build());
        }

        var chatClient = new ChatClientBuilder(inner)
            .UseLogging(loggerFactory)
            .UseOpenTelemetry(loggerFactory, OpenTelemetryChatSourceName)
            .Build();

        return new DisposableChatClient(chatClient, telemetryResources);
    }

    private static Uri BuildOtlpTraceEndpoint(Uri endpoint)
    {
        if (!string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return endpoint;
        }

        if (endpoint.AbsolutePath.EndsWith("/v1/traces", StringComparison.OrdinalIgnoreCase))
        {
            return endpoint;
        }

        var builder = new UriBuilder(endpoint);
        builder.Path = string.IsNullOrWhiteSpace(builder.Path) || builder.Path == "/"
            ? "/v1/traces"
            : $"{builder.Path.TrimEnd('/')}/v1/traces";

        return builder.Uri;
    }

    private static void AppendContents(IList<AIContent> target, IEnumerable<AIContent> source)
    {
        foreach (var content in source)
        {
            switch (content)
            {
                case TextContent text:
                    AppendTextContent(target, text.Text);
                    break;
                default:
                    target.Add(content);
                    break;
            }
        }
    }

    private static void AppendTextContent(IList<AIContent> contents, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (contents.Count > 0 && contents[^1] is TextContent existing)
        {
            contents[^1] = new TextContent($"{existing.Text}\n\n{text}");
            return;
        }

        contents.Add(new TextContent(text));
    }

    private static readonly JsonSerializerOptions JsonOutputOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private sealed class JsonOutputResponse
    {
        public string Role { get; init; } = "assistant";
        public string Content { get; init; } = string.Empty;
        public string? Model { get; init; }
        public JsonToolCall[] ToolCalls { get; init; } = [];
        public int MessageCount { get; init; }
    }

    private sealed class JsonToolCall
    {
        public string? Id { get; init; }
        public string? Name { get; init; }
        public IDictionary<string, object?>? Arguments { get; init; }
    }

    private sealed record PersistedSessionSource(SessionManager Manager, SessionContext Context);

    private sealed class DisposableChatClient(
        IChatClient inner,
        IReadOnlyList<IDisposable> resources) : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.GetResponseAsync(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            inner.GetStreamingResponseAsync(messages, options, cancellationToken);

        public object? GetService(Type serviceType, object? serviceKey = null) =>
            inner.GetService(serviceType, serviceKey);

        public void Dispose()
        {
            Exception? disposeException = null;

            try
            {
                inner.Dispose();
            }
            catch (Exception exception)
            {
                disposeException = exception;
            }

            foreach (var resource in resources.Reverse())
            {
                try
                {
                    resource.Dispose();
                }
                catch (Exception exception) when (disposeException is null)
                {
                    disposeException = exception;
                }
            }

            if (disposeException is not null)
            {
                throw disposeException;
            }
        }
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
