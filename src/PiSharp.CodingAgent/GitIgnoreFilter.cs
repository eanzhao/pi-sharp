using System.Text.RegularExpressions;

namespace PiSharp.CodingAgent;

internal sealed class GitIgnoreFilter
{
    private readonly string _workingDirectory;
    private readonly IReadOnlyList<GitIgnoreRule> _rules;

    public GitIgnoreFilter(string workingDirectory)
    {
        _workingDirectory = Path.GetFullPath(workingDirectory);
        _rules = LoadRules(_workingDirectory);
    }

    public bool IsIgnored(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath == ".")
        {
            return false;
        }

        var normalizedPath = NormalizePath(relativePath);
        if (normalizedPath.Length == 0)
        {
            return false;
        }

        var isDirectory = normalizedPath.EndsWith("/", StringComparison.Ordinal);
        var trimmedPath = normalizedPath.Trim('/');
        if (trimmedPath.Length == 0)
        {
            return false;
        }

        var pathSegments = trimmedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var ignored = false;

        for (var i = 0; i < pathSegments.Length; i++)
        {
            var candidatePath = string.Join('/', pathSegments.Take(i + 1));
            var candidateIsDirectory = i < pathSegments.Length - 1 || isDirectory;

            foreach (var rule in _rules)
            {
                if (rule.IsMatch(candidatePath, candidateIsDirectory))
                {
                    ignored = !rule.IsNegated;
                }
            }
        }

        return ignored;
    }

        private static IReadOnlyList<GitIgnoreRule> LoadRules(string workingDirectory)
    {
        var directories = new List<string>();
        for (var current = workingDirectory; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current)!)
        {
            directories.Add(current);

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                break;
            }
        }

        directories.Reverse();

        var rules = new List<GitIgnoreRule>();
        foreach (var directory in directories)
        {
            var gitIgnorePath = Path.Combine(directory, ".gitignore");
            if (!File.Exists(gitIgnorePath))
            {
                continue;
            }

            foreach (var rawLine in File.ReadLines(gitIgnorePath))
            {
                var rule = GitIgnoreRule.TryParse(workingDirectory, directory, rawLine);
                if (rule is not null)
                {
                    rules.Add(rule);
                }
            }
        }

        return rules;
    }

    private static string NormalizePath(string path) =>
        path.Replace('\\', '/');

    private sealed class GitIgnoreRule
    {
        private readonly string[] _patternSegments;
        private readonly Regex? _basenameRegex;

        private GitIgnoreRule(
            string workingDirectory,
            string baseDirectory,
            bool isNegated,
            bool directoryOnly,
            bool anchored,
            bool basenameOnly,
            string pattern)
        {
            WorkingDirectory = workingDirectory;
            BaseDirectory = baseDirectory;
            IsNegated = isNegated;
            DirectoryOnly = directoryOnly;
            Anchored = anchored;
            BasenameOnly = basenameOnly;
            Pattern = pattern;
            _patternSegments = basenameOnly
                ? Array.Empty<string>()
                : pattern.Split('/', StringSplitOptions.RemoveEmptyEntries);
            _basenameRegex = basenameOnly ? CreateSegmentRegex(pattern) : null;
        }

        public string WorkingDirectory { get; }
        public string BaseDirectory { get; }
        public bool IsNegated { get; }
        public bool DirectoryOnly { get; }
        public bool Anchored { get; }
        public bool BasenameOnly { get; }
        public string Pattern { get; }

        public static GitIgnoreRule? TryParse(string workingDirectory, string baseDirectory, string rawLine)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                return null;
            }

            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                return null;
            }

            var isNegated = false;
            if (line.StartsWith('!'))
            {
                isNegated = true;
                line = line[1..];
            }

            line = line.Trim();
            if (line.Length == 0)
            {
                return null;
            }

            var directoryOnly = line.EndsWith("/", StringComparison.Ordinal);
            if (directoryOnly)
            {
                line = line[..^1];
            }

            var anchored = line.StartsWith("/", StringComparison.Ordinal);
            if (anchored)
            {
                line = line[1..];
            }

            if (line.Length == 0)
            {
                return null;
            }

            var basenameOnly = !line.Contains('/', StringComparison.Ordinal);
            return new GitIgnoreRule(workingDirectory, baseDirectory, isNegated, directoryOnly, anchored, basenameOnly, line);
        }

        public bool IsMatch(string relativePath, bool isDirectory)
        {
            if (DirectoryOnly && !isDirectory)
            {
                return false;
            }

            if (!TryGetPathRelativeToBase(relativePath, out var relativeToBase))
            {
                return false;
            }

            var normalizedPath = NormalizePath(relativeToBase).Trim('/');
            if (normalizedPath.Length == 0)
            {
                return false;
            }

            if (BasenameOnly)
            {
                return _basenameRegex!.IsMatch(Path.GetFileName(normalizedPath));
            }

            var candidateSegments = normalizedPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (Anchored)
            {
                return MatchSegments(candidateSegments, 0, _patternSegments, 0);
            }

            for (var start = 0; start < candidateSegments.Length; start++)
            {
                if (MatchSegments(candidateSegments, start, _patternSegments, 0))
                {
                    return true;
                }
            }

            return false;
        }

        private bool TryGetPathRelativeToBase(string relativePath, out string relativeToBase)
        {
            var candidateFullPath = Path.GetFullPath(Path.Combine(WorkingDirectory, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var relative = Path.GetRelativePath(BaseDirectory, candidateFullPath);

            if (relative == ".." ||
                relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal) ||
                Path.IsPathRooted(relative))
            {
                relativeToBase = string.Empty;
                return false;
            }

            relativeToBase = relative;
            return true;
        }

        private static bool MatchSegments(string[] candidateSegments, int candidateIndex, string[] patternSegments, int patternIndex)
        {
            if (patternIndex == patternSegments.Length)
            {
                return candidateIndex == candidateSegments.Length;
            }

            if (candidateIndex > candidateSegments.Length)
            {
                return false;
            }

            var currentPattern = patternSegments[patternIndex];
            if (currentPattern == "**")
            {
                for (var i = candidateIndex; i <= candidateSegments.Length; i++)
                {
                    if (MatchSegments(candidateSegments, i, patternSegments, patternIndex + 1))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (candidateIndex >= candidateSegments.Length)
            {
                return false;
            }

            return CreateSegmentRegex(currentPattern).IsMatch(candidateSegments[candidateIndex]) &&
                MatchSegments(candidateSegments, candidateIndex + 1, patternSegments, patternIndex + 1);
        }

        private static Regex CreateSegmentRegex(string pattern)
        {
            var builder = new System.Text.StringBuilder("^");
            foreach (var character in pattern)
            {
                builder.Append(character switch
                {
                    '*' => "[^/]*",
                    '?' => "[^/]",
                    _ => Regex.Escape(character.ToString()),
                });
            }

            builder.Append('$');
            return new Regex(builder.ToString(), RegexOptions.CultureInvariant);
        }
    }
}
