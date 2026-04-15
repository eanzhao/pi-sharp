namespace PiSharp.Tui.Tests;

public sealed class BoxTests
{
    [Fact]
    public void Render_DrawsBordersWithoutTitle()
    {
        var box = new Box();
        var lines = box.Render(new RenderContext(10));

        Assert.Equal(3, lines.Count);
        Assert.StartsWith("+", lines[0]);
        Assert.EndsWith("+", lines[0]);
        Assert.StartsWith("|", lines[1]);
        Assert.EndsWith("|", lines[1]);
        Assert.StartsWith("+", lines[2]);
        Assert.EndsWith("+", lines[2]);
    }

    [Fact]
    public void Render_IncludesTitle_InTopBorder()
    {
        var box = new Box(title: "Test");
        var lines = box.Render(new RenderContext(20));

        Assert.Contains("Test", lines[0]);
        Assert.StartsWith("+", lines[0]);
        Assert.EndsWith("+", lines[0]);
    }

    [Fact]
    public void Render_RendersChildContent()
    {
        var box = new Box();
        box.AddChild(new Text( "hello"));
        var lines = box.Render(new RenderContext(12));

        Assert.True(lines.Count >= 3);
        Assert.Contains(lines, line => line.Contains("hello"));
    }

    [Fact]
    public void Render_AppliesPadding()
    {
        var box = new Box(padding: 1);
        box.AddChild(new Text( "x"));
        var lines = box.Render(new RenderContext(10));

        Assert.True(lines.Count >= 5);
        Assert.Equal($"|{new string(' ', 8)}|", lines[1]);
        var lastContentIndex = lines.Count - 2;
        Assert.Equal($"|{new string(' ', 8)}|", lines[lastContentIndex]);
    }

    [Fact]
    public void Render_EmptyBox_HasMinimumContentLine()
    {
        var box = new Box();
        var lines = box.Render(new RenderContext(8));

        Assert.Equal(3, lines.Count);
    }
}
