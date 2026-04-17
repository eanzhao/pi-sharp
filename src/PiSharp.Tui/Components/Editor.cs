namespace PiSharp.Tui;

public sealed class Editor : Component, IInputComponent, IFocusableComponent
{
    private readonly List<string> _lines = new() { string.Empty };
    private readonly Stack<EditorSnapshot> _undoStack = new();
    private readonly Stack<EditorSnapshot> _redoStack = new();
    private string _killRing = string.Empty;

    public int CursorRow { get; private set; }
    public int CursorCol { get; private set; }
    public bool IsFocused { get; set; }

    public IReadOnlyList<string> Lines => _lines;

    public string Value
    {
        get => string.Join('\n', _lines);
        set
        {
            _lines.Clear();
            var source = value ?? string.Empty;
            foreach (var line in source.Replace("\r\n", "\n").Split('\n'))
            {
                _lines.Add(line);
            }

            if (_lines.Count == 0)
            {
                _lines.Add(string.Empty);
            }

            CursorRow = 0;
            CursorCol = 0;
            _undoStack.Clear();
            _redoStack.Clear();
            RaiseInvalidated();
        }
    }

    public bool HandleInput(KeyEvent keyEvent, ShortcutMap shortcuts)
    {
        switch (keyEvent.Kind)
        {
            case KeyKind.UpArrow:
                CursorRow = Math.Max(0, CursorRow - 1);
                CursorCol = Math.Min(CursorCol, _lines[CursorRow].Length);
                RaiseInvalidated();
                return true;
            case KeyKind.DownArrow:
                CursorRow = Math.Min(_lines.Count - 1, CursorRow + 1);
                CursorCol = Math.Min(CursorCol, _lines[CursorRow].Length);
                RaiseInvalidated();
                return true;
            case KeyKind.LeftArrow:
                if (CursorCol > 0) CursorCol--;
                else if (CursorRow > 0) { CursorRow--; CursorCol = _lines[CursorRow].Length; }
                RaiseInvalidated();
                return true;
            case KeyKind.RightArrow:
                if (CursorCol < _lines[CursorRow].Length) CursorCol++;
                else if (CursorRow < _lines.Count - 1) { CursorRow++; CursorCol = 0; }
                RaiseInvalidated();
                return true;
            case KeyKind.Home:
                CursorCol = 0;
                RaiseInvalidated();
                return true;
            case KeyKind.End:
                CursorCol = _lines[CursorRow].Length;
                RaiseInvalidated();
                return true;
            case KeyKind.Enter:
                Snapshot();
                var rest = _lines[CursorRow][CursorCol..];
                _lines[CursorRow] = _lines[CursorRow][..CursorCol];
                _lines.Insert(CursorRow + 1, rest);
                CursorRow++;
                CursorCol = 0;
                RaiseInvalidated();
                return true;
            case KeyKind.Backspace:
                if (CursorCol > 0)
                {
                    Snapshot();
                    _lines[CursorRow] = _lines[CursorRow].Remove(CursorCol - 1, 1);
                    CursorCol--;
                }
                else if (CursorRow > 0)
                {
                    Snapshot();
                    var prev = _lines[CursorRow - 1];
                    CursorCol = prev.Length;
                    _lines[CursorRow - 1] = prev + _lines[CursorRow];
                    _lines.RemoveAt(CursorRow);
                    CursorRow--;
                }

                RaiseInvalidated();
                return true;
            case KeyKind.Delete:
                if (CursorCol < _lines[CursorRow].Length)
                {
                    Snapshot();
                    _lines[CursorRow] = _lines[CursorRow].Remove(CursorCol, 1);
                    RaiseInvalidated();
                }

                return true;
            case KeyKind.Character when keyEvent.Character is not null:
                if (keyEvent.Modifiers.HasFlag(KeyModifiers.Control))
                {
                    return HandleControl(keyEvent.Character.Value);
                }

                Snapshot();
                _lines[CursorRow] = _lines[CursorRow].Insert(CursorCol, keyEvent.Character.Value.ToString());
                CursorCol++;
                RaiseInvalidated();
                return true;
        }

        return false;
    }

    public override IReadOnlyList<string> Render(RenderContext context)
    {
        var width = context.Width;
        var rendered = new List<string>(_lines.Count);
        for (var i = 0; i < _lines.Count; i++)
        {
            var display = _lines[i];
            if (IsFocused && i == CursorRow)
            {
                var pos = Math.Min(CursorCol, display.Length);
                display = display.Insert(pos, "│");
            }

            rendered.Add(AnsiString.Fit(display, width));
        }

        return rendered;
    }

    private bool HandleControl(char ch)
    {
        var upper = char.ToUpperInvariant(ch);
        switch (upper)
        {
            case 'K':
                Snapshot();
                _killRing = _lines[CursorRow][CursorCol..];
                _lines[CursorRow] = _lines[CursorRow][..CursorCol];
                RaiseInvalidated();
                return true;
            case 'Y':
                if (!string.IsNullOrEmpty(_killRing))
                {
                    Snapshot();
                    _lines[CursorRow] = _lines[CursorRow].Insert(CursorCol, _killRing);
                    CursorCol += _killRing.Length;
                    RaiseInvalidated();
                }

                return true;
            case 'Z':
                Undo();
                return true;
            default:
                return false;
        }
    }

    private void Snapshot()
    {
        _undoStack.Push(new EditorSnapshot(_lines.ToArray(), CursorRow, CursorCol));
        _redoStack.Clear();
    }

    public bool Undo()
    {
        if (_undoStack.Count == 0) return false;
        _redoStack.Push(new EditorSnapshot(_lines.ToArray(), CursorRow, CursorCol));
        var snap = _undoStack.Pop();
        _lines.Clear();
        _lines.AddRange(snap.Lines);
        CursorRow = snap.Row;
        CursorCol = snap.Col;
        RaiseInvalidated();
        return true;
    }

    public bool Redo()
    {
        if (_redoStack.Count == 0) return false;
        _undoStack.Push(new EditorSnapshot(_lines.ToArray(), CursorRow, CursorCol));
        var snap = _redoStack.Pop();
        _lines.Clear();
        _lines.AddRange(snap.Lines);
        CursorRow = snap.Row;
        CursorCol = snap.Col;
        RaiseInvalidated();
        return true;
    }

    private sealed record EditorSnapshot(string[] Lines, int Row, int Col);
}
