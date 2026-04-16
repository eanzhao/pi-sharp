using PiSharp.Mom;

namespace PiSharp.Mom.Tests;

public sealed class SlackMrkdwnFormatterTests
{
    [Fact]
    public void Format_ConvertsCommonMarkdownPatterns()
    {
        var formatted = SlackMrkdwnFormatter.Format("**done** [docs](https://example.com)");

        Assert.Equal("*done* <https://example.com|docs>", formatted);
    }

    [Fact]
    public void Limit_TruncatesLongMessages()
    {
        var limited = SlackMrkdwnFormatter.Limit(new string('a', 20), 10);

        Assert.EndsWith("_(message truncated)_", limited);
        Assert.True(limited.Length <= 10 + "\n\n_(message truncated)_".Length);
    }
}
