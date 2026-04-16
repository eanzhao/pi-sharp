namespace PiSharp.CodingAgent;

public static class UnifiedDiffApplier
{
    public static string Apply(string originalContent, string diff)
    {
        ArgumentNullException.ThrowIfNull(originalContent);
        ArgumentNullException.ThrowIfNull(diff);

        var originalLines = originalContent.Split('\n');
        var result = new List<string>(originalLines);
        var hunks = ParseHunks(diff);
        var offset = 0;

        foreach (var hunk in hunks)
        {
            offset = ApplyHunk(result, hunk, offset);
        }

        return string.Join('\n', result);
    }

    private static List<DiffHunk> ParseHunks(string diff)
    {
        var hunks = new List<DiffHunk>();
        var lines = diff.Split('\n');
        DiffHunk? current = null;

        foreach (var line in lines)
        {
            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                current = ParseHunkHeader(line);
                if (current is not null)
                {
                    hunks.Add(current);
                }

                continue;
            }

            if (line.StartsWith("---", StringComparison.Ordinal) ||
                line.StartsWith("+++", StringComparison.Ordinal))
            {
                continue;
            }

            if (current is null)
            {
                continue;
            }

            if (line.StartsWith('-'))
            {
                current.Lines.Add(new DiffLine(DiffLineKind.Remove, line[1..]));
            }
            else if (line.StartsWith('+'))
            {
                current.Lines.Add(new DiffLine(DiffLineKind.Add, line[1..]));
            }
            else if (line.StartsWith(' '))
            {
                current.Lines.Add(new DiffLine(DiffLineKind.Context, line[1..]));
            }
            else if (line.Length > 0)
            {
                current.Lines.Add(new DiffLine(DiffLineKind.Context, line));
            }
        }

        return hunks;
    }

    private static DiffHunk? ParseHunkHeader(string line)
    {
        var start = line.IndexOf("-", StringComparison.Ordinal) + 1;
        var comma = line.IndexOf(",", start, StringComparison.Ordinal);
        var space = line.IndexOf(" +", start, StringComparison.Ordinal);

        if (start <= 0 || space < 0)
        {
            return null;
        }

        var startLine = int.Parse(comma > start ? line[start..comma] : line[start..space]);
        return new DiffHunk { OriginalStartLine = startLine };
    }

    private static int ApplyHunk(List<string> result, DiffHunk hunk, int offset)
    {
        var position = hunk.OriginalStartLine - 1 + offset;
        var removedCount = 0;
        var addedCount = 0;

        foreach (var line in hunk.Lines)
        {
            switch (line.Kind)
            {
                case DiffLineKind.Context:
                    if (position < result.Count && result[position].TrimEnd('\r') != line.Content.TrimEnd('\r'))
                    {
                        throw new InvalidOperationException(
                            $"Context mismatch at line {position + 1}: expected '{line.Content}', got '{result[position]}'.");
                    }

                    position++;
                    break;

                case DiffLineKind.Remove:
                    if (position < result.Count && result[position].TrimEnd('\r') != line.Content.TrimEnd('\r'))
                    {
                        throw new InvalidOperationException(
                            $"Remove mismatch at line {position + 1}: expected '{line.Content}', got '{result[position]}'.");
                    }

                    result.RemoveAt(position);
                    removedCount++;
                    break;

                case DiffLineKind.Add:
                    result.Insert(position, line.Content);
                    position++;
                    addedCount++;
                    break;
            }
        }

        return offset + addedCount - removedCount;
    }

    private sealed class DiffHunk
    {
        public int OriginalStartLine { get; init; }
        public List<DiffLine> Lines { get; } = [];
    }

    private sealed record DiffLine(DiffLineKind Kind, string Content);

    private enum DiffLineKind { Context, Add, Remove }
}
