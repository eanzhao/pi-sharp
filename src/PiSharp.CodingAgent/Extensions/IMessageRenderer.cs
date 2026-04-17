namespace PiSharp.CodingAgent;

public interface IMessageRenderer
{
    string CustomType { get; }
    string Render(object? data);
}

public sealed class DelegateMessageRenderer : IMessageRenderer
{
    private readonly Func<object?, string> _render;

    public DelegateMessageRenderer(string customType, Func<object?, string> render)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(customType);
        ArgumentNullException.ThrowIfNull(render);
        CustomType = customType;
        _render = render;
    }

    public string CustomType { get; }

    public string Render(object? data) => _render(data);
}
