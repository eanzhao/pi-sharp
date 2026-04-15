using System.Text;

namespace PiSharp.Tui;

public readonly record struct TerminalSize
{
    public TerminalSize(int columns, int rows)
    {
        Columns = Math.Max(1, columns);
        Rows = Math.Max(1, rows);
    }

    public int Columns { get; }

    public int Rows { get; }
}

public interface ITerminal
{
    TerminalSize Size { get; }

    ValueTask WriteAsync(string output, CancellationToken cancellationToken = default);
}

public sealed class ProcessTerminal(TextWriter? output = null) : ITerminal
{
    private readonly TextWriter _output = output ?? Console.Out;

    public TerminalSize Size => new(Console.WindowWidth, Console.WindowHeight);

    public ValueTask WriteAsync(string output, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _output.Write(output);
        _output.Flush();
        return ValueTask.CompletedTask;
    }
}

public static class Ansi
{
    public const string Escape = "\u001b";
    public const string Reset = $"{Escape}[0m";
    public const string HideCursor = $"{Escape}[?25l";
    public const string ShowCursor = $"{Escape}[?25h";
    public const string ClearToEndOfLine = $"{Escape}[K";
    public const string ClearToEndOfScreen = $"{Escape}[J";
    public const string BeginSynchronizedUpdate = $"{Escape}[?2026h";
    public const string EndSynchronizedUpdate = $"{Escape}[?2026l";

    public static string MoveCursor(int row, int column)
        => $"{Escape}[{Math.Max(1, row + 1)};{Math.Max(1, column + 1)}H";

    public static string MoveUp(int rows)
        => rows <= 0 ? string.Empty : $"{Escape}[{rows}A";

    public static string MoveDown(int rows)
        => rows <= 0 ? string.Empty : $"{Escape}[{rows}B";

    public static string MoveRight(int columns)
        => columns <= 0 ? string.Empty : $"{Escape}[{columns}C";

    public static string MoveLeft(int columns)
        => columns <= 0 ? string.Empty : $"{Escape}[{columns}D";

    public static string SetTitle(string title)
        => $"{Escape}]0;{title}\u0007";
}
