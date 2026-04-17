using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.AI;
using PiSharp.Agent;

namespace PiSharp.CodingAgent;

public sealed class CodingAgentToolOptions
{
    public int ReadMaxLines { get; init; } = 200;

    public int ReadMaxCharacters { get; init; } = 32_000;

    public int LsMaxEntries { get; init; } = 500;

    public int FindMaxResults { get; init; } = 200;

    public int GrepMaxResults { get; init; } = 200;

    public int BashTimeoutMilliseconds { get; init; } = 30_000;

    public int BashMaxOutputCharacters { get; init; } = 12_000;
}

public sealed record EditSegment(string OldText, string NewText);

public static class CodingAgentTools
{
    private const string ImageReadMarkerPrefix = "__PI_SHARP_IMAGE__:";
    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> GrepFileTypeExtensions =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["c"] = new HashSet<string>([".c", ".h"], StringComparer.OrdinalIgnoreCase),
            ["cpp"] = new HashSet<string>([".cc", ".cpp", ".cxx", ".hh", ".hpp", ".hxx"], StringComparer.OrdinalIgnoreCase),
            ["cs"] = new HashSet<string>([".cs"], StringComparer.OrdinalIgnoreCase),
            ["csharp"] = new HashSet<string>([".cs"], StringComparer.OrdinalIgnoreCase),
            ["css"] = new HashSet<string>([".css", ".less", ".sass", ".scss"], StringComparer.OrdinalIgnoreCase),
            ["go"] = new HashSet<string>([".go"], StringComparer.OrdinalIgnoreCase),
            ["html"] = new HashSet<string>([".htm", ".html"], StringComparer.OrdinalIgnoreCase),
            ["java"] = new HashSet<string>([".java"], StringComparer.OrdinalIgnoreCase),
            ["js"] = new HashSet<string>([".cjs", ".js", ".jsx", ".mjs"], StringComparer.OrdinalIgnoreCase),
            ["javascript"] = new HashSet<string>([".cjs", ".js", ".jsx", ".mjs"], StringComparer.OrdinalIgnoreCase),
            ["json"] = new HashSet<string>([".json"], StringComparer.OrdinalIgnoreCase),
            ["kt"] = new HashSet<string>([".kt", ".kts"], StringComparer.OrdinalIgnoreCase),
            ["md"] = new HashSet<string>([".md", ".mdx"], StringComparer.OrdinalIgnoreCase),
            ["markdown"] = new HashSet<string>([".md", ".mdx"], StringComparer.OrdinalIgnoreCase),
            ["php"] = new HashSet<string>([".php"], StringComparer.OrdinalIgnoreCase),
            ["py"] = new HashSet<string>([".py"], StringComparer.OrdinalIgnoreCase),
            ["python"] = new HashSet<string>([".py"], StringComparer.OrdinalIgnoreCase),
            ["rb"] = new HashSet<string>([".rb"], StringComparer.OrdinalIgnoreCase),
            ["rs"] = new HashSet<string>([".rs"], StringComparer.OrdinalIgnoreCase),
            ["sh"] = new HashSet<string>([".bash", ".sh", ".zsh"], StringComparer.OrdinalIgnoreCase),
            ["sql"] = new HashSet<string>([".sql"], StringComparer.OrdinalIgnoreCase),
            ["swift"] = new HashSet<string>([".swift"], StringComparer.OrdinalIgnoreCase),
            ["ts"] = new HashSet<string>([".cts", ".mts", ".ts", ".tsx"], StringComparer.OrdinalIgnoreCase),
            ["typescript"] = new HashSet<string>([".cts", ".mts", ".ts", ".tsx"], StringComparer.OrdinalIgnoreCase),
            ["xml"] = new HashSet<string>([".xml"], StringComparer.OrdinalIgnoreCase),
            ["yaml"] = new HashSet<string>([".yaml", ".yml"], StringComparer.OrdinalIgnoreCase),
            ["yml"] = new HashSet<string>([".yaml", ".yml"], StringComparer.OrdinalIgnoreCase),
        };

    public static IReadOnlyDictionary<string, string> PromptSnippets { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BuiltInToolNames.Read] = "Read file contents from the working directory",
            [BuiltInToolNames.Bash] = "Run shell commands from the working directory",
            [BuiltInToolNames.Edit] = "Apply targeted text replacements to existing files",
            [BuiltInToolNames.Write] = "Create or overwrite files",
            [BuiltInToolNames.EditDiff] = "Apply unified diff patches to files",
            [BuiltInToolNames.Grep] = "Search file contents recursively",
            [BuiltInToolNames.Find] = "Find files and directories by name",
            [BuiltInToolNames.Ls] = "List directory contents",
        };

    public static IReadOnlyDictionary<string, AgentTool> CreateAll(
        string workingDirectory,
        CodingAgentToolOptions? options = null)
    {
        var runtime = new BuiltInToolRuntime(new WorkingDirectoryScope(workingDirectory), options ?? new CodingAgentToolOptions());

        return new Dictionary<string, AgentTool>(StringComparer.Ordinal)
        {
            [BuiltInToolNames.Read] = CreateReadTool(runtime),
            [BuiltInToolNames.Bash] = CreateBashTool(runtime),
            [BuiltInToolNames.Edit] = CreateEditTool(runtime),
            [BuiltInToolNames.Write] = AgentTool.Create(
                runtime.WriteAsync,
                name: BuiltInToolNames.Write,
                description: "Create or overwrite a file in the working directory."),
            [BuiltInToolNames.Grep] = AgentTool.Create(
                runtime.GrepAsync,
                name: BuiltInToolNames.Grep,
                description: "Search file contents recursively with a regular expression."),
            [BuiltInToolNames.Find] = AgentTool.Create(
                runtime.FindAsync,
                name: BuiltInToolNames.Find,
                description: "Find files and directories whose relative path contains the given pattern."),
            [BuiltInToolNames.Ls] = AgentTool.Create(
                runtime.LsAsync,
                name: BuiltInToolNames.Ls,
                description: "List directory contents from the working directory."),
            [BuiltInToolNames.EditDiff] = AgentTool.Create(
                runtime.EditDiffAsync,
                name: BuiltInToolNames.EditDiff,
                description: "Apply a unified diff patch to files in the working directory."),
        };
    }

    public static IReadOnlyList<AgentTool> CreateSelected(
        string workingDirectory,
        IEnumerable<string> toolNames,
        CodingAgentToolOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(toolNames);

        var allTools = CreateAll(workingDirectory, options);
        var selectedTools = new List<AgentTool>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var toolName in toolNames)
        {
            if (!seen.Add(toolName))
            {
                continue;
            }

            if (!allTools.TryGetValue(toolName, out var tool))
            {
                throw new ArgumentException($"Unknown built-in tool '{toolName}'.", nameof(toolNames));
            }

            selectedTools.Add(tool);
        }

        return selectedTools;
    }

    private static AgentTool CreateReadTool(BuiltInToolRuntime runtime)
    {
        const string description = "Read a file from the working directory. Supports offset and limit for partial reads. Image files return multimodal content.";

        var function = AIFunctionFactory.Create(
            runtime.ReadAsync,
            new AIFunctionFactoryOptions
            {
                Name = BuiltInToolNames.Read,
                Description = description,
            });

        return new AgentTool(
            function,
            executeAsync: async (_, arguments, __, cancellationToken) =>
            {
                var path = GetRequiredStringArgument(arguments, "path");
                var offset = GetOptionalInt32Argument(arguments, "offset");
                var limit = GetOptionalInt32Argument(arguments, "limit");
                return await runtime.ExecuteReadToolAsync(path, offset, limit, cancellationToken).ConfigureAwait(false);
            });
    }

    private static AgentTool CreateBashTool(BuiltInToolRuntime runtime)
    {
        const string description = "Run a shell command in the working directory and return stdout, stderr, and exit code.";

        var function = AIFunctionFactory.Create(
            runtime.BashAsync,
            new AIFunctionFactoryOptions
            {
                Name = BuiltInToolNames.Bash,
                Description = description,
            });

        return new AgentTool(
            function,
            executeAsync: async (_, arguments, __, cancellationToken) =>
            {
                var command = GetRequiredStringArgument(arguments, "command");
                var timeoutMs = GetOptionalInt32Argument(arguments, "timeoutMs");
                return await runtime.ExecuteBashToolAsync(command, timeoutMs, cancellationToken).ConfigureAwait(false);
            });
    }

    private static AgentTool CreateEditTool(BuiltInToolRuntime runtime) =>
        AgentTool.Create(
            runtime.EditToolAsync,
            name: BuiltInToolNames.Edit,
            description: "Apply exact text replacements to an existing file in the working directory. Supports single edits and multi-edit arrays.");

    private sealed class BuiltInToolRuntime(WorkingDirectoryScope scope, CodingAgentToolOptions options)
    {
        private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".gif", ".webp",
        };

        private static readonly HashSet<string> PdfExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".pdf",
        };

        public async Task<string> ReadAsync(
            string path,
            int? offset = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            var readResult = await ReadCoreAsync(path, offset, limit, cancellationToken).ConfigureAwait(false);
            return readResult.Value;
        }

        public async Task<IReadOnlyList<AIContent>> ReadContentAsync(
            string path,
            int? offset = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            var readResult = await ReadCoreAsync(path, offset, limit, cancellationToken).ConfigureAwait(false);
            return readResult.Content;
        }

        public async Task<AgentToolResult> ExecuteReadToolAsync(
            string path,
            int? offset = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            var readResult = await ReadCoreAsync(path, offset, limit, cancellationToken).ConfigureAwait(false);
            return new AgentToolResult(readResult.Value, readResult.Content, readResult.Value);
        }

        private async Task<ReadResult> ReadCoreAsync(
            string path,
            int? offset,
            int? limit,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var fullPath = scope.ResolvePath(path);
            EnsureFileExists(fullPath, path);

            var extension = Path.GetExtension(fullPath);
            if (ImageExtensions.Contains(extension))
            {
                if (!CodingAgentContextLoader.TryGetImageMediaType(fullPath, out var mediaType))
                {
                    throw new InvalidOperationException($"Unsupported image file type '{extension}'.");
                }

                var displayPath = scope.ToDisplayPath(fullPath);
                var imageBytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
                return new ReadResult(
                    CreateImageReadMarker(displayPath),
                    [
                        new TextContent($"Reading image: {displayPath}"),
                        new DataContent(imageBytes, mediaType)
                        {
                            Name = displayPath,
                        },
                    ]);
            }

            if (PdfExtensions.Contains(extension))
            {
                var fileInfo = new FileInfo(fullPath);
                var message = $"PDF file detected: {scope.ToDisplayPath(fullPath)} ({fileInfo.Length} bytes). PDF text extraction not yet available.";
                return new ReadResult(message, [new TextContent(message)]);
            }

            var bytes = await File.ReadAllBytesAsync(fullPath, cancellationToken).ConfigureAwait(false);
            if (!CanDecodeUtf8(bytes))
            {
                var fileInfo = new FileInfo(fullPath);
                var message = $"Binary file detected: {scope.ToDisplayPath(fullPath)} ({fileInfo.Length} bytes). Cannot display binary content.";
                return new ReadResult(message, [new TextContent(message)]);
            }

            var content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var normalizedContent = NormalizeLineEndings(content);
            var allLines = normalizedContent.Split('\n');
            var startLine = offset ?? 1;

            if (startLine < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "offset must be greater than or equal to 1.");
            }

            if (allLines.Length == 1 && allLines[0].Length == 0)
            {
                return new ReadResult(string.Empty, [new TextContent(string.Empty)]);
            }

            if (startLine > allLines.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), $"offset {startLine} is beyond the end of the file.");
            }

            var selectedLines = allLines.Skip(startLine - 1).ToList();
            var requestedLimit = limit.GetValueOrDefault(int.MaxValue);
            if (requestedLimit < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), "limit must be greater than or equal to 1.");
            }

            if (selectedLines.Count > requestedLimit)
            {
                selectedLines = selectedLines.Take(requestedLimit).ToList();
            }

            var truncatedByTool = false;
            if (selectedLines.Count > options.ReadMaxLines)
            {
                truncatedByTool = true;
                selectedLines = selectedLines.Take(options.ReadMaxLines).ToList();
            }

            var output = string.Join('\n', selectedLines);
            if (output.Length > options.ReadMaxCharacters)
            {
                truncatedByTool = true;
                output = output[..options.ReadMaxCharacters];
            }

            var nextOffset = startLine + CountLines(output);
            var hasMoreContent = nextOffset <= allLines.Length;
            if (hasMoreContent)
            {
                var note = truncatedByTool || limit is not null
                    ? $"[Use offset={nextOffset} to continue.]"
                    : $"[More content available. Use offset={nextOffset} to continue.]";

                if (!string.IsNullOrEmpty(output))
                {
                    output += "\n\n";
                }

                output += note;
            }

            return new ReadResult(output, [new TextContent(output)]);
        }

        public async Task<string> WriteAsync(
            string path,
            string content,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(content);

            var fullPath = scope.ResolvePath(path);
            var directoryPath = Path.GetDirectoryName(fullPath);
            if (!string.IsNullOrEmpty(directoryPath))
            {
                Directory.CreateDirectory(directoryPath);
            }

            await File.WriteAllTextAsync(fullPath, content, cancellationToken).ConfigureAwait(false);
            return $"Wrote {content.Length} characters to {scope.ToDisplayPath(fullPath)}.";
        }

        public async Task<string> EditAsync(
            string path,
            string oldText,
            string newText,
            bool replaceAll = false,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(oldText);
            ArgumentNullException.ThrowIfNull(newText);

            if (oldText.Length == 0)
            {
                throw new ArgumentException("oldText must not be empty.", nameof(oldText));
            }

            var fullPath = scope.ResolvePath(path);
            EnsureFileExists(fullPath, path);

            var content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var occurrenceCount = CountOccurrences(content, oldText);

            if (occurrenceCount == 0)
            {
                throw new InvalidOperationException($"Could not find the requested text in {path}.");
            }

            if (!replaceAll && occurrenceCount > 1)
            {
                throw new InvalidOperationException($"The requested text occurs {occurrenceCount} times in {path}. Set replaceAll=true or provide a more specific oldText.");
            }

            var updatedContent = replaceAll
                ? content.Replace(oldText, newText, StringComparison.Ordinal)
                : ReplaceFirst(content, oldText, newText);

            await File.WriteAllTextAsync(fullPath, updatedContent, cancellationToken).ConfigureAwait(false);

            return replaceAll
                ? $"Replaced {occurrenceCount} occurrences in {scope.ToDisplayPath(fullPath)}."
                : $"Updated {scope.ToDisplayPath(fullPath)}.";
        }

        public Task<string> EditToolAsync(
            string path,
            string? oldText = null,
            string? newText = null,
            bool replaceAll = false,
            EditSegment[]? edits = null,
            CancellationToken cancellationToken = default)
        {
            if (edits is { Length: > 0 })
            {
                return EditMultiAsync(path, edits, cancellationToken);
            }

            if (edits is not null)
            {
                throw new ArgumentException("edits must contain at least one segment when provided.", nameof(edits));
            }

            if (oldText is null)
            {
                throw new ArgumentNullException(nameof(oldText));
            }

            if (newText is null)
            {
                throw new ArgumentNullException(nameof(newText));
            }

            return EditAsync(path, oldText, newText, replaceAll, cancellationToken);
        }

        public async Task<string> EditMultiAsync(
            string path,
            IReadOnlyList<EditSegment> edits,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(edits);

            if (edits.Count == 0)
            {
                throw new ArgumentException("edits must contain at least one segment.", nameof(edits));
            }

            var fullPath = scope.ResolvePath(path);
            EnsureFileExists(fullPath, path);

            var content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);

            for (var index = 0; index < edits.Count; index++)
            {
                var edit = edits[index] ?? throw new InvalidOperationException($"Edit segment #{index + 1} was null.");
                ArgumentNullException.ThrowIfNull(edit.OldText);
                ArgumentNullException.ThrowIfNull(edit.NewText);

                if (edit.OldText.Length == 0)
                {
                    throw new ArgumentException($"Edit segment #{index + 1} oldText must not be empty.", nameof(edits));
                }

                var occurrenceCount = CountOccurrences(content, edit.OldText);
                if (occurrenceCount == 0)
                {
                    throw new InvalidOperationException($"Could not find edit segment #{index + 1} text in {path}.");
                }

                if (occurrenceCount > 1)
                {
                    throw new InvalidOperationException($"Edit segment #{index + 1} occurs {occurrenceCount} times in {path}. Provide a more specific oldText.");
                }

                content = ReplaceFirst(content, edit.OldText, edit.NewText);
            }

            await File.WriteAllTextAsync(fullPath, content, cancellationToken).ConfigureAwait(false);
            return $"Applied {edits.Count} edits to {scope.ToDisplayPath(fullPath)}.";
        }

        public async Task<string> EditDiffAsync(
            string diff,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(diff);

            var patches = UnifiedDiffApplier.Parse(diff);
            if (patches.Count == 0)
            {
                throw new InvalidOperationException("No unified diff file patches were found.");
            }

            var updatedPaths = new List<string>(patches.Count);

            foreach (var patch in patches)
            {
                var sourcePath = patch.IsNewFile ? null : scope.ResolvePath(patch.OldPath);
                var targetPath = patch.IsDeletion ? null : scope.ResolvePath(patch.NewPath);

                string originalContent;
                if (sourcePath is null)
                {
                    originalContent = string.Empty;
                }
                else
                {
                    EnsureFileExists(sourcePath, patch.OldPath);
                    originalContent = await File.ReadAllTextAsync(sourcePath, cancellationToken).ConfigureAwait(false);
                }

                var updatedContent = UnifiedDiffApplier.Apply(originalContent, patch);

                if (patch.IsDeletion)
                {
                    File.Delete(sourcePath!);
                    updatedPaths.Add(scope.ToDisplayPath(sourcePath!));
                    continue;
                }

                var targetDirectory = Path.GetDirectoryName(targetPath!);
                if (!string.IsNullOrEmpty(targetDirectory))
                {
                    Directory.CreateDirectory(targetDirectory);
                }

                await File.WriteAllTextAsync(targetPath!, updatedContent, cancellationToken).ConfigureAwait(false);
                updatedPaths.Add(scope.ToDisplayPath(targetPath!));

                if (!patch.IsNewFile &&
                    sourcePath is not null &&
                    !string.Equals(sourcePath, targetPath, PathComparison))
                {
                    File.Delete(sourcePath);
                }
            }

            return updatedPaths.Count == 1
                ? $"Applied unified diff to {updatedPaths[0]}."
                : $"Applied unified diff to {updatedPaths.Count} files:\n{string.Join("\n", updatedPaths)}";
        }

        public async Task<string> BashAsync(
            string command,
            int? timeoutMs = null,
            CancellationToken cancellationToken = default)
        {
            var execution = await ExecuteBashCoreAsync(command, timeoutMs, cancellationToken).ConfigureAwait(false);
            return execution.Output;
        }

        public async Task<AgentToolResult> ExecuteBashToolAsync(
            string command,
            int? timeoutMs = null,
            CancellationToken cancellationToken = default)
        {
            var execution = await ExecuteBashCoreAsync(command, timeoutMs, cancellationToken).ConfigureAwait(false);
            return new AgentToolResult(
                execution.Output,
                [new TextContent(execution.Output)],
                execution.MutationTracker);
        }

        private async Task<BashToolExecution> ExecuteBashCoreAsync(
            string command,
            int? timeoutMs,
            CancellationToken cancellationToken)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command);

            var effectiveTimeout = timeoutMs ?? options.BashTimeoutMilliseconds;
            if (effectiveTimeout < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(timeoutMs), "timeoutMs must be greater than or equal to 1.");
            }

            var result = await BashExecutor.ExecuteAsync(
                scope.RootPath,
                command,
                effectiveTimeout,
                cancellationToken).ConfigureAwait(false);

            var tracker = new FileMutationTracker(scope.RootPath);
            tracker.Scan(result.StandardOutput);
            tracker.Scan(result.StandardError);

            return new BashToolExecution(
                FormatBashResult(result, options.BashMaxOutputCharacters),
                tracker);
        }

        public Task<string> LsAsync(
            string? path = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetPath = scope.ResolvePath(path ?? ".");
            if (!Directory.Exists(targetPath))
            {
                throw new DirectoryNotFoundException($"Directory not found: {path ?? "."}");
            }

            var effectiveLimit = limit ?? options.LsMaxEntries;
            if (effectiveLimit < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), "limit must be greater than or equal to 1.");
            }

            var entries = scope.EnumerateDirectoryEntries(targetPath)
                .Select(entry =>
                {
                    var name = Path.GetFileName(entry.Path);
                    if (entry.IsDirectory)
                    {
                        name += "/";
                    }

                    return name;
                })
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (entries.Count == 0)
            {
                return Task.FromResult("(empty directory)");
            }

            var hasMore = entries.Count > effectiveLimit;
            var output = string.Join('\n', entries.Take(effectiveLimit));
            if (hasMore)
            {
                output += $"\n\n[{entries.Count - effectiveLimit} more entries. Increase limit to continue.]";
            }

            return Task.FromResult(output);
        }

        public Task<string> FindAsync(
            string pattern,
            string? path = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
            cancellationToken.ThrowIfCancellationRequested();

            var startPath = scope.ResolvePath(path ?? ".");
            if (!Directory.Exists(startPath) && !File.Exists(startPath))
            {
                throw new FileNotFoundException($"Path not found: {path ?? "."}");
            }

            var effectiveLimit = limit ?? options.FindMaxResults;
            if (effectiveLimit < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), "limit must be greater than or equal to 1.");
            }

            var results = new List<string>();
            var normalizedPattern = pattern.Trim();

            if (File.Exists(startPath))
            {
                var relativeFilePath = scope.ToDisplayPath(startPath);
                if (relativeFilePath.Contains(normalizedPattern, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(relativeFilePath);
                }
            }
            else
            {
                foreach (var entry in scope.EnumerateFileSystemEntries(startPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var displayPath = scope.ToDisplayPath(entry.Path);
                    var candidate = entry.IsDirectory ? displayPath + "/" : displayPath;
                    if (candidate.Contains(normalizedPattern, StringComparison.OrdinalIgnoreCase))
                    {
                        results.Add(candidate);
                        if (results.Count >= effectiveLimit)
                        {
                            break;
                        }
                    }
                }
            }

            if (results.Count == 0)
            {
                return Task.FromResult("No matches found.");
            }

            return Task.FromResult(string.Join('\n', results));
        }

        public Task<string> GrepAsync(
            string pattern,
            string? path = null,
            int? limit = null,
            string? fileType = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(pattern);
            cancellationToken.ThrowIfCancellationRequested();

            var startPath = scope.ResolvePath(path ?? ".");
            if (!Directory.Exists(startPath) && !File.Exists(startPath))
            {
                throw new FileNotFoundException($"Path not found: {path ?? "."}");
            }

            var effectiveLimit = limit ?? options.GrepMaxResults;
            if (effectiveLimit < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), "limit must be greater than or equal to 1.");
            }

            var allowedExtensions = ResolveGrepFileTypeExtensions(fileType);
            var regex = new Regex(pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant);
            var matches = new List<string>();

            IEnumerable<string> files = File.Exists(startPath)
                ? [startPath]
                : scope.EnumerateFileSystemEntries(startPath)
                    .Where(entry => !entry.IsDirectory)
                    .Select(entry => entry.Path);

            if (allowedExtensions is not null)
            {
                files = files.Where(filePath => allowedExtensions.Contains(Path.GetExtension(filePath)));
            }

            foreach (var filePath in files)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var lineNumber = 0;
                    foreach (var line in File.ReadLines(filePath))
                    {
                        lineNumber++;
                        if (!regex.IsMatch(line))
                        {
                            continue;
                        }

                        matches.Add($"{scope.ToDisplayPath(filePath)}:{lineNumber}: {line}");
                        if (matches.Count >= effectiveLimit)
                        {
                            return Task.FromResult(string.Join('\n', matches));
                        }
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                catch (DecoderFallbackException)
                {
                }
            }

            return Task.FromResult(matches.Count == 0 ? "No matches found." : string.Join('\n', matches));
        }

        private static IReadOnlySet<string>? ResolveGrepFileTypeExtensions(string? fileType)
        {
            if (string.IsNullOrWhiteSpace(fileType))
            {
                return null;
            }

            if (GrepFileTypeExtensions.TryGetValue(fileType.Trim(), out var extensions))
            {
                return extensions;
            }

            throw new ArgumentException($"Unsupported fileType '{fileType}'.", nameof(fileType));
        }

        private static void EnsureFileExists(string fullPath, string originalPath)
        {
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"File not found: {originalPath}");
            }
        }

        private static string NormalizeLineEndings(string text) =>
            text.Replace("\r\n", "\n", PathComparison).Replace('\r', '\n');

        private static bool CanDecodeUtf8(byte[] bytes)
        {
            try
            {
                _ = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
                return true;
            }
            catch (DecoderFallbackException)
            {
                return false;
            }
        }

        private static int CountLines(string text) =>
            string.IsNullOrEmpty(text)
                ? 0
                : text.Count(static character => character == '\n') + 1;

        private static int CountOccurrences(string content, string value)
        {
            var count = 0;
            var index = 0;

            while (true)
            {
                index = content.IndexOf(value, index, StringComparison.Ordinal);
                if (index < 0)
                {
                    return count;
                }

                count++;
                index += value.Length;
            }
        }

        private static string ReplaceFirst(string content, string oldText, string newText)
        {
            var index = content.IndexOf(oldText, StringComparison.Ordinal);
            return index < 0
                ? content
                : string.Concat(content.AsSpan(0, index), newText, content.AsSpan(index + oldText.Length));
        }

        private static string FormatBashResult(BashExecutionResult result, int maxOutputCharacters)
        {
            var builder = new StringBuilder();
            builder.Append("Exit code: ");
            builder.AppendLine(result.ExitCode.ToString());
            builder.AppendLine();
            builder.AppendLine("Stdout:");
            builder.AppendLine(Truncate(result.StandardOutput, maxOutputCharacters));
            builder.AppendLine();
            builder.AppendLine("Stderr:");
            builder.Append(Truncate(result.StandardError, maxOutputCharacters));
            return builder.ToString().TrimEnd();
        }

        private static string Truncate(string value, int maxCharacters)
        {
            if (string.IsNullOrEmpty(value))
            {
                return "[no output]";
            }

            if (value.Length <= maxCharacters)
            {
                return value.TrimEnd();
            }

            return $"{value[..maxCharacters].TrimEnd()}\n[output truncated]";
        }

        private sealed record ReadResult(string Value, IReadOnlyList<AIContent> Content);

        private sealed record BashToolExecution(string Output, FileMutationTracker MutationTracker);
    }

    private sealed class WorkingDirectoryScope
    {
        private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            "bin",
            "obj",
        };

        private readonly Lazy<GitIgnoreFilter> _gitIgnoreFilter;

        public WorkingDirectoryScope(string workingDirectory)
        {
            RootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(workingDirectory)
                ? Directory.GetCurrentDirectory()
                : workingDirectory);
            _gitIgnoreFilter = new Lazy<GitIgnoreFilter>(() => new GitIgnoreFilter(RootPath));
        }

        public string RootPath { get; }

        public string ResolvePath(string path)
        {
            var candidate = string.IsNullOrWhiteSpace(path)
                ? RootPath
                : Path.IsPathRooted(path)
                    ? path
                    : Path.Combine(RootPath, path);

            var fullPath = Path.GetFullPath(candidate);
            var relativePath = Path.GetRelativePath(RootPath, fullPath);

            if (relativePath == ".." ||
                relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relativePath.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) ||
                Path.IsPathRooted(relativePath))
            {
                throw new InvalidOperationException($"Path '{path}' escapes the working directory.");
            }

            return fullPath;
        }

        public string ToDisplayPath(string fullPath)
        {
            var relativePath = Path.GetRelativePath(RootPath, fullPath);
            return relativePath == "." ? "." : relativePath.Replace('\\', '/');
        }

        public IEnumerable<FileSystemEntry> EnumerateFileSystemEntries(string startPath)
        {
            var pending = new Stack<string>();
            pending.Push(startPath);

            while (pending.Count > 0)
            {
                var currentPath = pending.Pop();
                foreach (var entry in EnumerateDirectoryEntries(currentPath))
                {
                    yield return entry;

                    if (!entry.IsDirectory)
                    {
                        continue;
                    }

                    if (TryGetAttributes(entry.Path, out var attributes) &&
                        attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    pending.Push(entry.Path);
                }
            }
        }

        public IEnumerable<FileSystemEntry> EnumerateDirectoryEntries(string directoryPath)
        {
            foreach (var childPath in Directory.EnumerateFileSystemEntries(directoryPath))
            {
                var isDirectory = Directory.Exists(childPath);
                if (ShouldSkip(childPath, isDirectory))
                {
                    continue;
                }

                yield return new FileSystemEntry(childPath, isDirectory);
            }
        }

        private bool ShouldSkip(string path, bool isDirectory)
        {
            var name = Path.GetFileName(path);
            if (isDirectory && IgnoredDirectoryNames.Contains(name))
            {
                return true;
            }

            var displayPath = ToDisplayPath(path);
            return displayPath != "." &&
                _gitIgnoreFilter.Value.IsIgnored(isDirectory ? displayPath + "/" : displayPath);
        }

        private static bool TryGetAttributes(string path, out FileAttributes attributes)
        {
            try
            {
                attributes = File.GetAttributes(path);
                return true;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            attributes = default;
            return false;
        }
    }

    private readonly record struct FileSystemEntry(string Path, bool IsDirectory);

    private sealed record BashExecutionResult(int ExitCode, string StandardOutput, string StandardError);

    private static class BashExecutor
    {
        public static async Task<BashExecutionResult> ExecuteAsync(
            string workingDirectory,
            string command,
            int timeoutMilliseconds,
            CancellationToken cancellationToken)
        {
            using var process = new Process
            {
                StartInfo = CreateStartInfo(workingDirectory, command),
            };

            process.Start();

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(timeoutMilliseconds);
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                throw new TimeoutException($"Command timed out after {timeoutMilliseconds} ms.");
            }

            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new BashExecutionResult(process.ExitCode, stdout, stderr);
        }

        private static ProcessStartInfo CreateStartInfo(string workingDirectory, string command)
        {
            var startInfo = OperatingSystem.IsWindows()
                ? new ProcessStartInfo("cmd.exe")
                : new ProcessStartInfo("/bin/sh");

            startInfo.WorkingDirectory = workingDirectory;
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            if (OperatingSystem.IsWindows())
            {
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(command);
            }
            else
            {
                startInfo.ArgumentList.Add("-lc");
                startInfo.ArgumentList.Add(command);
            }

            return startInfo;
        }
    }

    private static string CreateImageReadMarker(string path) => $"{ImageReadMarkerPrefix}{path}";

    private static string GetRequiredStringArgument(AIFunctionArguments arguments, string name)
    {
        var value = GetOptionalStringArgument(arguments, name);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException($"Missing required argument '{name}'.", nameof(arguments));
        }

        return value;
    }

    private static string? GetOptionalStringArgument(AIFunctionArguments arguments, string name)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!arguments.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            string text => text,
            System.Text.Json.JsonElement jsonElement when jsonElement.ValueKind == System.Text.Json.JsonValueKind.String => jsonElement.GetString(),
            _ => value.ToString(),
        };
    }

    private static int? GetOptionalInt32Argument(AIFunctionArguments arguments, string name)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (!arguments.TryGetValue(name, out var value) || value is null)
        {
            return null;
        }

        return value switch
        {
            int number => number,
            long number => checked((int)number),
            System.Text.Json.JsonElement jsonElement when jsonElement.ValueKind == System.Text.Json.JsonValueKind.Number => jsonElement.GetInt32(),
            _ => Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture),
        };
    }
}
