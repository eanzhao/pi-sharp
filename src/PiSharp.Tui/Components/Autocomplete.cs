namespace PiSharp.Tui;

public sealed class Autocomplete : Component, IInputComponent, IFocusableComponent
{
    private IReadOnlyList<string> _candidates = Array.Empty<string>();
    private string _query = string.Empty;
    private int _selectedIndex;
    private IReadOnlyList<string> _filtered = Array.Empty<string>();

    public event Action<string>? Selected;

    public bool IsFocused { get; set; }

    public int MaxVisible { get; set; } = 8;

    public IReadOnlyList<string> Candidates
    {
        get => _candidates;
        set
        {
            _candidates = value ?? Array.Empty<string>();
            Recompute();
        }
    }

    public string Query
    {
        get => _query;
        set
        {
            _query = value ?? string.Empty;
            Recompute();
        }
    }

    public IReadOnlyList<string> FilteredCandidates => _filtered;

    public int SelectedIndex => _selectedIndex;

    public bool HandleInput(KeyEvent keyEvent, ShortcutMap shortcuts)
    {
        if (_filtered.Count == 0)
        {
            return false;
        }

        if (keyEvent.Kind == KeyKind.UpArrow)
        {
            _selectedIndex = Math.Max(0, _selectedIndex - 1);
            RaiseInvalidated();
            return true;
        }

        if (keyEvent.Kind == KeyKind.DownArrow)
        {
            _selectedIndex = Math.Min(_filtered.Count - 1, _selectedIndex + 1);
            RaiseInvalidated();
            return true;
        }

        if (keyEvent.Kind == KeyKind.Enter)
        {
            Selected?.Invoke(_filtered[_selectedIndex]);
            return true;
        }

        return false;
    }

    public override IReadOnlyList<string> Render(RenderContext context)
    {
        var lines = new List<string> { $"> {_query}" };
        var visible = _filtered.Take(MaxVisible).ToArray();
        for (var i = 0; i < visible.Length; i++)
        {
            var marker = i == _selectedIndex && IsFocused ? "› " : "  ";
            lines.Add($"{marker}{visible[i]}");
        }

        return lines;
    }

    private void Recompute()
    {
        if (string.IsNullOrEmpty(_query))
        {
            _filtered = _candidates;
        }
        else
        {
            _filtered = _candidates
                .Select(c => (Candidate: c, Score: FuzzyScore(c, _query)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .Select(x => x.Candidate)
                .ToArray();
        }

        _selectedIndex = Math.Clamp(_selectedIndex, 0, Math.Max(0, _filtered.Count - 1));
        RaiseInvalidated();
    }

    internal static int FuzzyScore(string candidate, string query)
    {
        if (string.IsNullOrEmpty(query))
        {
            return 1;
        }

        if (candidate.StartsWith(query, StringComparison.OrdinalIgnoreCase))
        {
            return 1000 + (100 - Math.Min(candidate.Length, 100));
        }

        var score = 0;
        var queryIndex = 0;
        var lastMatch = -2;

        for (var i = 0; i < candidate.Length && queryIndex < query.Length; i++)
        {
            if (char.ToLowerInvariant(candidate[i]) == char.ToLowerInvariant(query[queryIndex]))
            {
                score += 10;
                if (i == lastMatch + 1)
                {
                    score += 15; // consecutive bonus
                }

                lastMatch = i;
                queryIndex++;
            }
        }

        return queryIndex == query.Length ? score : 0;
    }
}
