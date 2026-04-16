using PiSharp.Tui;

namespace PiSharp.Pods.Tests.Support;

internal sealed class FakeTerminal(int columns = 80, int rows = 24) : ITerminal
{
    public TerminalSize Size { get; set; } = new(columns, rows);

    public List<string> Writes { get; } = [];

    public ValueTask WriteAsync(string output, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Writes.Add(output);
        return ValueTask.CompletedTask;
    }
}
