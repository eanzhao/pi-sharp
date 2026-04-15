using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace PiSharp.CodingAgent;

public sealed class SessionManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    private readonly List<SessionEntry> _entries = [];
    private readonly ConcurrentDictionary<string, SessionEntry> _byId = new(StringComparer.Ordinal);

    public SessionManager(string sessionDir, string cwd)
    {
        SessionDir = Path.GetFullPath(sessionDir);
        Cwd = Path.GetFullPath(cwd);
    }

    public string SessionDir { get; }
    public string Cwd { get; }
    public SessionHeader? Header { get; private set; }
    public string? SessionFile { get; private set; }
    public string? LeafId { get; private set; }
    public IReadOnlyList<SessionEntry> Entries => _entries;

    public string NewSession(
        string? parentSession = null,
        string? providerId = null,
        string? modelId = null,
        string? thinkingLevel = null,
        string? systemPrompt = null,
        IReadOnlyList<string>? toolNames = null)
    {
        var sessionId = Guid.NewGuid().ToString("N")[..16];
        var timestamp = SessionEntry.Now();

        Header = new SessionHeader
        {
            Id = sessionId,
            Timestamp = timestamp,
            Cwd = Cwd,
            ParentSession = parentSession,
            ProviderId = providerId,
            ModelId = modelId,
            ThinkingLevel = thinkingLevel,
            SystemPrompt = systemPrompt,
            ToolNames = toolNames?.ToArray() ?? Array.Empty<string>(),
        };

        _entries.Clear();
        _byId.Clear();
        LeafId = null;

        Directory.CreateDirectory(SessionDir);
        var fileName = $"{DateTimeOffset.UtcNow:yyyyMMdd_HHmmss}_{sessionId}.jsonl";
        SessionFile = Path.Combine(SessionDir, fileName);

        File.WriteAllText(SessionFile, JsonSerializer.Serialize(Header, JsonOptions) + "\n");

        return sessionId;
    }

    public async Task LoadSessionAsync(string sessionFile, CancellationToken ct = default)
    {
        SessionFile = Path.GetFullPath(sessionFile);
        _entries.Clear();
        _byId.Clear();
        LeafId = null;

        var lines = await File.ReadAllLinesAsync(SessionFile, ct).ConfigureAwait(false);
        if (lines.Length == 0)
        {
            throw new InvalidOperationException("Session file is empty.");
        }

        Header = JsonSerializer.Deserialize<SessionHeader>(lines[0], JsonOptions)
            ?? throw new InvalidOperationException("Failed to deserialize session header.");

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line))
            {
                continue;
            }

            var entry = JsonSerializer.Deserialize<SessionEntry>(line, JsonOptions);
            if (entry is null)
            {
                continue;
            }

            _entries.Add(entry);
            _byId[entry.Id] = entry;
            LeafId = entry.Id;
        }
    }

    public void AppendEntry(SessionEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrEmpty(entry.ParentId) && LeafId is not null)
        {
            entry = entry with { ParentId = LeafId };
        }

        _entries.Add(entry);
        _byId[entry.Id] = entry;
        LeafId = entry.Id;

        if (SessionFile is not null)
        {
            File.AppendAllText(SessionFile, JsonSerializer.Serialize<SessionEntry>(entry, JsonOptions) + "\n");
        }
    }

    public void UpdateHeader(Func<SessionHeader, SessionHeader> transform)
    {
        ArgumentNullException.ThrowIfNull(transform);

        if (Header is null)
        {
            throw new InvalidOperationException("Session header is not loaded.");
        }

        Header = transform(Header);
        RewriteFile();
    }

    public SessionEntry? GetEntry(string id) =>
        _byId.TryGetValue(id, out var entry) ? entry : null;

    public IReadOnlyList<SessionEntry> GetBranch(string? leafId = null)
    {
        leafId ??= LeafId;
        if (leafId is null)
        {
            return Array.Empty<SessionEntry>();
        }

        var branch = new List<SessionEntry>();
        var currentId = leafId;

        while (currentId is not null && _byId.TryGetValue(currentId, out var entry))
        {
            branch.Add(entry);
            currentId = entry.ParentId;
        }

        branch.Reverse();
        return branch;
    }

    public void SetLeaf(string entryId)
    {
        if (!_byId.ContainsKey(entryId))
        {
            throw new KeyNotFoundException($"Entry '{entryId}' not found.");
        }

        LeafId = entryId;
    }

    public string ResolveSessionFile(string selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        if (string.Equals(selector, "latest", StringComparison.OrdinalIgnoreCase))
        {
            return FindLatestSessionFile()
                ?? throw new FileNotFoundException($"No sessions were found in {SessionDir}.");
        }

        if (File.Exists(selector))
        {
            return Path.GetFullPath(selector);
        }

        var rootedCandidate = Path.GetFullPath(selector);
        if (File.Exists(rootedCandidate))
        {
            return rootedCandidate;
        }

        if (!Directory.Exists(SessionDir))
        {
            throw new FileNotFoundException($"Session directory not found: {SessionDir}");
        }

        var matches = Directory
            .EnumerateFiles(SessionDir, "*.jsonl", SearchOption.TopDirectoryOnly)
            .Select(file => (File: file, Header: TryReadHeader(file)))
            .Where(static candidate => candidate.Header is not null)
            .Where(candidate =>
                string.Equals(candidate.Header!.Id, selector, StringComparison.OrdinalIgnoreCase) ||
                candidate.Header.Id.StartsWith(selector, StringComparison.OrdinalIgnoreCase) ||
                Path.GetFileNameWithoutExtension(candidate.File).Contains(selector, StringComparison.OrdinalIgnoreCase))
            .Select(static candidate => candidate.File)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return matches.Length switch
        {
            0 => throw new FileNotFoundException($"Session '{selector}' was not found in {SessionDir}."),
            1 => matches[0],
            _ => throw new InvalidOperationException($"Session selector '{selector}' is ambiguous."),
        };
    }

    public string? FindLatestSessionFile()
    {
        if (!Directory.Exists(SessionDir))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(SessionDir, "*.jsonl", SearchOption.TopDirectoryOnly)
            .OrderByDescending(static file => Path.GetFileName(file), StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public SessionContext BuildContext(string? leafId = null)
    {
        var branch = GetBranch(leafId);
        var messages = new List<ChatMessage>();
        var thinkingLevel = Header?.ThinkingLevel;
        var providerId = Header?.ProviderId;
        var modelId = Header?.ModelId;
        var systemPrompt = Header?.SystemPrompt;
        var toolNames = Header?.ToolNames?.ToArray() ?? Array.Empty<string>();

        CompactionEntry? lastCompaction = null;
        var firstKeptIndex = 0;

        for (var i = branch.Count - 1; i >= 0; i--)
        {
            if (branch[i] is CompactionEntry compaction)
            {
                lastCompaction = compaction;
                firstKeptIndex = i + 1;
                break;
            }
        }

        if (lastCompaction is not null)
        {
            messages.Add(new ChatMessage(ChatRole.Assistant, lastCompaction.Summary));
        }

        for (var i = firstKeptIndex; i < branch.Count; i++)
        {
            switch (branch[i])
            {
                case SessionMessageEntry msg:
                    messages.Add(msg.ToChatMessage());
                    break;
                case ThinkingLevelChangeEntry tlc:
                    thinkingLevel = tlc.Level;
                    break;
                case ModelChangeEntry mc:
                    providerId = mc.ProviderId;
                    modelId = mc.ModelId;
                    break;
            }
        }

        return new SessionContext(messages, thinkingLevel, providerId, modelId, systemPrompt, toolNames);
    }

    private static SessionHeader? TryReadHeader(string sessionFile)
    {
        try
        {
            using var reader = File.OpenText(sessionFile);
            var line = reader.ReadLine();
            return string.IsNullOrWhiteSpace(line)
                ? null
                : JsonSerializer.Deserialize<SessionHeader>(line, JsonOptions);
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void RewriteFile()
    {
        if (SessionFile is null || Header is null)
        {
            return;
        }

        var lines = new List<string> { JsonSerializer.Serialize(Header, JsonOptions) };
        lines.AddRange(_entries.Select(static entry => JsonSerializer.Serialize<SessionEntry>(entry, JsonOptions)));
        File.WriteAllLines(SessionFile, lines);
    }
}
