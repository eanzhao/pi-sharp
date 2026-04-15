namespace PiSharp.Tui.Tests;

public sealed class DifferentialRendererTests
{
    [Fact]
    public void Render_PerformsFullRedraw_WhenNoPreviousFrameExists()
    {
        var renderer = new DifferentialRenderer();
        var frame = new ScreenFrame(new TerminalSize(12, 4), ["hello", "world"]);

        var diff = renderer.Render(frame, ScreenFrame.Empty);

        Assert.Contains(Ansi.BeginSynchronizedUpdate, diff);
        Assert.Contains(Ansi.MoveCursor(0, 0), diff);
        Assert.Contains("hello", diff);
        Assert.Contains("world", diff);
        Assert.Contains(Ansi.ClearToEndOfScreen, diff);
    }

    [Fact]
    public void Render_UpdatesOnlyChangedRows_WhenFrameShapeIsStable()
    {
        var renderer = new DifferentialRenderer();
        var previous = new ScreenFrame(new TerminalSize(12, 4), ["alpha", "bravo"]);
        var next = new ScreenFrame(new TerminalSize(12, 4), ["alpha", "charlie"]);

        var diff = renderer.Render(next, previous);

        Assert.DoesNotContain($"{Ansi.MoveCursor(0, 0)}alpha", diff);
        Assert.Contains($"{Ansi.MoveCursor(1, 0)}charlie", diff);
    }
}
