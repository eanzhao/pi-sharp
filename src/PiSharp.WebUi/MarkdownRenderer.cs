using Markdig;
using Microsoft.AspNetCore.Components;

namespace PiSharp.WebUi;

internal static class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .DisableHtml()
        .Build();

    public static MarkupString ToMarkupString(string? markdown) =>
        new(Markdown.ToHtml(markdown ?? string.Empty, Pipeline));
}
