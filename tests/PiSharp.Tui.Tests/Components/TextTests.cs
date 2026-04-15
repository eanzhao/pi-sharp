namespace PiSharp.Tui.Tests;

public sealed class TextTests
{
    [Fact]
    public void Render_WrapsContentAcrossMultipleLines()
    {
        var text = new Text("one two three four", paddingX: 1);

        var lines = text.Render(new RenderContext(10));

        Assert.Equal(3, lines.Count);
        Assert.Contains(" one two ", lines[0]);
        Assert.Contains(" three   ", lines[1]);
        Assert.Contains(" four    ", lines[2]);
    }
}
