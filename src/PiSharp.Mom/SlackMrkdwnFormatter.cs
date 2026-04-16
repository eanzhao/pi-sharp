using System.Text.RegularExpressions;

namespace PiSharp.Mom;

public static class SlackMrkdwnFormatter
{
    public static string Format(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var formatted = text;
        formatted = Regex.Replace(formatted, @"\*\*(.+?)\*\*", "*$1*");
        formatted = Regex.Replace(formatted, @"\[(.+?)\]\((https?://[^)\s]+)\)", "<$2|$1>");
        return formatted;
    }

    public static string Limit(string text, int maxCharacters = MomDefaults.MainMessageCharacterLimit)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length <= maxCharacters)
        {
            return text;
        }

        const string suffix = "\n\n_(message truncated)_";
        return text[..Math.Max(0, maxCharacters - suffix.Length)] + suffix;
    }
}
