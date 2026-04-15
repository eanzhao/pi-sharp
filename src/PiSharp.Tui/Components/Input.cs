namespace PiSharp.Tui;

public sealed class Input : Component, IInputComponent, IFocusableComponent
{
    private string _value = string.Empty;
    private int _cursorIndex;

    public Input(string prompt = "> ", string? placeholder = null)
    {
        Prompt = prompt;
        Placeholder = placeholder;
    }

    public event Action<string>? Submitted;

    public string Prompt { get; set; }

    public string? Placeholder { get; set; }

    public bool IsFocused { get; set; }

    public string Value
    {
        get => _value;
        set
        {
            _value = value ?? string.Empty;
            _cursorIndex = Math.Min(_cursorIndex, _value.Length);
            RaiseInvalidated();
        }
    }

    public int CursorIndex => _cursorIndex;

    public bool HandleInput(KeyEvent keyEvent, ShortcutMap shortcuts)
    {
        if (shortcuts.Matches(keyEvent, "input.submit"))
        {
            Submitted?.Invoke(_value);
            return true;
        }

        if (shortcuts.Matches(keyEvent, "input.cursor-left"))
        {
            _cursorIndex = Math.Max(0, _cursorIndex - 1);
            RaiseInvalidated();
            return true;
        }

        if (shortcuts.Matches(keyEvent, "input.cursor-right"))
        {
            _cursorIndex = Math.Min(_value.Length, _cursorIndex + 1);
            RaiseInvalidated();
            return true;
        }

        if (shortcuts.Matches(keyEvent, "input.home"))
        {
            _cursorIndex = 0;
            RaiseInvalidated();
            return true;
        }

        if (shortcuts.Matches(keyEvent, "input.end"))
        {
            _cursorIndex = _value.Length;
            RaiseInvalidated();
            return true;
        }

        if (shortcuts.Matches(keyEvent, "input.backspace"))
        {
            if (_cursorIndex == 0)
            {
                return true;
            }

            _value = _value.Remove(_cursorIndex - 1, 1);
            _cursorIndex--;
            RaiseInvalidated();
            return true;
        }

        if (shortcuts.Matches(keyEvent, "input.delete"))
        {
            if (_cursorIndex >= _value.Length)
            {
                return true;
            }

            _value = _value.Remove(_cursorIndex, 1);
            RaiseInvalidated();
            return true;
        }

        if (shortcuts.Matches(keyEvent, "input.delete-to-start"))
        {
            _value = _value[_cursorIndex..];
            _cursorIndex = 0;
            RaiseInvalidated();
            return true;
        }

        if (shortcuts.Matches(keyEvent, "input.delete-to-end"))
        {
            _value = _value[.._cursorIndex];
            RaiseInvalidated();
            return true;
        }

        if (keyEvent.Kind == KeyKind.Character && keyEvent.Character is not null && keyEvent.Modifiers is KeyModifiers.None or KeyModifiers.Shift)
        {
            _value = _value.Insert(_cursorIndex, keyEvent.Character.Value.ToString());
            _cursorIndex++;
            RaiseInvalidated();
            return true;
        }

        return false;
    }

    public override IReadOnlyList<string> Render(RenderContext context)
    {
        var width = context.Width;
        var contentWidth = Math.Max(1, width - Prompt.Length);
        var displayValue = string.IsNullOrEmpty(_value) && !string.IsNullOrEmpty(Placeholder)
            ? Placeholder!
            : _value;

        var cursorText = BuildDisplayValue(displayValue, contentWidth);
        return [$"{Prompt}{cursorText}"];
    }

    private string BuildDisplayValue(string displayValue, int width)
    {
        var raw = IsFocused
            ? displayValue.Insert(Math.Min(_cursorIndex, displayValue.Length), "|")
            : displayValue;

        if (raw.Length <= width)
        {
            return raw.PadRight(width);
        }

        var cursorOffset = IsFocused ? Math.Min(_cursorIndex, displayValue.Length) + 1 : Math.Min(_cursorIndex, displayValue.Length);
        var windowStart = Math.Max(0, cursorOffset - width);
        return raw.Substring(windowStart, width);
    }
}
