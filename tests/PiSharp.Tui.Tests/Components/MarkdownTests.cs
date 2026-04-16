namespace PiSharp.Tui.Tests;

public sealed class MarkdownTests
{
    [Fact]
    public void Render_FormatsHeadingsListsAndCodeBlocks()
    {
        var markdown = new Markdown(
            """
            # Title

            - item one
            - item two

            ```csharp
            Console.WriteLine("hi");
            ```
            """);

        var lines = markdown.Render(new RenderContext(40));

        var rendered = string.Join(Environment.NewLine, lines);
        var stripped = AnsiString.Strip(rendered);

        Assert.Contains("Title", stripped);
        Assert.Contains("item one", stripped);
        Assert.Contains("item two", stripped);
        Assert.Contains("Console.WriteLine", stripped);
    }
}
