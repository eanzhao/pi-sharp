namespace PiSharp.Tui;

public sealed record Theme(
    string Foreground,
    string Background,
    string Primary,
    string Secondary,
    string Error,
    string Warning,
    string Success,
    string Muted,
    string Border)
{
    public static Theme Dark { get; } = new(
        Foreground: "\u001b[37m",
        Background: "\u001b[40m",
        Primary: "\u001b[36m",
        Secondary: "\u001b[35m",
        Error: "\u001b[31m",
        Warning: "\u001b[33m",
        Success: "\u001b[32m",
        Muted: "\u001b[90m",
        Border: "\u001b[90m");

    public static Theme Light { get; } = new(
        Foreground: "\u001b[30m",
        Background: "\u001b[47m",
        Primary: "\u001b[34m",
        Secondary: "\u001b[35m",
        Error: "\u001b[31m",
        Warning: "\u001b[33m",
        Success: "\u001b[32m",
        Muted: "\u001b[37m",
        Border: "\u001b[37m");
}

public static class ThemeManager
{
    public static Theme Current { get; private set; } = Theme.Dark;

    public static void SetTheme(Theme theme) => Current = theme ?? throw new ArgumentNullException(nameof(theme));
}
