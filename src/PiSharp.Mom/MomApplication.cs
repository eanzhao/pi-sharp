using System.Reflection;
using System.Globalization;
using PiSharp.CodingAgent;

namespace PiSharp.Mom;

public sealed record MomCommandLineOptions
{
    public string? WorkspaceDirectory { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }

    public string? ApiKey { get; init; }

    public string? SlackAppToken { get; init; }

    public string? SlackBotToken { get; init; }
}

public sealed class MomApplication
{
    private readonly MomConsoleEnvironment _environment;
    private readonly string _appName;
    private readonly bool _namespaced;
    private readonly CodingAgentProviderCatalog _providerCatalog;
    private readonly Func<string, string, SettingsManager> _createSettingsManager;
    private readonly Func<MomCommandLineOptions, CancellationToken, Task<int>> _runBotAsync;

    public MomApplication(
        MomConsoleEnvironment? environment = null,
        string appName = "pisharp-mom",
        bool namespaced = false,
        CodingAgentProviderCatalog? providerCatalog = null,
        Func<string, string, SettingsManager>? createSettingsManager = null,
        Func<MomCommandLineOptions, CancellationToken, Task<int>>? runBotAsync = null)
    {
        _environment = environment ?? MomConsoleEnvironment.CreateProcessEnvironment();
        _appName = string.IsNullOrWhiteSpace(appName) ? "pisharp-mom" : appName.Trim();
        _namespaced = namespaced;
        _providerCatalog = providerCatalog ?? CodingAgentProviderCatalog.CreateDefault();
        _createSettingsManager = createSettingsManager ?? SettingsManager.Create;
        _runBotAsync = runBotAsync ?? RunBotInternalAsync;
    }

    public async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(args);

        var normalizedArgs = args.Count > 0 && string.Equals(args[0], "mom", StringComparison.Ordinal)
            ? args.Skip(1).ToArray()
            : args.ToArray();

        if (normalizedArgs.Length == 0 || IsHelp(normalizedArgs[0]))
        {
            await _environment.Output.WriteLineAsync(GetHelpText(_appName, _namespaced)).ConfigureAwait(false);
            return 0;
        }

        if (IsVersion(normalizedArgs[0]))
        {
            await _environment.Output.WriteLineAsync(GetVersionText()).ConfigureAwait(false);
            return 0;
        }

        MomCommandLineOptions options;
        try
        {
            options = Parse(normalizedArgs);
        }
        catch (Exception exception)
        {
            await _environment.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }

        if (string.IsNullOrWhiteSpace(options.WorkspaceDirectory))
        {
            await _environment.Error.WriteLineAsync("Usage: " + GetUsage(_appName, _namespaced)).ConfigureAwait(false);
            return 1;
        }

        try
        {
            return await _runBotAsync(options, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return 0;
        }
        catch (Exception exception)
        {
            await _environment.Error.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 1;
        }
    }

    public static string GetHelpText(string appName = "pisharp-mom", bool namespaced = false)
    {
        var rootCommand = namespaced ? $"{appName} mom" : appName;

        return
        $"""
{rootCommand} - PiSharp Slack bot runtime

Usage:
  {GetUsage(appName, namespaced)}

Options:
  --provider <name>          Override provider from settings/environment
  --model <id>               Override model id
  --api-key <key>            Override provider API key
  --slack-app-token <xapp>   Override {MomDefaults.SlackAppTokenEnvironmentVariable}
  --slack-bot-token <xoxb>   Override {MomDefaults.SlackBotTokenEnvironmentVariable}
  -h, --help                 Show help
  --version                  Show version

Examples:
  {rootCommand} ./mom-data
  {rootCommand} --provider anthropic --model claude-3-7-sonnet-latest ./mom-data
""";
    }

