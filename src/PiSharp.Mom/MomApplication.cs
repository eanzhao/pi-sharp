using System.Reflection;
using System.Globalization;
using System.Text.Json;
using PiSharp.CodingAgent;

namespace PiSharp.Mom;

public enum MomCommandKind
{
    RunBot,
    ShowStats,
}

public sealed record MomCommandLineOptions
{
    public MomCommandKind Command { get; init; } = MomCommandKind.RunBot;

    public bool JsonOutput { get; init; }

    public string? WorkspaceDirectory { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }

    public string? ApiKey { get; init; }

    public string? SlackAppToken { get; init; }

    public string? SlackBotToken { get; init; }
}

public sealed class MomApplication
{
    private static readonly JsonSerializerOptions StatsJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly MomConsoleEnvironment _environment;
    private readonly string _appName;
    private readonly bool _namespaced;
    private readonly CodingAgentProviderCatalog _providerCatalog;
    private readonly Func<string, string, SettingsManager> _createSettingsManager;
    private readonly Func<MomCommandLineOptions, CancellationToken, Task<int>> _runBotAsync;
    private readonly Func<MomCommandLineOptions, CancellationToken, Task<int>> _runStatsAsync;

    public MomApplication(
        MomConsoleEnvironment? environment = null,
        string appName = "pisharp-mom",
        bool namespaced = false,
        CodingAgentProviderCatalog? providerCatalog = null,
        Func<string, string, SettingsManager>? createSettingsManager = null,
        Func<MomCommandLineOptions, CancellationToken, Task<int>>? runBotAsync = null,
        Func<MomCommandLineOptions, CancellationToken, Task<int>>? runStatsAsync = null)
    {
        _environment = environment ?? MomConsoleEnvironment.CreateProcessEnvironment();
        _appName = string.IsNullOrWhiteSpace(appName) ? "pisharp-mom" : appName.Trim();
        _namespaced = namespaced;
        _providerCatalog = providerCatalog ?? CodingAgentProviderCatalog.CreateDefault();
        _createSettingsManager = createSettingsManager ?? SettingsManager.Create;
        _runBotAsync = runBotAsync ?? RunBotInternalAsync;
        _runStatsAsync = runStatsAsync ?? RunStatsInternalAsync;
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

        if (string.Equals(normalizedArgs[0], "stats", StringComparison.Ordinal))
        {
            if (normalizedArgs.Length == 1 || IsHelp(normalizedArgs[1]))
            {
                await _environment.Output.WriteLineAsync(GetStatsHelpText(_appName, _namespaced)).ConfigureAwait(false);
                return 0;
            }

            if (IsVersion(normalizedArgs[1]))
            {
                await _environment.Output.WriteLineAsync(GetVersionText()).ConfigureAwait(false);
                return 0;
            }
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
            await _environment.Error.WriteLineAsync(
                    "Usage: " + GetUsage(_appName, _namespaced, options.Command))
                .ConfigureAwait(false);
            return 1;
        }

        try
        {
            var runner = options.Command switch
            {
                MomCommandKind.ShowStats => _runStatsAsync,
                _ => _runBotAsync,
            };
            return await runner(options, cancellationToken).ConfigureAwait(false);
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
  {GetUsage(appName, namespaced, MomCommandKind.RunBot)}
  {GetUsage(appName, namespaced, MomCommandKind.ShowStats)}

Commands:
  stats [--json] <workspace-directory>
                                Print persisted runtime stats for a mom workspace

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
  {rootCommand} stats ./mom-data
  {rootCommand} stats --json ./mom-data
  {rootCommand} --provider anthropic --model claude-3-7-sonnet-latest ./mom-data
""";
    }

    public static string GetStatsHelpText(string appName = "pisharp-mom", bool namespaced = false)
    {
        var rootCommand = namespaced ? $"{appName} mom" : appName;

        return
        $"""
{rootCommand} stats - Print persisted mom runtime stats

Usage:
  {GetUsage(appName, namespaced, MomCommandKind.ShowStats)}

Options:
  --json                    Output runtime stats as JSON
  -h, --help                Show help
  --version                 Show version

Examples:
  {rootCommand} stats ./mom-data
  {rootCommand} stats --json ./mom-data
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
        var runtimeStats = new MomRuntimeStats(Path.Combine(workspaceDirectory, MomDefaults.RuntimeStatsFileName));

        async Task ReportNoticeAsync(string message, CancellationToken _)
        {
            await _environment.Output.WriteLineAsync(message).ConfigureAwait(false);
            await _environment.Output.WriteLineAsync(runtimeStats.FormatSummary()).ConfigureAwait(false);
        }

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
            auth.UserId,
            ReportNoticeAsync,
            runtimeStats);
        var backfillResult = await backfiller.BackfillAllAsync(auth.UserId, cancellationToken).ConfigureAwait(false);
        runtimeStats.RecordStartupBackfill(backfillResult, DateTimeOffset.UtcNow);
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
        await _environment.Output.WriteLineAsync(runtimeStats.FormatSummary()).ConfigureAwait(false);

        var responseCutoffTimestamp = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d)
            .ToString("F6", CultureInfo.InvariantCulture);
        await _environment.Output.WriteLineAsync($"Listening for Slack events in {workspaceDirectory}").ConfigureAwait(false);
        await socketModeClient.RunAsync(
                auth.UserId,
                runtime.DispatchAsync,
                responseCutoffTimestamp,
                runtimeStats,
                ReportNoticeAsync,
                cancellationToken)
            .ConfigureAwait(false);
        return 0;
    }

    private async Task<int> RunStatsInternalAsync(MomCommandLineOptions options, CancellationToken cancellationToken)
    {
        var workspaceDirectory = Path.GetFullPath(options.WorkspaceDirectory!);
        if (!Directory.Exists(workspaceDirectory))
        {
            throw new InvalidOperationException($"Workspace directory not found: {workspaceDirectory}");
        }

        var statsPath = Path.Combine(workspaceDirectory, MomDefaults.RuntimeStatsFileName);
        if (!File.Exists(statsPath))
        {
            if (options.JsonOutput)
            {
                await _environment.Output.WriteLineAsync(JsonSerializer.Serialize(
                        new
                        {
                            workspaceDirectory,
                            runtimeStatsFound = false,
                        },
                        StatsJsonOptions))
                    .ConfigureAwait(false);
                return 0;
            }

            await _environment.Output.WriteLineAsync($"No runtime stats found in {workspaceDirectory}").ConfigureAwait(false);
            return 0;
        }

        var runtimeStats = new MomRuntimeStats(statsPath);
        var snapshot = runtimeStats.Snapshot();
        if (options.JsonOutput)
        {
            await _environment.Output.WriteLineAsync(JsonSerializer.Serialize(
                    new
                    {
                        workspaceDirectory,
                        runtimeStatsFound = true,
                        summary = runtimeStats.FormatSummary(),
                        snapshot,
                    },
                    StatsJsonOptions))
                .ConfigureAwait(false);
            return 0;
        }

        foreach (var line in BuildStatsReport(workspaceDirectory, runtimeStats, snapshot))
        {
            cancellationToken.ThrowIfCancellationRequested();
            await _environment.Output.WriteLineAsync(line).ConfigureAwait(false);
        }

        return 0;
    }

    private static MomCommandLineOptions Parse(IReadOnlyList<string> args)
    {
        if (args.Count > 0 && string.Equals(args[0], "stats", StringComparison.Ordinal))
        {
            return ParseStatsArguments(args.Skip(1).ToArray());
        }

        return ParseRunArguments(args);
    }

    private static MomCommandLineOptions ParseRunArguments(IReadOnlyList<string> args)
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
            Command = MomCommandKind.RunBot,
            WorkspaceDirectory = workspaceDirectory,
            Provider = provider,
            Model = model,
            ApiKey = apiKey,
            SlackAppToken = slackAppToken,
            SlackBotToken = slackBotToken,
        };
    }

    private static MomCommandLineOptions ParseStatsArguments(IReadOnlyList<string> args)
    {
        string? workspaceDirectory = null;
        var jsonOutput = false;

        for (var index = 0; index < args.Count; index++)
        {
            if (string.Equals(args[index], "--json", StringComparison.Ordinal))
            {
                jsonOutput = true;
                continue;
            }

            if (args[index].StartsWith("--", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Unknown option '{args[index]}'.");
            }

            workspaceDirectory ??= args[index];
        }

        return new MomCommandLineOptions
        {
            Command = MomCommandKind.ShowStats,
            JsonOutput = jsonOutput,
            WorkspaceDirectory = workspaceDirectory,
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

    private static string GetUsage(string appName, bool namespaced, MomCommandKind command)
    {
        var rootCommand = namespaced ? $"{appName} mom" : appName;
        return command switch
        {
            MomCommandKind.ShowStats => $"{rootCommand} stats [--json] <workspace-directory>",
            _ => $"{rootCommand} [--provider <name>] [--model <id>] [--api-key <key>] [--slack-app-token <xapp>] [--slack-bot-token <xoxb>] <workspace-directory>",
        };
    }

    private static bool IsHelp(string value) =>
        string.Equals(value, "-h", StringComparison.Ordinal) ||
        string.Equals(value, "--help", StringComparison.Ordinal);

    private static bool IsVersion(string value) =>
        string.Equals(value, "--version", StringComparison.Ordinal);

    private static string GetVersionText() =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";

    private static IReadOnlyList<string> BuildStatsReport(
        string workspaceDirectory,
        MomRuntimeStats runtimeStats,
        MomRuntimeStatsSnapshot snapshot)
    {
        return
        [
            $"Workspace: {workspaceDirectory}",
            runtimeStats.FormatSummary(),
            $"Startup backfill: channels={snapshot.StartupBackfillChannels} messages={snapshot.StartupBackfillMessages} last={FormatTimestamp(snapshot.LastStartupBackfillAt)}",
            $"Reconnects: count={snapshot.ReconnectCount} last={FormatTimestamp(snapshot.LastReconnectAt)} generation={snapshot.LastReconnectGeneration?.ToString() ?? "none"}",
            $"Bootstrap backfill totals: count={snapshot.BootstrapBackfillCount} messages={snapshot.BootstrapBackfillMessages} failures={snapshot.BootstrapBackfillFailures}",
            $"Last bootstrap success: {FormatSuccess(snapshot.LastBootstrapBackfillAt, snapshot.LastBootstrapBackfillChannel)}",
            $"Last bootstrap failure: {FormatFailure(snapshot.LastBootstrapBackfillFailureAt, snapshot.LastBootstrapBackfillFailureChannel, snapshot.LastBootstrapBackfillFailureKind, snapshot.LastBootstrapBackfillFailureReason)}",
            $"Reconnect-gap backfill totals: count={snapshot.ReconnectGapBackfillCount} messages={snapshot.ReconnectGapBackfillMessages} failures={snapshot.ReconnectGapBackfillFailures}",
            $"Last reconnect-gap success: {FormatSuccess(snapshot.LastReconnectGapBackfillAt, snapshot.LastReconnectGapBackfillChannel)}",
            $"Last reconnect-gap failure: {FormatFailure(snapshot.LastReconnectGapBackfillFailureAt, snapshot.LastReconnectGapBackfillFailureChannel, snapshot.LastReconnectGapBackfillFailureKind, snapshot.LastReconnectGapBackfillFailureReason)}",
        ];
    }

    private static string FormatSuccess(DateTimeOffset? timestamp, string? channel) =>
        timestamp is null
            ? "none"
            : $"at={FormatTimestamp(timestamp)} channel={channel ?? "none"}";

    private static string FormatFailure(
        DateTimeOffset? timestamp,
        string? channel,
        string? kind,
        string? reason) =>
        timestamp is null
            ? "none"
            : $"at={FormatTimestamp(timestamp)} channel={channel ?? "none"} kind={kind ?? "unknown"} reason={reason ?? "unknown"}";

    private static string FormatTimestamp(DateTimeOffset? timestamp) =>
        timestamp?.ToString("O", CultureInfo.InvariantCulture) ?? "none";
}
