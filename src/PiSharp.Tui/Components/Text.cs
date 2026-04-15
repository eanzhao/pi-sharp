namespace PiSharp.Tui;

public sealed class Text : Component
{
    private string _value;

    public Text(string value = "", int paddingX = 0, int paddingY = 0)
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
        var lines = TextLayout.Wrap(_value, contentWidth);
        var result = new List<string>();
        var empty = new string(' ', width);

        for (var index = 0; index < PaddingY; index++)
        {
            result.Add(empty);
        }

        foreach (var line in lines)
        {
            var paddedContent = TextLayout.PadToWidth(line, contentWidth);
            result.Add($"{new string(' ', PaddingX)}{paddedContent}{new string(' ', PaddingX)}");
        }

        for (var index = 0; index < PaddingY; index++)
        {
            result.Add(empty);
        }

        return result;
    }
}
