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
        while (remaining.Length > width)
        {
            var splitIndex = remaining[..width].LastIndexOf(' ');
            if (splitIndex <= 0)
            {
                splitIndex = width;
            }

            result.Add(remaining[..splitIndex].TrimEnd());
            remaining = remaining[splitIndex..].TrimStart();
        }

        result.Add(remaining);
    }
}
