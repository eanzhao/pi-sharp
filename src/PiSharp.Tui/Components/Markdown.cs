using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace PiSharp.Tui;

public sealed class Markdown : Component
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private string _value;

    public Markdown(string value = "", int paddingX = 0, int paddingY = 0)
    {
        _value = value;
        PaddingX = Math.Max(0, paddingX);
        PaddingY = Math.Max(0, paddingY);
    }

    public int PaddingX { get; }

    public int PaddingY { get; }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value)
            {
                return;
            }

            _value = value;
            RaiseInvalidated();
        }
    }

    public override IReadOnlyList<string> Render(RenderContext context)
    {
        var width = context.Width;
        var contentWidth = Math.Max(1, width - (PaddingX * 2));
        var lines = RenderMarkdownBlocks(_value, contentWidth);
        var result = new List<string>();
        var empty = new string(' ', width);

        for (var index = 0; index < PaddingY; index++)
        {
            result.Add(empty);
        }

        foreach (var line in lines)
        {
            var padded = TextLayout.PadToWidth(line, contentWidth);
            result.Add($"{new string(' ', PaddingX)}{padded}{new string(' ', PaddingX)}");
        }

        for (var index = 0; index < PaddingY; index++)
        {
            result.Add(empty);
        }

        return result;
    }

    private static IReadOnlyList<string> RenderMarkdownBlocks(string markdown, int width)
    {
        var document = Markdig.Markdown.Parse(markdown ?? string.Empty, Pipeline);
        var result = new List<string>();

        foreach (var block in document)
        {
            switch (block)
            {
                case HeadingBlock heading:
                    var headingText = RenderInline(heading.Inline);
                    result.AddRange(TextLayout.Wrap($"\u001b[1;4m{headingText}\u001b[0m", width));
                    result.Add(string.Empty);
                    break;
                case ParagraphBlock paragraph:
                    result.AddRange(TextLayout.Wrap(RenderInline(paragraph.Inline), width));
                    result.Add(string.Empty);
                    break;
                case ListBlock list:
                    foreach (var item in list.OfType<ListItemBlock>())
                    {
                        foreach (var child in item)
                        {
                            if (child is ParagraphBlock itemParagraph)
                            {
                                var wrapped = TextLayout.Wrap(RenderInline(itemParagraph.Inline), Math.Max(1, width - 4));
                                for (var index = 0; index < wrapped.Count; index++)
                                {
                                    var prefix = index == 0 ? "  \u2022 " : "    ";
                                    result.Add(prefix + wrapped[index]);
                                }
                            }
                        }
                    }

                    result.Add(string.Empty);
                    break;
                case QuoteBlock quote:
                    foreach (var child in quote)
                    {
                        if (child is ParagraphBlock quotedParagraph)
                        {
                            foreach (var line in TextLayout.Wrap(RenderInline(quotedParagraph.Inline), Math.Max(1, width - 4)))
                            {
                                result.Add($"\u001b[90m\u2502 \u001b[0m{line}");
                            }
                        }
                    }

                    result.Add(string.Empty);
                    break;
                case FencedCodeBlock fencedCode:
                    if (!string.IsNullOrWhiteSpace(fencedCode.Info))
                    {
                        result.Add($"\u001b[90m  [{fencedCode.Info}]\u001b[0m");
                    }

                    foreach (var line in fencedCode.Lines.Lines)
                    {
                        result.Add($"  \u001b[90m{line}\u001b[0m");
                    }

                    result.Add(string.Empty);
                    break;
                case ThematicBreakBlock:
                    result.Add($"\u001b[90m{new string('\u2500', Math.Min(width, 40))}\u001b[0m");
                    result.Add(string.Empty);
                    break;
            }
        }

        if (result.Count > 0 && result[^1].Length == 0)
        {
            result.RemoveAt(result.Count - 1);
        }

        return result.Count > 0 ? result : [string.Empty];
    }

    private static string RenderInline(ContainerInline? inline)
    {
        if (inline is null)
        {
            return string.Empty;
        }

        var result = new List<string>();
        foreach (var child in inline)
        {
            switch (child)
            {
                case LiteralInline literal:
                    result.Add(literal.Content.ToString());
                    break;
                case LineBreakInline:
                    result.Add(Environment.NewLine);
                    break;
                case CodeInline code:
                    result.Add($"\u001b[36m{code.Content}\u001b[0m");
                    break;
                case EmphasisInline emphasis:
                    var style = emphasis.DelimiterCount >= 2 ? "\u001b[1m" : "\u001b[3m";
                    result.Add($"{style}{RenderInline(emphasis)}\u001b[0m");
                    break;
                case LinkInline link when !link.IsImage:
                    var linkText = RenderInline(link);
                    result.Add($"{linkText} \u001b[4;34m{link.Url}\u001b[0m");
                    break;
                case ContainerInline container:
                    result.Add(RenderInline(container));
                    break;
            }
        }

        return string.Concat(result);
    }
}
