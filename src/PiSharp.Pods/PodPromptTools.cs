using System.Text;
using System.Text.RegularExpressions;
using PiSharp.Agent;

namespace PiSharp.Pods;

public static class PodPromptTools
{
    public static IReadOnlyDictionary<string, string> PromptSnippets { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["ls"] = "List files and directories under the working directory",
            ["read"] = "Read a text file with line numbers",
            ["glob"] = "Find files and directories using glob patterns",
            ["rg"] = "Search file contents with regular expressions",
        };

    public static IReadOnlyList<AgentTool> CreateDefault(string workingDirectory, PodPromptToolOptions? options = null)
    {
        var runtime = new ToolRuntime(new WorkingDirectoryScope(workingDirectory), options ?? new PodPromptToolOptions());

        return
        [
            AgentTool.Create(
                runtime.LsAsync,
                name: "ls",
                description: "List files and directories under the working directory."),
            AgentTool.Create(
                runtime.ReadAsync,
                name: "read",
                description: "Read a text file with line numbers. Supports offset and limit."),
            AgentTool.Create(
                runtime.GlobAsync,
                name: "glob",
                description: "Find files and directories by glob pattern, for example **/*.cs."),
            AgentTool.Create(
                runtime.RgAsync,
                name: "rg",
                description: "Search file contents with a regular expression."),
        ];
    }

    private sealed class ToolRuntime(WorkingDirectoryScope scope, PodPromptToolOptions options)
    {
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
                .Select(entryPath =>
                {
                    var entryName = Path.GetFileName(entryPath);
                    return Directory.Exists(entryPath) ? entryName + "/" : entryName;
                })
                .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (entries.Count == 0)
            {
                return Task.FromResult("(empty directory)");
            }

            var selected = entries.Take(effectiveLimit).ToArray();
            if (entries.Count <= effectiveLimit)
            {
                return Task.FromResult(string.Join('\n', selected));
            }

            return Task.FromResult(
                string.Join('\n', selected) +
                $"\n\n[{entries.Count - effectiveLimit} more entries. Increase limit to continue.]");
        }

        public async Task<string> ReadAsync(
            string path,
            int? offset = null,
            int? limit = null,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);

            var fullPath = scope.ResolvePath(path);
            if (!File.Exists(fullPath))
            {
                throw new FileNotFoundException($"File not found: {path}");
            }

            var content = await File.ReadAllTextAsync(fullPath, cancellationToken).ConfigureAwait(false);
            var normalizedContent = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
            var allLines = normalizedContent.Split('\n');
            var startLine = offset ?? 1;
            var requestedLineCount = limit ?? options.ReadMaxLines;

            if (startLine < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), "offset must be greater than or equal to 1.");
            }

            if (requestedLineCount < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), "limit must be greater than or equal to 1.");
            }

            if (allLines.Length == 1 && allLines[0].Length == 0)
            {
                return string.Empty;
            }

            if (startLine > allLines.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset), $"offset {startLine} is beyond the end of the file.");
            }

            var selectedLines = allLines
                .Skip(startLine - 1)
                .Take(requestedLineCount)
                .Select((line, index) => $"{startLine + index}: {line}")
                .ToList();

            if (selectedLines.Count > options.ReadMaxLines)
            {
                selectedLines = selectedLines.Take(options.ReadMaxLines).ToList();
            }

            var output = string.Join('\n', selectedLines);
            if (output.Length > options.ReadMaxCharacters)
            {
                output = output[..options.ReadMaxCharacters].TrimEnd();
                output += "\n[output truncated]";
            }

            var nextOffset = startLine + selectedLines.Count;
            if (nextOffset <= allLines.Length)
            {
                output += $"\n\n[Use offset={nextOffset} to continue.]";
            }

            return output;
        }

        public Task<string> GlobAsync(
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

            var effectiveLimit = limit ?? options.GlobMaxResults;
            if (effectiveLimit < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(limit), "limit must be greater than or equal to 1.");
            }

            var matcher = GlobMatcher.Create(pattern);
            var matches = new List<string>();

            if (File.Exists(startPath))
            {
                var relativeToStart = Path.GetFileName(startPath);
                if (matcher.IsMatch(relativeToStart))
                {
                    matches.Add(scope.ToDisplayPath(startPath));
                }
            }
            else
            {
                foreach (var entry in scope.EnumerateFileSystemEntries(startPath))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var relativeToStart = scope.ToRelativePath(startPath, entry.Path);
                    var candidate = entry.IsDirectory ? relativeToStart + "/" : relativeToStart;
                    if (!matcher.IsMatch(candidate))
                    {
                        continue;
                    }

                    matches.Add(entry.IsDirectory ? scope.ToDisplayPath(entry.Path) + "/" : scope.ToDisplayPath(entry.Path));
                    if (matches.Count >= effectiveLimit)
                    {
                        break;
                    }
                }
            }

            return Task.FromResult(matches.Count == 0 ? "No matches found." : string.Join('\n', matches.OrderBy(match => match, StringComparer.OrdinalIgnoreCase)));
        }

        public Task<string> RgAsync(
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

            var effectiveLimit = limit ?? options.SearchMaxResults;
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
            RootPath = Path.GetFullPath(
                string.IsNullOrWhiteSpace(workingDirectory)
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

        public string ToRelativePath(string startPath, string fullPath)
        {
            var relativePath = Path.GetRelativePath(startPath, fullPath);
            return relativePath.Replace('\\', '/');
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
                    var childName = Path.GetFileName(childPath);
                    var isDirectory = Directory.Exists(childPath);

                    yield return new FileSystemEntry(childPath, isDirectory);

                    if (!isDirectory)
                    {
                        continue;
                    }

                    if (IgnoredDirectoryNames.Contains(childName))
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

    private sealed class GlobMatcher
    {
        private readonly Regex _regex;

        private GlobMatcher(Regex regex)
        {
            _regex = regex;
        }

        public bool IsMatch(string path) => _regex.IsMatch(path.Replace('\\', '/'));

        public static GlobMatcher Create(string pattern)
        {
            var builder = new StringBuilder("^");

            for (var index = 0; index < pattern.Length; index++)
            {
                var character = pattern[index];
                if (character == '*')
                {
                    var isDoubleStar = index + 1 < pattern.Length && pattern[index + 1] == '*';
                    if (isDoubleStar)
                    {
                        builder.Append(".*");
                        index++;
                    }
                    else
                    {
                        builder.Append("[^/]*");
                    }

                    continue;
                }

                if (character == '?')
                {
                    builder.Append("[^/]");
                    continue;
                }

                if (character == '/')
                {
                    builder.Append('/');
                    continue;
                }

                builder.Append(Regex.Escape(character.ToString()));
            }

            builder.Append('$');
            return new GlobMatcher(new Regex(builder.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase));
        }
    }
}
