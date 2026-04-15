namespace PiSharp.Tui.Tests;

public sealed class TuiApplicationTests
{
    [Fact]
    public async Task RenderAsync_WritesDiffToTerminal()
    {
        var terminal = new FakeTerminal(columns: 20, rows: 8);
        var app = new TuiApplication(terminal);
        var text = new Text("hello");
        app.AddChild(text);

        await app.RenderAsync(forceFullRedraw: true);

        text.Value = "world";
        await app.RenderAsync();

        Assert.Equal(2, terminal.Writes.Count);
        Assert.Contains("hello", terminal.Writes[0]);
        Assert.Contains($"{Ansi.MoveCursor(0, 0)}world", terminal.Writes[1]);
    }

    [Fact]
    public async Task HandleInputAsync_RendersFocusedInputComponent()
    {
        var terminal = new FakeTerminal(columns: 20, rows: 8);
        var app = new TuiApplication(terminal);
        var input = new Input();
        app.AddChild(input);
        app.SetFocus(input);

        var handled = await app.HandleInputAsync("a");

        Assert.True(handled);
        Assert.Equal("a", input.Value);
        Assert.NotEmpty(terminal.Writes);
    }
}
