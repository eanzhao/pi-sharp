using PiSharp.Agent;
using PiSharp.CodingAgent;

namespace PiSharp.Cli;

public enum CliDiagnosticSeverity
{
    Warning,
    Error,
}

public sealed record CliDiagnostic(CliDiagnosticSeverity Severity, string Message);

public sealed class CliArguments
{
    public bool Help { get; init; }

    public bool Version { get; init; }

    public bool Print { get; init; }

    public bool Verbose { get; init; }

    public bool ListModels { get; init; }

    public string? ListModelsFilter { get; init; }

    public string? Provider { get; init; }

    public string? Model { get; init; }

    public string? ApiKey { get; init; }

    public string? WorkingDirectory { get; init; }

    public string? SystemPrompt { get; init; }

    public IReadOnlyList<string> AppendSystemPromptInputs { get; init; } = Array.Empty<string>();

    public ThinkingLevel? ThinkingLevel { get; init; }

    public bool NoTools { get; init; }

    public IReadOnlyList<string>? Tools { get; init; }

    public bool NoContextFiles { get; init; }

    public string? SessionDirectory { get; init; }

    public string? ResumeSession { get; init; }

    public string? ForkSession { get; init; }

    public bool NoSession { get; init; }

    public IReadOnlyList<string> Messages { get; init; } = Array.Empty<string>();

    public IReadOnlyList<string> FileArguments { get; init; } = Array.Empty<string>();

    public IReadOnlyList<CliDiagnostic> Diagnostics { get; init; } = Array.Empty<CliDiagnostic>();
}

