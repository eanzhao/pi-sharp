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
}
