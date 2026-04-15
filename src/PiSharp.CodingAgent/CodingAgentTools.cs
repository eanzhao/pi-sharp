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

public static class CodingAgentTools
{
    public static IReadOnlyDictionary<string, string> PromptSnippets { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [BuiltInToolNames.Read] = "Read file contents from the working directory",
            [BuiltInToolNames.Bash] = "Run shell commands from the working directory",
            [BuiltInToolNames.Edit] = "Apply targeted text replacements to existing files",
            [BuiltInToolNames.Write] = "Create or overwrite files",
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
            [BuiltInToolNames.Read] = AgentTool.Create(
                runtime.ReadAsync,
                name: BuiltInToolNames.Read,
                description: "Read a text file from the working directory. Supports offset and limit for partial reads."),
            [BuiltInToolNames.Bash] = AgentTool.Create(
                runtime.BashAsync,
                name: BuiltInToolNames.Bash,
                description: "Run a shell command in the working directory and return stdout, stderr, and exit code."),
            [BuiltInToolNames.Edit] = AgentTool.Create(
                runtime.EditAsync,
                name: BuiltInToolNames.Edit,
                description: "Apply an exact text replacement to an existing file in the working directory."),
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

    private sealed class BuiltInToolRuntime(WorkingDirectoryScope scope, CodingAgentToolOptions options)
    {
        private static readonly StringComparison PathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        public async Task<string> ReadAsync(
            string path,
            int? offset = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var fullPath = scope.ResolvePath(path);
            EnsureFileExists(fullPath, path);

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
                return string.Empty;
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

            return output;
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

        public async Task<string> BashAsync(
            string command,
            int? timeoutMs = null,
            CancellationToken cancellationToken = default)
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

            return FormatBashResult(result, options.BashMaxOutputCharacters);
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

            var entries = Directory.EnumerateFileSystemEntries(targetPath)
                .Select(fullPath =>
                {
                    var name = Path.GetFileName(fullPath);
                    if (Directory.Exists(fullPath))
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

            var regex = new Regex(pattern, RegexOptions.Multiline | RegexOptions.CultureInvariant);
            var matches = new List<string>();

            IEnumerable<string> files = File.Exists(startPath)
                ? [startPath]
                : scope.EnumerateFileSystemEntries(startPath)
                    .Where(entry => !entry.IsDirectory)
                    .Select(entry => entry.Path);

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

        private static void EnsureFileExists(string fullPath, string originalPath)
        {
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"File not found: {originalPath}");
            }
        }

        private static string NormalizeLineEndings(string text) =>
            text.Replace("\r\n", "\n", PathComparison).Replace('\r', '\n');

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
    }

    private sealed class WorkingDirectoryScope
    {
        private static readonly HashSet<string> IgnoredDirectoryNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            "bin",
            "obj",
        };

        public WorkingDirectoryScope(string workingDirectory)
        {
            RootPath = Path.GetFullPath(string.IsNullOrWhiteSpace(workingDirectory)
                ? Directory.GetCurrentDirectory()
                : workingDirectory);
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
                foreach (var childPath in Directory.EnumerateFileSystemEntries(currentPath))
                {
                    var name = Path.GetFileName(childPath);
                    var isDirectory = Directory.Exists(childPath);

                    yield return new FileSystemEntry(childPath, isDirectory);

                    if (!isDirectory)
                    {
                        continue;
                    }

                    if (IgnoredDirectoryNames.Contains(name))
                    {
                        continue;
                    }

                    if (TryGetAttributes(childPath, out var attributes) &&
                        attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        continue;
                    }

                    pending.Push(childPath);
                }
            }
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
}
