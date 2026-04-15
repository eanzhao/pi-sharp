namespace PiSharp.Tui.Tests;

public sealed class KeyParserTests
{
    [Fact]
    public void Parse_ReturnsCharacterEvent_ForPrintableInput()
    {
        var keyEvent = KeyParser.Parse("a");

        Assert.Equal(KeyKind.Character, keyEvent.Kind);
        Assert.Equal('a', keyEvent.Character);
        Assert.Equal(KeyModifiers.None, keyEvent.Modifiers);
    }

    [Fact]
    public void Parse_ReturnsControlModifier_ForControlSequence()
    {
        var keyEvent = KeyParser.Parse("\u0003");

        Assert.Equal(KeyKind.Character, keyEvent.Kind);
        Assert.Equal('C', keyEvent.Character);
        Assert.Equal(KeyModifiers.Control, keyEvent.Modifiers);
    }

    [Fact]
    public void Parse_ReturnsArrowKey_ForCsiSequence()
    {
        var keyEvent = KeyParser.Parse("\u001b[C");

        Assert.Equal(KeyKind.RightArrow, keyEvent.Kind);
        Assert.Equal(KeyModifiers.None, keyEvent.Modifiers);
    }

    [Fact]
    public void Parse_ReturnsAltModifier_ForEscapedCharacter()
    {
        var keyEvent = KeyParser.Parse("\u001bx");

        Assert.Equal(KeyKind.Character, keyEvent.Kind);
        Assert.Equal('x', keyEvent.Character);
        Assert.Equal(KeyModifiers.Alt, keyEvent.Modifiers);
    }

    [Fact]
    public void Parse_ReturnsEnter_ForCarriageReturn()
    {
        var keyEvent = KeyParser.Parse("\r");

        Assert.Equal(KeyKind.Enter, keyEvent.Kind);
        Assert.Equal(KeyModifiers.None, keyEvent.Modifiers);
    }

    [Fact]
    public void Parse_ReturnsBackspace_ForDel()
    {
        var keyEvent = KeyParser.Parse("\u007f");

        Assert.Equal(KeyKind.Backspace, keyEvent.Kind);
    }

    [Fact]
    public void Parse_ReturnsTab()
    {
        var keyEvent = KeyParser.Parse("\t");

        Assert.Equal(KeyKind.Tab, keyEvent.Kind);
    }

    [Fact]
    public void Parse_ReturnsEscape_ForEscapeAlone()
    {
        var keyEvent = KeyParser.Parse("\u001b");

        Assert.Equal(KeyKind.Escape, keyEvent.Kind);
    }

    [Fact]
    public void Parse_ReturnsCtrlA()
    {
        var keyEvent = KeyParser.Parse("\u0001");

        Assert.Equal(KeyKind.Character, keyEvent.Kind);
        Assert.Equal('A', keyEvent.Character);
        Assert.Equal(KeyModifiers.Control, keyEvent.Modifiers);
    }

    [Fact]
    public void Parse_CsiWithModifier_ReturnsCtrlRightArrow()
    {
        // CSI 1;5C = Ctrl+Right
        var keyEvent = KeyParser.Parse("\u001b[1;5C");

        Assert.Equal(KeyKind.RightArrow, keyEvent.Kind);
        Assert.Equal(KeyModifiers.Control, keyEvent.Modifiers);
    }

    [Fact]
    public void Parse_CsiWithModifier_ReturnsShiftUp()
    {
        // CSI 1;2A = Shift+Up
        var keyEvent = KeyParser.Parse("\u001b[1;2A");

        Assert.Equal(KeyKind.UpArrow, keyEvent.Kind);
        Assert.Equal(KeyModifiers.Shift, keyEvent.Modifiers);
    }

    [Fact]
    public void Parse_ReturnsDelete_ForCsiTilde()
    {
        var keyEvent = KeyParser.Parse("\u001b[3~");

        Assert.Equal(KeyKind.Delete, keyEvent.Kind);
    }

    [Fact]
    public void Parse_ReturnsPageUp()
    {
        var keyEvent = KeyParser.Parse("\u001b[5~");

        Assert.Equal(KeyKind.PageUp, keyEvent.Kind);
    }

    [Fact]
    public void Parse_ReturnsPageDown()
    {
        var keyEvent = KeyParser.Parse("\u001b[6~");

        Assert.Equal(KeyKind.PageDown, keyEvent.Kind);
    }

    [Fact]
    public void Parse_ReturnsHome_ForCsiH()
    {
        var keyEvent = KeyParser.Parse("\u001b[H");

        Assert.Equal(KeyKind.Home, keyEvent.Kind);
    }

    [Fact]
    public void Parse_ReturnsEnd_ForCsiF()
    {
        var keyEvent = KeyParser.Parse("\u001b[F");

        Assert.Equal(KeyKind.End, keyEvent.Kind);
    }

    [Fact]
    public void Parse_ReturnsUnknown_ForEmptyInput()
    {
        var keyEvent = KeyParser.Parse("");

        Assert.Equal(KeyKind.Unknown, keyEvent.Kind);
    }

    [Fact]
    public void Parse_AltArrow_CombinesModifiers()
    {
        // ESC + CSI D = Alt + Left
        var keyEvent = KeyParser.Parse("\u001b\u001b[D");

        Assert.Equal(KeyKind.LeftArrow, keyEvent.Kind);
        Assert.True(keyEvent.Modifiers.HasFlag(KeyModifiers.Alt));
    }
}
