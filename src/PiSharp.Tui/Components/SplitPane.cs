namespace PiSharp.Tui;

public enum SplitOrientation
{
    Horizontal,
    Vertical,
}

public sealed class SplitPane : Component
{
    private IComponent? _first;
    private IComponent? _second;
    private double _ratio = 0.5;

    public SplitOrientation Orientation { get; set; } = SplitOrientation.Horizontal;

    public double Ratio
    {
        get => _ratio;
        set
        {
            _ratio = Math.Clamp(value, 0.05, 0.95);
            RaiseInvalidated();
        }
    }

    public IComponent? First
    {
        get => _first;
        set
        {
            _first = value;
            RaiseInvalidated();
        }
    }

    public IComponent? Second
    {
        get => _second;
        set
        {
            _second = value;
            RaiseInvalidated();
        }
    }

    public override IReadOnlyList<string> Render(RenderContext context)
    {
        if (_first is null && _second is null)
        {
            return [string.Empty];
        }

        if (Orientation == SplitOrientation.Horizontal)
        {
            var leftWidth = Math.Max(1, (int)(context.Width * _ratio));
            var rightWidth = Math.Max(1, context.Width - leftWidth);

            var left = _first?.Render(new RenderContext(leftWidth, context.Height)) ?? Array.Empty<string>();
            var right = _second?.Render(new RenderContext(rightWidth, context.Height)) ?? Array.Empty<string>();

            var rows = Math.Max(left.Count, right.Count);
            var result = new List<string>(rows);
            for (var i = 0; i < rows; i++)
            {
                var l = AnsiString.Fit(i < left.Count ? left[i] : string.Empty, leftWidth);
                var r = AnsiString.Fit(i < right.Count ? right[i] : string.Empty, rightWidth);
                result.Add(l + r);
            }

            return result;
        }

        var topHeight = Math.Max(1, (int)(context.Height * _ratio));
        var bottomHeight = Math.Max(1, context.Height - topHeight);

        var top = _first?.Render(new RenderContext(context.Width, topHeight)) ?? Array.Empty<string>();
        var bottom = _second?.Render(new RenderContext(context.Width, bottomHeight)) ?? Array.Empty<string>();

        var combined = new List<string>(topHeight + bottomHeight);
        combined.AddRange(top.Take(topHeight));
        while (combined.Count < topHeight)
        {
            combined.Add(string.Empty);
        }

        combined.AddRange(bottom.Take(bottomHeight));
        return combined;
    }
}
