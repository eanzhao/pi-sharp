namespace PiSharp.Tui;

public sealed class Box : ContainerComponent
{
    public Box(string? title = null, int padding = 0)
    {
        Title = title;
        Padding = Math.Max(0, padding);
    }

    public string? Title { get; set; }

    public int Padding { get; }

    public override IReadOnlyList<string> Render(RenderContext context)
    {
        var width = Math.Max(4, context.Width);
        var innerWidth = width - 2;
        var contentWidth = Math.Max(1, innerWidth - (Padding * 2));
        var contentContext = new RenderContext(contentWidth, context.Height);
        var contentLines = Children.SelectMany(child => child.Render(contentContext)).ToList();

        if (contentLines.Count == 0)
        {
            contentLines.Add(string.Empty);
        }

        var result = new List<string>();
        result.Add(BuildTopBorder(innerWidth));

        for (var row = 0; row < Padding; row++)
        {
            result.Add($"|{new string(' ', innerWidth)}|");
        }

        foreach (var line in contentLines)
        {
            var content = TextLayout.PadToWidth(line, contentWidth);
            result.Add($"|{new string(' ', Padding)}{content}{new string(' ', Padding)}|");
        }

        for (var row = 0; row < Padding; row++)
        {
            result.Add($"|{new string(' ', innerWidth)}|");
        }

        result.Add($"+{new string('-', innerWidth)}+");
        return result;
    }

    private string BuildTopBorder(int innerWidth)
    {
        if (string.IsNullOrWhiteSpace(Title))
        {
            return $"+{new string('-', innerWidth)}+";
        }

        var title = $" {Title.Trim()} ";
        var available = Math.Max(0, innerWidth - title.Length);
        return $"+{title}{new string('-', available)}+";
    }
}
