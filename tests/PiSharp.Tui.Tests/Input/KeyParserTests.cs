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
}