public static class CliArgumentsParser
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

    public static CliArguments Parse(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var appendSystemPromptInputs = new List<string>();
        var messages = new List<string>();
        var fileArguments = new List<string>();
        var diagnostics = new List<CliDiagnostic>();
        List<string>? tools = null;

        var help = false;
        var version = false;
        var print = false;
        var verbose = false;
        var listModels = false;
        string? listModelsFilter = null;
        string? provider = null;
        string? model = null;
        string? apiKey = null;
        string? workingDirectory = null;
        string? systemPrompt = null;
        ThinkingLevel? thinkingLevel = null;
        var noTools = false;
        var noContextFiles = false;
        var sessionDirectory = (string?)null;
        var resumeSession = (string?)null;
        var forkSession = (string?)null;
        var noSession = false;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];

            if (argument is "--help" or "-h")
            {
                help = true;
                continue;
            }

            if (argument is "--version" or "-v")
            {
                version = true;
                continue;
            }

            if (argument is "--print" or "-p")
            {
                print = true;
                continue;
            }

            if (argument == "--verbose")
            {
                verbose = true;
                continue;
            }

            if (argument == "--list-models")
            {
                listModels = true;
                if (index + 1 < args.Count &&
                    !args[index + 1].StartsWith("-", StringComparison.Ordinal) &&
                    !args[index + 1].StartsWith("@", StringComparison.Ordinal))
                {
                    listModelsFilter = args[++index];
                }

                continue;
            }

            if (argument == "--provider")
            {
                provider = ReadRequiredValue(args, ref index, argument, diagnostics);
                continue;
            }

            if (argument == "--model")
            {
                model = ReadRequiredValue(args, ref index, argument, diagnostics);
                continue;
            }

            if (argument == "--api-key")
            {
                apiKey = ReadRequiredValue(args, ref index, argument, diagnostics);
                continue;
            }

            if (argument is "--cwd" or "--working-directory")
            {
                workingDirectory = ReadRequiredValue(args, ref index, argument, diagnostics);
                continue;
            }

            if (argument == "--system-prompt")
            {
                systemPrompt = ReadRequiredValue(args, ref index, argument, diagnostics);
                continue;
            }

            if (argument == "--append-system-prompt")
            {
                var value = ReadRequiredValue(args, ref index, argument, diagnostics);
                if (value is not null)
                {
                    appendSystemPromptInputs.Add(value);
                }

                continue;
            }

            if (argument == "--thinking")
            {
                var value = ReadRequiredValue(args, ref index, argument, diagnostics);
                if (value is not null)
                {
                    if (!ThinkingLevels.TryGetValue(value, out ThinkingLevel parsedThinkingLevel))
                    {
                        diagnostics.Add(
                            new CliDiagnostic(
                                CliDiagnosticSeverity.Warning,
                                $"Invalid thinking level '{value}'. Valid values: {string.Join(", ", ThinkingLevels.Keys)}."));
                        thinkingLevel = null;
                    }
                    else
                    {
                        thinkingLevel = parsedThinkingLevel;
                    }
                }

                continue;
            }

            if (argument == "--no-tools")
            {
                noTools = true;
                continue;
            }

            if (argument == "--tools")
            {
                var rawTools = ReadRequiredValue(args, ref index, argument, diagnostics);
                if (rawTools is not null)
                {
                    tools ??= new List<string>();
                    foreach (var toolName in rawTools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (BuiltInToolNames.All.Contains(toolName, StringComparer.Ordinal))
                        {
                            tools.Add(toolName);
                        }
                        else
                        {
                            diagnostics.Add(
                                new CliDiagnostic(
                                    CliDiagnosticSeverity.Warning,
                                    $"Unknown tool '{toolName}'. Valid tools: {string.Join(", ", BuiltInToolNames.All)}."));
                        }
                    }
                }

                continue;
            }

            if (argument == "--no-context-files")
            {
                noContextFiles = true;
                continue;
            }

            if (argument == "--session-dir")
            {
                sessionDirectory = ReadRequiredValue(args, ref index, argument, diagnostics);
                continue;
            }

            if (argument == "--resume")
            {
                resumeSession = ReadRequiredValue(args, ref index, argument, diagnostics);
                continue;
            }

            if (argument == "--fork")
            {
                forkSession = ReadRequiredValue(args, ref index, argument, diagnostics);
                continue;
            }

            if (argument == "--no-session")
            {
                noSession = true;
                continue;
            }

            if (argument.StartsWith("@", StringComparison.Ordinal))
            {
                fileArguments.Add(argument[1..]);
                continue;
            }

            if (argument.StartsWith("-", StringComparison.Ordinal))
            {
                diagnostics.Add(new CliDiagnostic(CliDiagnosticSeverity.Error, $"Unknown option '{argument}'."));
                continue;
            }

            messages.Add(argument);
        }

        if (noTools && tools is { Count: > 0 })
        {
            diagnostics.Add(new CliDiagnostic(CliDiagnosticSeverity.Error, "--tools cannot be combined with --no-tools."));
        }

        if (!string.IsNullOrWhiteSpace(resumeSession) && !string.IsNullOrWhiteSpace(forkSession))
        {
            diagnostics.Add(new CliDiagnostic(CliDiagnosticSeverity.Error, "--resume cannot be combined with --fork."));
        }

        if (noSession && (!string.IsNullOrWhiteSpace(resumeSession) || !string.IsNullOrWhiteSpace(forkSession)))
        {
            diagnostics.Add(new CliDiagnostic(CliDiagnosticSeverity.Error, "--no-session cannot be combined with --resume or --fork."));
        }

        return new CliArguments
        {
            Help = help,
            Version = version,
            Print = print,
            Verbose = verbose,
            ListModels = listModels,
            ListModelsFilter = listModelsFilter,
            Provider = provider,
            Model = model,
            ApiKey = apiKey,
            WorkingDirectory = workingDirectory,
            SystemPrompt = systemPrompt,
            AppendSystemPromptInputs = appendSystemPromptInputs,
            ThinkingLevel = thinkingLevel,
            NoTools = noTools,
            Tools = tools,
            NoContextFiles = noContextFiles,
            SessionDirectory = sessionDirectory,
            ResumeSession = resumeSession,
            ForkSession = forkSession,
            NoSession = noSession,
            Messages = messages,
            FileArguments = fileArguments,
            Diagnostics = diagnostics,
        };
    }

    public static string GetHelpText(string appName = "pisharp") =>
        $"""
{appName} - PiSharp coding agent CLI

Usage:
  {appName} pods [setup|active|remove|start|stop|list|logs|agent] ...
  {appName} [options] [@files...] [message...]

Options:
  --provider <name>              Provider name (default: openai)
  --model <id>                   Model id (default: provider default model)
  --api-key <key>                API key (defaults to provider env var)
  --cwd <dir>                    Working directory (default: current directory)
  --system-prompt <text>         Replace the default system prompt
  --append-system-prompt <text>  Append text or file contents to the system prompt
  --tools <tools>                Comma-separated built-in tools to enable
  --no-tools                     Disable all built-in tools
  --no-context-files             Disable AGENTS.md / CLAUDE.md loading
  --thinking <level>             off, minimal, low, medium, high, xhigh
  --session-dir <dir>            Override the persisted session directory
  --resume <id-or-path>          Resume a persisted session
  --fork <id-or-path>            Fork a persisted session into a new session
  --no-session                   Disable session persistence for this run
  --list-models [pattern]        List known models, optionally filtered
  --print, -p                    Print mode (current default behavior)
  --verbose                      Print tool execution diagnostics to stderr
  --help, -h                     Show this help text
  --version, -v                  Show version

Examples:
  {appName} pods setup dc1 "ssh root@1.2.3.4" --models-path /workspace
  {appName} pods start Qwen/Qwen2.5-Coder-32B-Instruct --name qwen
  {appName} "Summarize the repository"
  {appName}                      Start interactive mode when stdin is a TTY
  {appName} --model gpt-4.1-mini --tools read,grep,find "Find failing tests"
  {appName} --resume latest --print "Continue the last session"
  cat error.log | {appName} --append-system-prompt AGENTS.md "Debug this"
""";

    private static string? ReadRequiredValue(
        IReadOnlyList<string> args,
        ref int index,
        string optionName,
        ICollection<CliDiagnostic> diagnostics)
    {
        if (index + 1 >= args.Count)
        {
            diagnostics.Add(new CliDiagnostic(CliDiagnosticSeverity.Error, $"Missing value for '{optionName}'."));
            return null;
        }

        return args[++index];
    }
}
