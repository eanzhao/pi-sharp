using System.Text.RegularExpressions;

namespace PiSharp.Tui;

[Flags]
public enum KeyModifiers
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Control = 4,
    Meta = 8,
}

public enum KeyKind
{
    Unknown = 0,
    Character,
    Enter,
    Escape,
    Tab,
    Backspace,
    Delete,
    UpArrow,
    DownArrow,
    LeftArrow,
    RightArrow,
    Home,
    End,
    PageUp,
    PageDown,
}

public readonly record struct KeyEvent(KeyKind Kind, KeyModifiers Modifiers, char? Character, string Raw)
{
    public static KeyEvent FromCharacter(char character, KeyModifiers modifiers = KeyModifiers.None, string? raw = null)
        => new(KeyKind.Character, modifiers, character, raw ?? character.ToString());
}

public static partial class KeyParser
{
    [GeneratedRegex(@"^\u001b\[(?:(?<code>\d+)(?:;(?<modifier>\d+))?)?(?<suffix>[~A-Za-z])$")]
    private static partial Regex CsiRegex();

    public static KeyEvent Parse(string raw)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return new KeyEvent(KeyKind.Unknown, KeyModifiers.None, null, string.Empty);
        }

        if (raw == "\u001b")
        {
            return new KeyEvent(KeyKind.Escape, KeyModifiers.None, null, raw);
        }

        if (raw.StartsWith("\u001b[", StringComparison.Ordinal))
        {
            return ParseCsi(raw);
        }

        if (raw[0] == '\u001b')
        {
            var inner = Parse(raw[1..]);
            return inner with { Modifiers = inner.Modifiers | KeyModifiers.Alt, Raw = raw };
        }

        if (raw.Length == 1)
        {
            return ParseSingleCharacter(raw[0], raw);
        }

        return new KeyEvent(KeyKind.Unknown, KeyModifiers.None, null, raw);
    }

    private static KeyEvent ParseSingleCharacter(char character, string raw)
    {
        return character switch
        {
            '\r' or '\n' => new KeyEvent(KeyKind.Enter, KeyModifiers.None, null, raw),
            '\t' => new KeyEvent(KeyKind.Tab, KeyModifiers.None, null, raw),
            '\b' or '\u007f' => new KeyEvent(KeyKind.Backspace, KeyModifiers.None, null, raw),
            >= '\u0001' and <= '\u001a' => KeyEvent.FromCharacter((char)('A' + character - 1), KeyModifiers.Control, raw),
            _ when !char.IsControl(character) => KeyEvent.FromCharacter(character, KeyModifiers.None, raw),
            _ => new KeyEvent(KeyKind.Unknown, KeyModifiers.None, null, raw),
        };
    }

    private static KeyEvent ParseCsi(string raw)
    {
        var match = CsiRegex().Match(raw);
        if (!match.Success)
        {
            return new KeyEvent(KeyKind.Unknown, KeyModifiers.None, null, raw);
        }

        var code = match.Groups["code"].Success ? int.Parse(match.Groups["code"].Value) : 1;
        var modifiers = match.Groups["modifier"].Success
            ? ParseModifiers(int.Parse(match.Groups["modifier"].Value))
            : KeyModifiers.None;
        var suffix = match.Groups["suffix"].Value[0];

        return suffix switch
        {
            'A' => new KeyEvent(KeyKind.UpArrow, modifiers, null, raw),
            'B' => new KeyEvent(KeyKind.DownArrow, modifiers, null, raw),
            'C' => new KeyEvent(KeyKind.RightArrow, modifiers, null, raw),
            'D' => new KeyEvent(KeyKind.LeftArrow, modifiers, null, raw),
            'H' => new KeyEvent(KeyKind.Home, modifiers, null, raw),
            'F' => new KeyEvent(KeyKind.End, modifiers, null, raw),
            '~' => ParseTildeSequence(code, modifiers, raw),
            _ => new KeyEvent(KeyKind.Unknown, modifiers, null, raw),
        };
    }

    private static KeyEvent ParseTildeSequence(int code, KeyModifiers modifiers, string raw)
    {
        var kind = code switch
        {
            1 or 7 => KeyKind.Home,
            3 => KeyKind.Delete,
            4 or 8 => KeyKind.End,
            5 => KeyKind.PageUp,
            6 => KeyKind.PageDown,
            _ => KeyKind.Unknown,
        };

        return new KeyEvent(kind, modifiers, null, raw);
    }

    private static KeyModifiers ParseModifiers(int value)
    {
        var bitset = Math.Max(0, value - 1);
        var modifiers = KeyModifiers.None;

        if ((bitset & 1) != 0)
        {
            modifiers |= KeyModifiers.Shift;
        }

        if ((bitset & 2) != 0)
        {
            modifiers |= KeyModifiers.Alt;
        }

        if ((bitset & 4) != 0)
        {
            modifiers |= KeyModifiers.Control;
        }

        if ((bitset & 8) != 0)
        {
            modifiers |= KeyModifiers.Meta;
        }

        return modifiers;
    }
}
