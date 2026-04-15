namespace PiSharp.Tui.Tests;

public sealed class AnsiStringTests
{
    [Fact]
    public void Strip_ReturnsPlainText_WhenNoEscapeSequences()
    {
        Assert.Equal("hello", AnsiString.Strip("hello"));
    }

    [Fact]
    public void Strip_RemovesCsiSequences()
    {
        Assert.Equal("hello", AnsiString.Strip("\u001b[31mhello\u001b[0m"));
    }

    [Fact]
    public void Strip_RemovesOscSequences()
    {
        Assert.Equal("text", AnsiString.Strip("\u001b]0;title\u0007text"));
    }

    [Fact]
    public void Strip_ReturnsEmpty_ForEmptyInput()
    {
        Assert.Equal("", AnsiString.Strip(""));
    }

    [Fact]
    public void Strip_ReturnsNull_ForNullInput()
    {
        Assert.Null(AnsiString.Strip(null!));
    }

    [Fact]
    public void VisibleLength_ReturnsCorrectCount_WithAnsiCodes()
    {
        Assert.Equal(5, AnsiString.VisibleLength("\u001b[1;32mhello\u001b[0m"));
    }

    [Fact]
    public void VisibleLength_ReturnsStringLength_WithoutAnsiCodes()
    {
        Assert.Equal(5, AnsiString.VisibleLength("world"));
    }

    [Fact]
    public void Fit_PadsShortString_ToTargetWidth()
    {
        var result = AnsiString.Fit("hi", 5);

        Assert.Equal("hi   ", result);
        Assert.Equal(5, result.Length);
    }

    [Fact]
    public void Fit_TruncatesLongString_ToTargetWidth()
    {
        var result = AnsiString.Fit("hello world", 5);

        Assert.Equal("hello", result);
    }

    [Fact]
    public void Fit_PreservesAnsiSequences_WhenPadding()
    {
        var result = AnsiString.Fit("\u001b[31mhi\u001b[0m", 5);

        Assert.Contains("\u001b[31m", result);
        Assert.Equal(5, AnsiString.VisibleLength(result));
    }

    [Fact]
    public void Fit_TruncatesWithAnsi_AppendingReset()
    {
        var result = AnsiString.Fit("\u001b[31mhello world\u001b[0m", 5);

        Assert.Equal(5, AnsiString.VisibleLength(result));
        Assert.EndsWith(Ansi.Reset, result);
    }

    [Fact]
    public void Fit_ReturnsSpaces_ForEmptyStringInput()
    {
        var result = AnsiString.Fit("", 4);

        Assert.Equal("    ", result);
    }

    [Fact]
    public void Fit_ReturnsEmpty_WhenWidthIsZero()
    {
        Assert.Equal("", AnsiString.Fit("hello", 0));
    }

    [Fact]
    public void Fit_ReturnsExactString_WhenLengthEqualsWidth()
    {
        Assert.Equal("hello", AnsiString.Fit("hello", 5));
    }
}
