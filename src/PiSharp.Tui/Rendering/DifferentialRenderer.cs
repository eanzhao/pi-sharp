using System.Text;

namespace PiSharp.Tui;

public readonly record struct ScreenFrame(TerminalSize Size, IReadOnlyList<string> Lines)
{
    public static ScreenFrame Empty => new(new TerminalSize(1, 1), Array.Empty<string>());
}

public sealed class DifferentialRenderer
{
    public string Render(ScreenFrame next, ScreenFrame previous, bool forceFullRedraw = false)
    {
        var builder = new StringBuilder();
        builder.Append(Ansi.BeginSynchronizedUpdate);
        builder.Append(Ansi.HideCursor);

        var nextLines = Normalize(next);
        var previousLines = Normalize(previous);
        var requiresFullRedraw = forceFullRedraw || next.Size != previous.Size || previous.Lines.Count == 0;

        if (requiresFullRedraw)
        {
            builder.Append(Ansi.MoveCursor(0, 0));

            for (var row = 0; row < nextLines.Count; row++)
            {
                builder.Append(nextLines[row]);
                builder.Append(Ansi.ClearToEndOfLine);

                if (row < nextLines.Count - 1)
                {
                    builder.Append('\n');
                }
            }

            builder.Append(Ansi.ClearToEndOfScreen);
        }
        else
        {
            var maxRows = Math.Max(previousLines.Count, nextLines.Count);
            for (var row = 0; row < maxRows; row++)
            {
                var previousLine = row < previousLines.Count ? previousLines[row] : string.Empty;
                var nextLine = row < nextLines.Count ? nextLines[row] : string.Empty;

                if (previousLine == nextLine)
                {
                    continue;
                }

                builder.Append(Ansi.MoveCursor(row, 0));
                builder.Append(nextLine);
                builder.Append(Ansi.ClearToEndOfLine);
            }
        }

        builder.Append(Ansi.Reset);
        builder.Append(Ansi.EndSynchronizedUpdate);
        return builder.ToString();
    }

    private static IReadOnlyList<string> Normalize(ScreenFrame frame)
    {
        var maxRows = Math.Max(0, frame.Size.Rows);
        var lines = new List<string>(Math.Min(frame.Lines.Count, maxRows));

        foreach (var line in frame.Lines.Take(maxRows))
        {
            lines.Add(AnsiString.Fit(line, frame.Size.Columns));
        }

        return lines;
    }
}
