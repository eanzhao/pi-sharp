namespace PiSharp.Tui;

public sealed class SelectList : Component, IInputComponent, IFocusableComponent
{
    private IReadOnlyList<string> _items = Array.Empty<string>();
    private int _selectedIndex;

    public event Action<int, string>? Submitted;

    public bool IsFocused { get; set; }

    public IReadOnlyList<string> Items
    {
        get => _items;
        set
        {
            _items = value ?? Array.Empty<string>();
            _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, _items.Count - 1));
            RaiseInvalidated();
        }
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            if (_items.Count == 0)
            {
                return;
            }

            _selectedIndex = Math.Clamp(value, 0, _items.Count - 1);
            RaiseInvalidated();
        }
    }

    public string? SelectedItem => _items.Count > 0 ? _items[_selectedIndex] : null;

    public bool HandleInput(KeyEvent keyEvent, ShortcutMap shortcuts)
    {
        if (_items.Count == 0)
        {
            return false;
        }

        if (keyEvent.Kind == KeyKind.UpArrow)
        {
            SelectedIndex = Math.Max(0, _selectedIndex - 1);
            return true;
        }

        if (keyEvent.Kind == KeyKind.DownArrow)
        {
            SelectedIndex = Math.Min(_items.Count - 1, _selectedIndex + 1);
            return true;
        }

        if (keyEvent.Kind == KeyKind.Enter)
        {
            Submitted?.Invoke(_selectedIndex, _items[_selectedIndex]);
            return true;
        }

        return false;
    }

    public override IReadOnlyList<string> Render(RenderContext context)
    {
        var lines = new List<string>();
        for (var i = 0; i < _items.Count; i++)
        {
            var marker = IsFocused && i == _selectedIndex ? "> " : "  ";
            lines.Add($"{marker}{_items[i]}");
        }

        return lines.Count > 0 ? lines : [string.Empty];
    }
}
