namespace PiSharp.Tui;

public readonly record struct RenderContext
{
    public RenderContext(int width, int height = int.MaxValue)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
    }

    public int Width { get; }

    public int Height { get; }
}

public interface IComponent
{
    IReadOnlyList<string> Render(RenderContext context);

    void Invalidate();
}

public interface IInputComponent : IComponent
{
    bool HandleInput(KeyEvent keyEvent, ShortcutMap shortcuts);
}

public interface IFocusableComponent
{
    bool IsFocused { get; set; }
}

public abstract class Component : IComponent
{
    public event EventHandler? Invalidated;

    public abstract IReadOnlyList<string> Render(RenderContext context);

    public virtual void Invalidate()
        => Invalidated?.Invoke(this, EventArgs.Empty);

    protected void RaiseInvalidated()
        => Invalidated?.Invoke(this, EventArgs.Empty);
}

public class ContainerComponent : Component
{
    private readonly List<IComponent> _children = [];

    public IReadOnlyList<IComponent> Children => _children;

    public void AddChild(IComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);
        _children.Add(component);
        if (component is Component typed)
        {
            typed.Invalidated += HandleChildInvalidated;
        }

        RaiseInvalidated();
    }

    public bool RemoveChild(IComponent component)
    {
        ArgumentNullException.ThrowIfNull(component);

        if (!_children.Remove(component))
        {
            return false;
        }

        if (component is Component typed)
        {
            typed.Invalidated -= HandleChildInvalidated;
        }

        RaiseInvalidated();
        return true;
    }

    public void Clear()
    {
        foreach (var child in _children.OfType<Component>())
        {
            child.Invalidated -= HandleChildInvalidated;
        }

        _children.Clear();
        RaiseInvalidated();
    }

    public override IReadOnlyList<string> Render(RenderContext context)
    {
        var lines = new List<string>();
        foreach (var child in _children)
        {
            lines.AddRange(child.Render(context));
        }

        return lines;
    }

    public override void Invalidate()
    {
        foreach (var child in _children)
        {
            child.Invalidate();
        }

        base.Invalidate();
    }

    private void HandleChildInvalidated(object? sender, EventArgs e)
        => RaiseInvalidated();
}

public sealed class TuiApplication : ContainerComponent
{
    private readonly ITerminal _terminal;
    private readonly DifferentialRenderer _renderer;
    private readonly ShortcutMap _shortcuts;
    private ScreenFrame _previousFrame = ScreenFrame.Empty;
    private IComponent? _focusedComponent;

    public TuiApplication(ITerminal terminal, ShortcutMap? shortcuts = null, DifferentialRenderer? renderer = null)
    {
        _terminal = terminal ?? throw new ArgumentNullException(nameof(terminal));
        _shortcuts = shortcuts ?? ShortcutMap.CreateDefault();
        _renderer = renderer ?? new DifferentialRenderer();
    }

    public IComponent? FocusedComponent => _focusedComponent;

    public void SetFocus(IComponent? component)
    {
        if (_focusedComponent is IFocusableComponent previouslyFocused)
        {
            previouslyFocused.IsFocused = false;
        }

        _focusedComponent = component;

        if (_focusedComponent is IFocusableComponent focusable)
        {
            focusable.IsFocused = true;
        }
    }

    public async ValueTask RenderAsync(bool forceFullRedraw = false, CancellationToken cancellationToken = default)
    {
        var size = _terminal.Size;
        var lines = Render(new RenderContext(size.Columns, size.Rows))
            .Take(size.Rows)
            .Select(line => AnsiString.Fit(line, size.Columns))
            .ToArray();

        var frame = new ScreenFrame(size, lines);
        var diff = _renderer.Render(frame, _previousFrame, forceFullRedraw);

        if (diff.Length > 0)
        {
            await _terminal.WriteAsync(diff, cancellationToken);
            _previousFrame = frame;
        }
    }

    public async ValueTask<bool> HandleInputAsync(string rawInput, CancellationToken cancellationToken = default)
    {
        if (_focusedComponent is not IInputComponent inputComponent)
        {
            return false;
        }

        var handled = inputComponent.HandleInput(KeyParser.Parse(rawInput), _shortcuts);
        if (handled)
        {
            await RenderAsync(cancellationToken: cancellationToken);
        }

        return handled;
    }
}