    private async Task<int> RunBotInternalAsync(MomCommandLineOptions options, CancellationToken cancellationToken)
    {
        var workspaceDirectory = Path.GetFullPath(options.WorkspaceDirectory!);
        Directory.CreateDirectory(workspaceDirectory);

        var slackAppToken = options.SlackAppToken ?? _environment.GetEnvironmentVariable(MomDefaults.SlackAppTokenEnvironmentVariable);
        var slackBotToken = options.SlackBotToken ?? _environment.GetEnvironmentVariable(MomDefaults.SlackBotTokenEnvironmentVariable);

        if (string.IsNullOrWhiteSpace(slackAppToken))
        {
            throw new InvalidOperationException($"Missing Slack app token. Set {MomDefaults.SlackAppTokenEnvironmentVariable} or pass --slack-app-token.");
        }

        if (string.IsNullOrWhiteSpace(slackBotToken))
        {
            throw new InvalidOperationException($"Missing Slack bot token. Set {MomDefaults.SlackBotTokenEnvironmentVariable} or pass --slack-bot-token.");
        }

        using var slackClient = new SlackWebApiClient(slackBotToken);
        var auth = await slackClient.AuthenticateAsync(cancellationToken).ConfigureAwait(false);

        var runtimeOptions = new MomRuntimeOptions
        {
            WorkspaceDirectory = workspaceDirectory,
            Provider = options.Provider,
            Model = options.Model,
            ApiKey = options.ApiKey,
        };
        var workspaceIndex = new MomSlackWorkspaceIndex();
        using var metadataService = new MomSlackMetadataService(slackClient, workspaceIndex);
        await metadataService.RefreshAsync(cancellationToken).ConfigureAwait(false);
        using var store = new MomChannelStore(workspaceDirectory, slackBotToken, workspaceIndex: workspaceIndex);

        var turnProcessor = new MomTurnProcessor(
            _environment,
            runtimeOptions,
            _providerCatalog,
            _createSettingsManager,
            slackClient,
            store);

        var backfiller = new MomLogBackfiller(slackClient, store);
        var runtime = new MomWorkspaceRuntime(
            turnProcessor,
            slackClient,
            store,
            metadataService,
            backfiller,
            auth.UserId);
        var backfillResult = await backfiller.BackfillAllAsync(auth.UserId, cancellationToken).ConfigureAwait(false);
        var socketModeClient = new SlackSocketModeClient(slackClient, slackAppToken);
        using var eventsWatcher = new MomEventsWatcher(workspaceDirectory, runtime.DispatchAsync);
        eventsWatcher.Start(cancellationToken);

        if (backfillResult.ChannelsScanned > 0)
        {
            await _environment.Output.WriteLineAsync(
                    $"Backfilled {backfillResult.MessagesLogged} messages across {backfillResult.ChannelsScanned} channels")
                .ConfigureAwait(false);
        }

        await _environment.Output.WriteLineAsync(
                $"Loaded {workspaceIndex.Users.Count} users and {workspaceIndex.Channels.Count} channels")
            .ConfigureAwait(false);

        var responseCutoffTimestamp = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d)
            .ToString("F6", CultureInfo.InvariantCulture);
        await _environment.Output.WriteLineAsync($"Listening for Slack events in {workspaceDirectory}").ConfigureAwait(false);
        await socketModeClient.RunAsync(auth.UserId, runtime.DispatchAsync, responseCutoffTimestamp, cancellationToken).ConfigureAwait(false);
        return 0;
    }

    private static MomCommandLineOptions Parse(IReadOnlyList<string> args)
    {
        string? workspaceDirectory = null;
        string? provider = null;
        string? model = null;
        string? apiKey = null;
        string? slackAppToken = null;
        string? slackBotToken = null;

        for (var index = 0; index < args.Count; index++)
        {
            switch (args[index])
            {
                case "--provider":
                    provider = ReadRequiredValue(args, ref index, "--provider");
                    break;
                case "--model":
                    model = ReadRequiredValue(args, ref index, "--model");
                    break;
                case "--api-key":
                    apiKey = ReadRequiredValue(args, ref index, "--api-key");
                    break;
                case "--slack-app-token":
                    slackAppToken = ReadRequiredValue(args, ref index, "--slack-app-token");
                    break;
                case "--slack-bot-token":
                    slackBotToken = ReadRequiredValue(args, ref index, "--slack-bot-token");
                    break;
                default:
                    if (args[index].StartsWith("--", StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException($"Unknown option '{args[index]}'.");
                    }

                    workspaceDirectory ??= args[index];
                    break;
            }
        }

        return new MomCommandLineOptions
        {
            WorkspaceDirectory = workspaceDirectory,
            Provider = provider,
            Model = model,
            ApiKey = apiKey,
            SlackAppToken = slackAppToken,
            SlackBotToken = slackBotToken,
        };
    }

    private static string ReadRequiredValue(IReadOnlyList<string> args, ref int index, string optionName)
    {
        if (index + 1 >= args.Count || args[index + 1].StartsWith("-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Option '{optionName}' requires a value.");
        }

        index++;
        return args[index];
    }

    private static string GetUsage(string appName, bool namespaced)
    {
        var rootCommand = namespaced ? $"{appName} mom" : appName;
        return $"{rootCommand} [--provider <name>] [--model <id>] [--api-key <key>] [--slack-app-token <xapp>] [--slack-bot-token <xoxb>] <workspace-directory>";
    }

    private static bool IsHelp(string value) =>
        string.Equals(value, "-h", StringComparison.Ordinal) ||
        string.Equals(value, "--help", StringComparison.Ordinal);

    private static bool IsVersion(string value) =>
        string.Equals(value, "--version", StringComparison.Ordinal);

    private static string GetVersionText() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
}
