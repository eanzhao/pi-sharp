namespace PiSharp.Tui.Tests;

public sealed class InputTests
{
    [Fact]
    public void HandleInput_InsertsMovesAndDeletesCharacters()
    {
        var input = new Input();
        var shortcuts = ShortcutMap.CreateDefault();

        Assert.True(input.HandleInput(KeyParser.Parse("a"), shortcuts));
        Assert.True(input.HandleInput(KeyParser.Parse("b"), shortcuts));
        Assert.True(input.HandleInput(KeyParser.Parse("\u001b[D"), shortcuts));
        Assert.True(input.HandleInput(KeyParser.Parse("\u007f"), shortcuts));

        Assert.Equal("b", input.Value);
        Assert.Equal(0, input.CursorIndex);
    }

    [Fact]
    public void HandleInput_RaisesSubmitEvent_OnEnter()
    {
        var input = new Input();
        var shortcuts = ShortcutMap.CreateDefault();
        string? submitted = null;
        input.Submitted += value => submitted = value;

        input.HandleInput(KeyParser.Parse("h"), shortcuts);
        input.HandleInput(KeyParser.Parse("i"), shortcuts);
        input.HandleInput(KeyParser.Parse("\r"), shortcuts);

        Assert.Equal("hi", submitted);
    }

    [Fact]
    public void HandleInput_Home_MovesCursorToStart()
    {
        var input = new Input();
        var shortcuts = ShortcutMap.CreateDefault();

        input.HandleInput(KeyParser.Parse("a"), shortcuts);
        input.HandleInput(KeyParser.Parse("b"), shortcuts);
        input.HandleInput(KeyParser.Parse("c"), shortcuts);
        input.HandleInput(KeyParser.Parse("\u001b[H"), shortcuts); // Home

        Assert.Equal(0, input.CursorIndex);
        Assert.Equal("abc", input.Value);
    }

    [Fact]
    public void HandleInput_End_MovesCursorToEnd()
    {
        var input = new Input();
        var shortcuts = ShortcutMap.CreateDefault();

        input.HandleInput(KeyParser.Parse("a"), shortcuts);
        input.HandleInput(KeyParser.Parse("b"), shortcuts);
        input.HandleInput(KeyParser.Parse("\u001b[H"), shortcuts); // Home
        input.HandleInput(KeyParser.Parse("\u001b[F"), shortcuts); // End

        Assert.Equal(2, input.CursorIndex);
    }

    [Fact]
    public void HandleInput_DeleteToStart_RemovesBeforeCursor()
    {
        var input = new Input();
        var shortcuts = ShortcutMap.CreateDefault();

        input.HandleInput(KeyParser.Parse("a"), shortcuts);
        input.HandleInput(KeyParser.Parse("b"), shortcuts);
        input.HandleInput(KeyParser.Parse("c"), shortcuts);
        input.HandleInput(KeyParser.Parse("\u001b[D"), shortcuts); // Left
        input.HandleInput(KeyParser.Parse("\u0015"), shortcuts); // Ctrl+U

        Assert.Equal("c", input.Value);
        Assert.Equal(0, input.CursorIndex);
    }

    [Fact]
    public void HandleInput_DeleteToEnd_RemovesAfterCursor()
    {
        var input = new Input();
        var shortcuts = ShortcutMap.CreateDefault();

        input.HandleInput(KeyParser.Parse("a"), shortcuts);
        input.HandleInput(KeyParser.Parse("b"), shortcuts);
        input.HandleInput(KeyParser.Parse("c"), shortcuts);
        input.HandleInput(KeyParser.Parse("\u001b[H"), shortcuts); // Home
        input.HandleInput(KeyParser.Parse("\u001b[C"), shortcuts); // Right
        input.HandleInput(KeyParser.Parse("\u000b"), shortcuts); // Ctrl+K

        Assert.Equal("a", input.Value);
        Assert.Equal(1, input.CursorIndex);
    }

    [Fact]
    public void HandleInput_Delete_RemovesCharacterAtCursor()
    {
        var input = new Input();
        var shortcuts = ShortcutMap.CreateDefault();

        input.HandleInput(KeyParser.Parse("a"), shortcuts);
        input.HandleInput(KeyParser.Parse("b"), shortcuts);
        input.HandleInput(KeyParser.Parse("\u001b[H"), shortcuts); // Home
        input.HandleInput(KeyParser.Parse("\u001b[3~"), shortcuts); // Delete key

        Assert.Equal("b", input.Value);
        Assert.Equal(0, input.CursorIndex);
    }

    [Fact]
    public void Render_ShowsPlaceholder_WhenValueIsEmpty()
    {
        var input = new Input(placeholder: "type here...");
        var lines = input.Render(new RenderContext(30));

        Assert.Single(lines);
        Assert.Contains("type here...", lines[0]);
    }

    [Fact]
    public void Render_ShowsCursor_WhenFocused()
    {
        var input = new Input { IsFocused = true };
        var shortcuts = ShortcutMap.CreateDefault();
        input.HandleInput(KeyParser.Parse("h"), shortcuts);
        input.HandleInput(KeyParser.Parse("i"), shortcuts);

        var lines = input.Render(new RenderContext(20));

        Assert.Contains("|", lines[0]);
    }

    [Fact]
    public void HandleInput_ReturnsFalse_ForUnhandledKey()
    {
        var input = new Input();
        var shortcuts = ShortcutMap.CreateDefault();

        // F1 key (not bound)
        var result = input.HandleInput(
            new KeyEvent(KeyKind.PageUp, KeyModifiers.None, null, "\u001b[5~"), shortcuts);

        Assert.False(result);
    }
}
