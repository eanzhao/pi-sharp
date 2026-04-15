namespace PiSharp.Tui;

public sealed record Shortcut(KeyKind Kind, KeyModifiers Modifiers = KeyModifiers.None, char? Character = null)
{
    public bool Matches(KeyEvent keyEvent)
    {
        if (Kind != keyEvent.Kind || Modifiers != keyEvent.Modifiers)
        {
            return false;
        }

        if (Kind != KeyKind.Character)
        {
            return true;
        }

        return Character is not null
            && keyEvent.Character is not null
            && char.ToUpperInvariant(Character.Value) == char.ToUpperInvariant(keyEvent.Character.Value);
    }

    public static Shortcut Parse(string value)
    {
        ArgumentException.ThrowIfNullOrEmpty(value);

        var segments = value.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var modifiers = KeyModifiers.None;
        var keyToken = segments[^1];

        foreach (var segment in segments[..^1])
        {
            modifiers |= segment.ToLowerInvariant() switch
            {
                "ctrl" or "control" => KeyModifiers.Control,
                "shift" => KeyModifiers.Shift,
                "alt" => KeyModifiers.Alt,
                "meta" or "super" or "cmd" => KeyModifiers.Meta,
                _ => throw new ArgumentException($"Unsupported shortcut modifier '{segment}'.", nameof(value)),
            };
        }

        return keyToken.ToLowerInvariant() switch
        {
            "enter" or "return" => new Shortcut(KeyKind.Enter, modifiers),
            "escape" or "esc" => new Shortcut(KeyKind.Escape, modifiers),
            "tab" => new Shortcut(KeyKind.Tab, modifiers),
            "backspace" => new Shortcut(KeyKind.Backspace, modifiers),
            "delete" or "del" => new Shortcut(KeyKind.Delete, modifiers),
            "left" => new Shortcut(KeyKind.LeftArrow, modifiers),
            "right" => new Shortcut(KeyKind.RightArrow, modifiers),
            "up" => new Shortcut(KeyKind.UpArrow, modifiers),
            "down" => new Shortcut(KeyKind.DownArrow, modifiers),
            "home" => new Shortcut(KeyKind.Home, modifiers),
            "end" => new Shortcut(KeyKind.End, modifiers),
            "pageup" => new Shortcut(KeyKind.PageUp, modifiers),
            "pagedown" => new Shortcut(KeyKind.PageDown, modifiers),
            { Length: 1 } => new Shortcut(KeyKind.Character, modifiers, keyToken[0]),
            _ => throw new ArgumentException($"Unsupported shortcut key '{keyToken}'.", nameof(value)),
        };
    }
}

public sealed class ShortcutMap
{
    private readonly Dictionary<string, List<Shortcut>> _bindings = new(StringComparer.OrdinalIgnoreCase);

    public static ShortcutMap CreateDefault()
    {
        var map = new ShortcutMap();
        map.Register("input.submit", "Enter");
        map.Register("input.cursor-left", "Left", "Ctrl+B");
        map.Register("input.cursor-right", "Right", "Ctrl+F");
        map.Register("input.home", "Home", "Ctrl+A");
        map.Register("input.end", "End", "Ctrl+E");
        map.Register("input.backspace", "Backspace");
        map.Register("input.delete", "Delete", "Ctrl+D");
        map.Register("input.delete-to-start", "Ctrl+U");
        map.Register("input.delete-to-end", "Ctrl+K");
        return map;
    }

    public void Register(string action, params string[] shortcuts)
    {
        ArgumentException.ThrowIfNullOrEmpty(action);

        if (!_bindings.TryGetValue(action, out var items))
        {
            items = [];
            _bindings[action] = items;
        }

        foreach (var shortcut in shortcuts)
        {
            items.Add(Shortcut.Parse(shortcut));
        }
    }

    public bool Matches(KeyEvent keyEvent, string action)
        => _bindings.TryGetValue(action, out var shortcuts) && shortcuts.Any(shortcut => shortcut.Matches(keyEvent));

    public IReadOnlyList<Shortcut> GetShortcuts(string action)
        => _bindings.TryGetValue(action, out var shortcuts) ? shortcuts.AsReadOnly() : Array.Empty<Shortcut>();
}
