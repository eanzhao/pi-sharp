namespace PiSharp.Tui;

public sealed class Image : Component
{
    public string? Source { get; set; }

    public string Alt { get; set; } = "image";

    public override IReadOnlyList<string> Render(RenderContext context)
    {
        var label = string.IsNullOrWhiteSpace(Alt) ? "image" : Alt;
        return [$"[Image: {label}]"];
    }
}
