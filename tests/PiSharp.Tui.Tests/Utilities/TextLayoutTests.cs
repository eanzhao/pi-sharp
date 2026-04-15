namespace PiSharp.Tui.Tests;

public sealed class TextLayoutTests
{
    [Fact]
    public void Wrap_ReturnsEmptyLine_ForEmptyString()
    {
        var lines = TextLayout.Wrap("", 10);

        Assert.Single(lines);
        Assert.Equal("", lines[0]);
    }

    [Fact]
    public void Wrap_ReturnsEmptyList_WhenWidthIsZero()
    {
        var lines = TextLayout.Wrap("hello", 0);

        Assert.Empty(lines);
    }

    [Fact]
    public void Wrap_BreaksLongWord_AtWidth()
    {
        var lines = TextLayout.Wrap("abcdefghij", 4);

        Assert.Equal(3, lines.Count);
        Assert.Equal("abcd", lines[0]);
        Assert.Equal("efgh", lines[1]);
        Assert.Equal("ij", lines[2]);
    }

    [Fact]
    public void Wrap_BreaksAtWordBoundary_WhenPossible()
    {
        var lines = TextLayout.Wrap("hello world foo", 11);

        Assert.Equal(2, lines.Count);
        Assert.Equal("hello", lines[0]);
        Assert.Equal("world foo", lines[1]);
    }

    [Fact]
    public void Wrap_PreservesExplicitNewlines()
    {
        var lines = TextLayout.Wrap("line1\nline2\nline3", 20);

        Assert.Equal(3, lines.Count);
        Assert.Equal("line1", lines[0]);
        Assert.Equal("line2", lines[1]);
        Assert.Equal("line3", lines[2]);
    }

    [Fact]
    public void Wrap_NormalizesCrLf_ToLf()
    {
        var lines = TextLayout.Wrap("a\r\nb\rc", 20);

        Assert.Equal(3, lines.Count);
        Assert.Equal("a", lines[0]);
        Assert.Equal("b", lines[1]);
        Assert.Equal("c", lines[2]);
    }

    [Fact]
    public void Wrap_FitsExactWidth_WithoutBreaking()
    {
        var lines = TextLayout.Wrap("abcd", 4);

        Assert.Single(lines);
        Assert.Equal("abcd", lines[0]);
    }

    [Fact]
    public void Wrap_HandlesNull_AsEmptyString()
    {
        var lines = TextLayout.Wrap(null!, 10);

        Assert.Single(lines);
        Assert.Equal("", lines[0]);
    }

    [Fact]
    public void PadToWidth_PadsShortText()
    {
        var result = TextLayout.PadToWidth("hi", 5);

        Assert.Equal("hi   ", result);
    }
}
