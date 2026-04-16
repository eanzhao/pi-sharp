using System.Text;
using Markdig;
using Markdig.Renderers;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Microsoft.AspNetCore.Components;

namespace PiSharp.WebUi;

internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static MarkupString ToMarkupString(string? markdown)
    {
        var document = Markdown.Parse(markdown ?? string.Empty, Pipeline);
        var writer = new StringWriter();
        var renderer = new HtmlRenderer(writer);
        Pipeline.Setup(renderer);

        renderer.ObjectRenderers.ReplaceOrAdd<CodeBlockRenderer>(new SyntaxHighlightingCodeBlockRenderer());

        renderer.Render(document);
        writer.Flush();
        return new MarkupString(writer.ToString());
    }

    private sealed class SyntaxHighlightingCodeBlockRenderer : HtmlObjectRenderer<CodeBlock>
    {
        protected override void Write(HtmlRenderer renderer, CodeBlock node)
        {
            var fenced = node as FencedCodeBlock;
            var language = fenced?.Info?.Trim();

            renderer.EnsureLine();

            if (!string.IsNullOrEmpty(language))
            {
                renderer.Write("<pre><code class=\"language-")
                    .WriteEscape(language)
                    .Write("\">");
            }
            else
            {
                renderer.Write("<pre><code>");
            }

            renderer.WriteLeafRawLines(node, true, false);
            renderer.Write("</code></pre>");
            renderer.EnsureLine();
        }
    }
}
