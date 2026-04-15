namespace PiSharp.Tui.Tests;

public sealed class ShortcutTests
{
    [Fact]
    public void Parse_SimpleKey_ReturnsCorrectShortcut()
    {
        var shortcut = Shortcut.Parse("Enter");

        Assert.Equal(KeyKind.Enter, shortcut.Kind);
        Assert.Equal(KeyModifiers.None, shortcut.Modifiers);
    }

    [Fact]
    public void Parse_CtrlKey_SetsControlModifier()
    {
        var shortcut = Shortcut.Parse("Ctrl+A");

        Assert.Equal(KeyKind.Character, shortcut.Kind);
        Assert.Equal(KeyModifiers.Control, shortcut.Modifiers);
        Assert.Equal('A', shortcut.Character);
    }

    [Fact]
    public void Parse_AltArrow_SetsAltModifierOnArrowKey()
    {
        var shortcut = Shortcut.Parse("Alt+Left");

        Assert.Equal(KeyKind.LeftArrow, shortcut.Kind);
        Assert.Equal(KeyModifiers.Alt, shortcut.Modifiers);
    }

    [Fact]
    public void Parse_MultipleModifiers_CombinesFlags()
    {
        var shortcut = Shortcut.Parse("Ctrl+Shift+Delete");

        Assert.Equal(KeyKind.Delete, shortcut.Kind);
        Assert.Equal(KeyModifiers.Control | KeyModifiers.Shift, shortcut.Modifiers);
    }

    [Fact]
    public void Parse_ThrowsOnInvalidModifier()
    {
        Assert.Throws<ArgumentException>(() => Shortcut.Parse("Foo+A"));
    }

    [Fact]
    public void Parse_ThrowsOnInvalidKey()
    {
        Assert.Throws<ArgumentException>(() => Shortcut.Parse("Ctrl+InvalidKey"));
    }

    [Fact]
    public void Matches_ReturnsTrueForMatchingKeyEvent()
    {
        var shortcut = Shortcut.Parse("Ctrl+K");
        var keyEvent = KeyParser.Parse("\u000b"); // Ctrl+K

        Assert.True(shortcut.Matches(keyEvent));
    }

    [Fact]
    public void Matches_ReturnsFalseForDifferentModifier()
    {
        var shortcut = Shortcut.Parse("Ctrl+A");
        var keyEvent = KeyParser.Parse("a"); // no modifier

        Assert.False(shortcut.Matches(keyEvent));
    }

    [Fact]
    public void Matches_IsCaseInsensitiveForCharacters()
    {
        var shortcut = Shortcut.Parse("Ctrl+a");
        var keyEvent = KeyEvent.FromCharacter('A', KeyModifiers.Control);

        Assert.True(shortcut.Matches(keyEvent));
    }

    [Fact]
    public void ShortcutMap_Register_And_Matches()
    {
        var map = new ShortcutMap();
        map.Register("my-action", "Ctrl+S");

        var keyEvent = KeyEvent.FromCharacter('S', KeyModifiers.Control);
        Assert.True(map.Matches(keyEvent, "my-action"));
        Assert.False(map.Matches(keyEvent, "other-action"));
    }

    [Fact]
    public void ShortcutMap_Matches_IsCaseInsensitiveForActionNames()
    {
        var map = new ShortcutMap();
        map.Register("My-Action", "Enter");

        var keyEvent = KeyParser.Parse("\r");
        Assert.True(map.Matches(keyEvent, "my-action"));
    }

    [Fact]
    public void ShortcutMap_GetShortcuts_ReturnsEmptyForUnknownAction()
    {
        var map = new ShortcutMap();

        Assert.Empty(map.GetShortcuts("unknown"));
    }

    [Fact]
    public void ShortcutMap_CreateDefault_HasExpectedBindings()
    {
        var map = ShortcutMap.CreateDefault();

        Assert.True(map.Matches(KeyParser.Parse("\r"), "input.submit"));
        Assert.True(map.Matches(KeyParser.Parse("\u001b[D"), "input.cursor-left"));
        Assert.True(map.Matches(KeyParser.Parse("\u001b[C"), "input.cursor-right"));
        Assert.True(map.Matches(KeyParser.Parse("\u001b[H"), "input.home"));
        Assert.True(map.Matches(KeyParser.Parse("\u001b[F"), "input.end"));
        Assert.True(map.Matches(KeyParser.Parse("\u007f"), "input.backspace"));
    }

    [Fact]
    public void ShortcutMap_MultipleBindingsForSameAction()
    {
        var map = ShortcutMap.CreateDefault();

        // Ctrl+B is also bound to cursor-left
        var ctrlB = KeyParser.Parse("\u0002");
        Assert.True(map.Matches(ctrlB, "input.cursor-left"));

        // Left arrow is also cursor-left
        var left = KeyParser.Parse("\u001b[D");
        Assert.True(map.Matches(left, "input.cursor-left"));
    }
}
