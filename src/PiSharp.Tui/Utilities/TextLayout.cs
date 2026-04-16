namespace PiSharp.Tui;

public static class TextLayout
{
    public static IReadOnlyList<string> Wrap(string text, int width)
    {
        if (width <= 0)
        {
            return Array.Empty<string>();
        }

        var normalized = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var result = new List<string>();

        foreach (var line in normalized.Split('\n'))
        {
            AppendWrappedLine(result, line, width);
        }

        if (result.Count == 0)
        {
            result.Add(string.Empty);
        }

        return result;
    }

    public static string PadToWidth(string text, int width)
        => AnsiString.Fit(text, width);

    private static void AppendWrappedLine(List<string> result, string line, int width)
    {
        if (line.Length == 0)
        {
            result.Add(string.Empty);
            return;
        }

        var remaining = line;
        while (AnsiString.VisibleLength(remaining) > width)
        {
            var splitIndex = FindSplitIndex(remaining, width);
            if (splitIndex <= 0)
            {
                splitIndex = FindCharIndexAtVisibleOffset(remaining, width);
                if (splitIndex <= 0)
                {
                    splitIndex = remaining.Length;
                }
            }

            result.Add(remaining[..splitIndex].TrimEnd());
            remaining = remaining[splitIndex..].TrimStart();
        }

        result.Add(remaining);
    }

    private static int FindSplitIndex(string text, int width)
    {
        var visibleOffset = FindCharIndexAtVisibleOffset(text, width);
        var candidate = text[..visibleOffset].LastIndexOf(' ');
        return candidate > 0 ? candidate : -1;
    }

    private static int FindCharIndexAtVisibleOffset(string text, int targetVisible)
    {
        var visible = 0;
        var i = 0;
        while (i < text.Length && visible < targetVisible)
        {
            if (text[i] == '\u001b')
            {
                i = SkipEscapeSequence(text, i);
                continue;
            }

            visible++;
            i++;
        }

        return i;
    }

    private static int SkipEscapeSequence(string text, int start)
    {
        if (start + 1 >= text.Length)
        {
            return start + 1;
        }

        return text[start + 1] switch
        {
            '[' => SkipCsi(text, start + 2),
            ']' => SkipOsc(text, start + 2),
            _ => start + 2,
        };
    }

    private static int SkipCsi(string text, int index)
    {
        while (index < text.Length)
        {
            var c = text[index];
            index++;
            if (c is >= '@' and <= '~')
            {
                break;
            }
        }

        return index;
    }

    private static int SkipOsc(string text, int index)
    {
        while (index < text.Length)
        {
            if (text[index] == '\u0007')
            {
                return index + 1;
            }

            if (text[index] == '\u001b' && index + 1 < text.Length && text[index + 1] == '\\')
            {
                return index + 2;
            }

            index++;
        }

        return index;
    }
}
