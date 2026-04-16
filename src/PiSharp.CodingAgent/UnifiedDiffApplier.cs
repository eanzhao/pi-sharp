using System.Text.RegularExpressions;

namespace PiSharp.CodingAgent;

public static class UnifiedDiffApplier
{
    private static readonly Regex HunkHeaderPattern = new(
        "^@@ -(?<oldStart>\\d+)(,(?<oldCount>\\d+))? \\+(?<newStart>\\d+)(,(?<newCount>\\d+))? @@",
        RegexOptions.CultureInvariant);

    public static IReadOnlyList<string> ExtractTargetPaths(string diff) =>
        Parse(diff)
            .Select(static patch => patch.TargetPath)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    public static string Apply(string originalContent, string diff, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(originalContent);
        ArgumentNullException.ThrowIfNull(diff);

        var patches = Parse(diff);
        if (patches.Count == 0)
        {
            throw new InvalidOperationException("No unified diff file patches were found.");
        }

        var patch = filePath is null
            ? patches.Count == 1
                ? patches[0]
                : throw new InvalidOperationException("The diff contains multiple file patches. Specify a file path.")
            : patches.FirstOrDefault(candidate =>
                string.Equals(candidate.TargetPath, filePath, StringComparison.Ordinal) ||
                string.Equals(candidate.OldPath, filePath, StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"The diff does not contain a patch for '{filePath}'.");

        return Apply(originalContent, patch);
    }

    internal static IReadOnlyList<UnifiedDiffFilePatch> Parse(string diff)
    {
        ArgumentNullException.ThrowIfNull(diff);

        var lines = SplitLines(NormalizeLineEndings(diff));
        var patches = new List<UnifiedDiffFilePatch>();

        for (var index = 0; index < lines.Count; index++)
        {
            if (!lines[index].StartsWith("--- ", StringComparison.Ordinal))
            {
                continue;
            }

            if (index + 1 >= lines.Count || !lines[index + 1].StartsWith("+++ ", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Invalid unified diff: missing +++ header after '{lines[index]}'.");
            }

            var oldPath = ParseHeaderPath(lines[index][4..]);
            var newPath = ParseHeaderPath(lines[index + 1][4..]);
            index += 2;

            var hunks = new List<UnifiedDiffHunk>();
            while (index < lines.Count && !lines[index].StartsWith("--- ", StringComparison.Ordinal))
            {
                if (string.IsNullOrEmpty(lines[index]))
                {
                    index++;
                    continue;
                }

                if (!lines[index].StartsWith("@@", StringComparison.Ordinal))
                {
                    index++;
                    continue;
                }

                hunks.Add(ParseHunk(lines, ref index));
            }

            index--;
            patches.Add(new UnifiedDiffFilePatch(oldPath, newPath, hunks.ToArray()));
        }

        return patches;
    }

    internal static string Apply(string originalContent, UnifiedDiffFilePatch patch)
    {
        var normalizedContent = NormalizeLineEndings(originalContent);
        var originalHasTrailingNewline = normalizedContent.EndsWith("\n", StringComparison.Ordinal);
        var lineEnding = originalContent.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var originalLines = SplitContentLines(normalizedContent);

        var resultLines = new List<string>(originalLines.Count);
        var sourceIndex = 0;

        foreach (var hunk in patch.Hunks)
        {
            var expectedIndex = Math.Max(hunk.OldStart - 1, 0);
            if (expectedIndex < sourceIndex)
            {
                throw new InvalidOperationException($"Patch for '{patch.TargetPath}' contains overlapping hunks near line {hunk.OldStart}.");
            }

            while (sourceIndex < expectedIndex && sourceIndex < originalLines.Count)
            {
                resultLines.Add(originalLines[sourceIndex]);
                sourceIndex++;
            }

            foreach (var line in hunk.Lines)
            {
                switch (line.Kind)
                {
                    case UnifiedDiffLineKind.Context:
                        VerifyMatch(originalLines, sourceIndex, line.Text, patch.TargetPath, hunk.OldStart, "context");
                        resultLines.Add(originalLines[sourceIndex]);
                        sourceIndex++;
                        break;
                    case UnifiedDiffLineKind.Remove:
                        VerifyMatch(originalLines, sourceIndex, line.Text, patch.TargetPath, hunk.OldStart, "remove");
                        sourceIndex++;
                        break;
                    case UnifiedDiffLineKind.Add:
                        resultLines.Add(line.Text);
                        break;
                    default:
                        throw new InvalidOperationException($"Unsupported diff line kind '{line.Kind}'.");
                }
            }
        }

        while (sourceIndex < originalLines.Count)
        {
            resultLines.Add(originalLines[sourceIndex]);
            sourceIndex++;
        }

        var hasTrailingNewline = patch.Hunks.Count > 0 ? true : originalHasTrailingNewline;
        return JoinLines(resultLines, lineEnding, hasTrailingNewline);
    }

    private static UnifiedDiffHunk ParseHunk(IReadOnlyList<string> lines, ref int index)
    {
        var header = lines[index];
        var match = HunkHeaderPattern.Match(header);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Invalid unified diff hunk header: '{header}'.");
        }

        var oldStart = int.Parse(match.Groups["oldStart"].Value);
        var oldCount = ParseCount(match.Groups["oldCount"].Value);
        var newStart = int.Parse(match.Groups["newStart"].Value);
        var newCount = ParseCount(match.Groups["newCount"].Value);
        index++;

        var hunkLines = new List<UnifiedDiffLine>();
        while (index < lines.Count)
        {
            var line = lines[index];
            if (line.StartsWith("@@", StringComparison.Ordinal) ||
                line.StartsWith("--- ", StringComparison.Ordinal))
            {
                break;
            }

            if (line.StartsWith("\\", StringComparison.Ordinal))
            {
                index++;
                continue;
            }

            if (line.Length == 0)
            {
                throw new InvalidOperationException("Invalid unified diff: hunk lines must start with ' ', '+', or '-'.");
            }

            hunkLines.Add(line[0] switch
            {
                ' ' => new UnifiedDiffLine(UnifiedDiffLineKind.Context, line[1..]),
                '+' => new UnifiedDiffLine(UnifiedDiffLineKind.Add, line[1..]),
                '-' => new UnifiedDiffLine(UnifiedDiffLineKind.Remove, line[1..]),
                _ => throw new InvalidOperationException($"Invalid unified diff line: '{line}'."),
            });

            index++;
        }

        return new UnifiedDiffHunk(oldStart, oldCount, newStart, newCount, hunkLines.ToArray());
    }

    private static string ParseHeaderPath(string value)
    {
        var headerValue = value.Trim();
        var tabIndex = headerValue.IndexOf('\t');
        if (tabIndex >= 0)
        {
            headerValue = headerValue[..tabIndex];
        }

        if (headerValue == "/dev/null")
        {
            return headerValue;
        }

        if ((headerValue.StartsWith("a/", StringComparison.Ordinal) ||
             headerValue.StartsWith("b/", StringComparison.Ordinal)) &&
            headerValue.Length > 2)
        {
            headerValue = headerValue[2..];
        }

        return headerValue;
    }

    private static int ParseCount(string value) =>
        string.IsNullOrEmpty(value) ? 1 : int.Parse(value);

    private static void VerifyMatch(
        IReadOnlyList<string> originalLines,
        int index,
        string expected,
        string path,
        int oldStart,
        string operation)
    {
        var actual = index < originalLines.Count ? originalLines[index] : "<end of file>";
        if (index >= originalLines.Count || !string.Equals(actual, expected, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Failed to apply {operation} hunk for '{path}' near original line {oldStart}: expected '{expected}', found '{actual}'.");
        }
    }

    private static string NormalizeLineEndings(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static IReadOnlyList<string> SplitLines(string text) =>
        text.Split('\n');

    private static List<string> SplitContentLines(string content)
    {
        if (content.Length == 0)
        {
            return [];
        }

        var lines = content.Split('\n').ToList();
        if (content.EndsWith("\n", StringComparison.Ordinal))
        {
            lines.RemoveAt(lines.Count - 1);
        }

        return lines;
    }

    private static string JoinLines(IReadOnlyList<string> lines, string lineEnding, bool hasTrailingNewline)
    {
        if (lines.Count == 0)
        {
            return string.Empty;
        }

        var content = string.Join(lineEnding, lines);
        return hasTrailingNewline ? content + lineEnding : content;
    }
}

internal sealed record UnifiedDiffFilePatch(
    string OldPath,
    string NewPath,
    IReadOnlyList<UnifiedDiffHunk> Hunks)
{
    public string TargetPath => NewPath == "/dev/null" ? OldPath : NewPath;
    public bool IsNewFile => OldPath == "/dev/null";
    public bool IsDeletion => NewPath == "/dev/null";
}

internal sealed record UnifiedDiffHunk(
    int OldStart,
    int OldCount,
    int NewStart,
    int NewCount,
    IReadOnlyList<UnifiedDiffLine> Lines);

internal sealed record UnifiedDiffLine(UnifiedDiffLineKind Kind, string Text);

internal enum UnifiedDiffLineKind
{
    Context,
    Add,
    Remove,
}
