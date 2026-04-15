using System.Text;

namespace PiSharp.CodingAgent;

public sealed class BuildSystemPromptOptions
{
    public string? CustomPrompt { get; init; }

    public IReadOnlyList<string>? SelectedTools { get; init; }

    public IReadOnlyDictionary<string, string>? ToolSnippets { get; init; }

    public IReadOnlyList<string>? PromptGuidelines { get; init; }

    public string? AppendSystemPrompt { get; init; }

    public string? WorkingDirectory { get; init; }

    public IReadOnlyList<CodingAgentContextFile>? ContextFiles { get; init; }

    public DateTimeOffset? CurrentTime { get; init; }
}

public static class CodingAgentSystemPrompt
{
    public static string Build(BuildSystemPromptOptions? options = null)
    {
        options ??= new BuildSystemPromptOptions();

        var selectedTools = options.SelectedTools ?? BuiltInToolNames.Default;
        var toolSnippets = options.ToolSnippets ?? CodingAgentTools.PromptSnippets;
        var contextFiles = options.ContextFiles ?? Array.Empty<CodingAgentContextFile>();
        var workingDirectory = Path.GetFullPath(options.WorkingDirectory ?? Directory.GetCurrentDirectory());
        var currentDate = (options.CurrentTime ?? DateTimeOffset.UtcNow).ToString("yyyy-MM-dd");

        var appendSection = string.IsNullOrWhiteSpace(options.AppendSystemPrompt)
            ? null
            : options.AppendSystemPrompt.Trim();

        if (!string.IsNullOrWhiteSpace(options.CustomPrompt))
        {
            var customBuilder = new StringBuilder(options.CustomPrompt.Trim());

            if (appendSection is not null)
            {
                customBuilder.AppendLine();
                customBuilder.AppendLine();
                customBuilder.Append(appendSection);
            }

            AppendContextFiles(customBuilder, contextFiles);
            customBuilder.AppendLine();
            customBuilder.Append($"Current date: {currentDate}");
            customBuilder.AppendLine();
            customBuilder.Append($"Current working directory: {NormalizeSlashes(workingDirectory)}");
            return customBuilder.ToString();
        }

        var visibleTools = selectedTools
            .Where(tool => toolSnippets.TryGetValue(tool, out var snippet) && !string.IsNullOrWhiteSpace(snippet))
            .Select(tool => $"- {tool}: {toolSnippets[tool]}")
            .ToArray();

        var guidelines = BuildGuidelines(selectedTools, options.PromptGuidelines);

        var builder = new StringBuilder();
        builder.AppendLine("You are an expert coding agent operating inside PiSharp.");
        builder.AppendLine("You help users by reading files, executing commands, editing code, and coordinating tool calls through the agent runtime.");
        builder.AppendLine();
        builder.AppendLine("Available tools:");
        builder.AppendLine(visibleTools.Length == 0 ? "(none)" : string.Join(Environment.NewLine, visibleTools));
        builder.AppendLine();
        builder.AppendLine("Guidelines:");
        foreach (var guideline in guidelines)
        {
            builder.Append("- ");
            builder.AppendLine(guideline);
        }

        if (appendSection is not null)
        {
            builder.AppendLine();
            builder.AppendLine(appendSection);
        }

        AppendContextFiles(builder, contextFiles);

        builder.AppendLine();
        builder.Append($"Current date: {currentDate}");
        builder.AppendLine();
        builder.Append($"Current working directory: {NormalizeSlashes(workingDirectory)}");
        return builder.ToString();
    }

    private static IReadOnlyList<string> BuildGuidelines(
        IReadOnlyList<string> selectedTools,
        IReadOnlyList<string>? extraGuidelines)
    {
        var guidelines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string guideline)
        {
            if (string.IsNullOrWhiteSpace(guideline))
            {
                return;
            }

            var normalized = guideline.Trim();
            if (seen.Add(normalized))
            {
                guidelines.Add(normalized);
            }
        }

        var toolSet = new HashSet<string>(selectedTools, StringComparer.Ordinal);
        var hasBash = toolSet.Contains(BuiltInToolNames.Bash);
        var hasExplorerTool = toolSet.Contains(BuiltInToolNames.Grep) ||
            toolSet.Contains(BuiltInToolNames.Find) ||
            toolSet.Contains(BuiltInToolNames.Ls);

        if (hasBash && hasExplorerTool)
        {
            Add("Prefer grep/find/ls over bash for repository exploration.");
        }
        else if (hasBash)
        {
            Add("Use bash when shell execution is the most direct way to inspect or verify something.");
        }

        if (toolSet.Contains(BuiltInToolNames.Read))
        {
            Add("Use read before editing unfamiliar files.");
        }

        if (toolSet.Contains(BuiltInToolNames.Edit))
        {
            Add("Prefer edit for targeted updates to existing files.");
        }

        if (toolSet.Contains(BuiltInToolNames.Write))
        {
            Add("Use write for new files or full rewrites.");
        }

        foreach (var guideline in extraGuidelines ?? Array.Empty<string>())
        {
            Add(guideline);
        }

        Add("Be concise in your user-facing responses.");
        Add("Show file paths clearly when referring to code or outputs.");

        return guidelines;
    }

    private static void AppendContextFiles(StringBuilder builder, IReadOnlyList<CodingAgentContextFile> contextFiles)
    {
        if (contextFiles.Count == 0)
        {
            return;
        }

        builder.AppendLine();
        builder.AppendLine();
        builder.AppendLine("# Project Context");
        builder.AppendLine();
        builder.AppendLine("Project-specific instructions and guidelines:");

        foreach (var contextFile in contextFiles)
        {
            builder.AppendLine();
            builder.Append("## ");
            builder.AppendLine(contextFile.Path);
            builder.AppendLine();
            builder.AppendLine(contextFile.Content.TrimEnd());
        }
    }

    private static string NormalizeSlashes(string path) => path.Replace('\\', '/');
}
