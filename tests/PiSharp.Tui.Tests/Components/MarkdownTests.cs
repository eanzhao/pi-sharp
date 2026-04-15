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

        var lines = markdown.Render(new RenderContext(24));

        var rendered = string.Join(Environment.NewLine, lines);

        Assert.Contains("Title", lines[0]);
        Assert.Contains("- item one", rendered);
        Assert.Contains("```csharp", rendered);
        Assert.Contains("    Console.WriteLine", rendered);
    }
}
