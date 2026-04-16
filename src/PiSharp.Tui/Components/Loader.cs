namespace PiSharp.Tui;

public sealed class Loader : Component
{
    private static readonly string[] SpinnerFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

    private int _frameIndex;

    public string Label { get; set; } = "Loading...";

    public bool IsActive { get; set; } = true;

    public void Tick()
    {
        if (!IsActive)
        {
            return;
        }

        _frameIndex = (_frameIndex + 1) % SpinnerFrames.Length;
        RaiseInvalidated();
    }

    public override IReadOnlyList<string> Render(RenderContext context)
    {
        if (!IsActive)
        {
            return [string.Empty];
        }

        var spinner = SpinnerFrames[_frameIndex];
        return [$"{spinner} {Label}"];
    }
}
