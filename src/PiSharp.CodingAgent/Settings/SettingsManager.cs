using System.Text.Json;
using System.Text.Json.Nodes;

namespace PiSharp.CodingAgent;

public sealed class SettingsManager
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    private CodingAgentSettings _globalSettings;
    private CodingAgentSettings _projectSettings;
    private CodingAgentSettings _merged;
    private readonly HashSet<string> _modifiedGlobalFields = new(StringComparer.Ordinal);
    private readonly HashSet<string> _modifiedProjectFields = new(StringComparer.Ordinal);

    private SettingsManager(
        CodingAgentSettings globalSettings,
        CodingAgentSettings projectSettings,
        string? globalSettingsPath,
        string? projectSettingsPath)
    {
        _globalSettings = globalSettings;
        _projectSettings = projectSettings;
        GlobalSettingsPath = globalSettingsPath;
        ProjectSettingsPath = projectSettingsPath;
        _merged = CodingAgentSettings.Default.MergeWith(globalSettings).MergeWith(projectSettings);
    }

    public string? GlobalSettingsPath { get; }
    public string? ProjectSettingsPath { get; }

    public CodingAgentSettings Settings => _merged;
    public CodingAgentSettings GlobalSettings => _globalSettings;
    public CodingAgentSettings ProjectSettings => _projectSettings;

    public IReadOnlySet<string> ModifiedGlobalFields => _modifiedGlobalFields;
    public IReadOnlySet<string> ModifiedProjectFields => _modifiedProjectFields;

    public static SettingsManager Create(string cwd, string agentDir)
    {
        var globalPath = Path.Combine(agentDir, "settings.json");
        var projectPath = Path.Combine(cwd, ".pi-sharp", "settings.json");

        var globalSettings = LoadFromFile(globalPath);
        var projectSettings = LoadFromFile(projectPath);

        return new SettingsManager(globalSettings, projectSettings, globalPath, projectPath);
    }

    public static SettingsManager InMemory(CodingAgentSettings? settings = null) =>
        new(settings ?? new CodingAgentSettings(), new CodingAgentSettings(), null, null);

    public void UpdateGlobal(Func<CodingAgentSettings, CodingAgentSettings> transform)
    {
        var previous = _globalSettings;
        _globalSettings = transform(_globalSettings);
        TrackChanges(previous, _globalSettings, _modifiedGlobalFields);
        _merged = CodingAgentSettings.Default.MergeWith(_globalSettings).MergeWith(_projectSettings);
    }

    public void UpdateProject(Func<CodingAgentSettings, CodingAgentSettings> transform)
    {
        var previous = _projectSettings;
        _projectSettings = transform(_projectSettings);
        TrackChanges(previous, _projectSettings, _modifiedProjectFields);
        _merged = CodingAgentSettings.Default.MergeWith(_globalSettings).MergeWith(_projectSettings);
    }

    public async Task ReloadAsync(CancellationToken ct = default)
    {
        if (GlobalSettingsPath is not null)
        {
            _globalSettings = await LoadFromFileAsync(GlobalSettingsPath, ct).ConfigureAwait(false);
        }

        if (ProjectSettingsPath is not null)
        {
            _projectSettings = await LoadFromFileAsync(ProjectSettingsPath, ct).ConfigureAwait(false);
        }

        _modifiedGlobalFields.Clear();
        _modifiedProjectFields.Clear();
        _merged = CodingAgentSettings.Default.MergeWith(_globalSettings).MergeWith(_projectSettings);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (GlobalSettingsPath is not null && _modifiedGlobalFields.Count > 0)
        {
            await MergeAndSaveAsync(GlobalSettingsPath, _globalSettings, _modifiedGlobalFields, ct).ConfigureAwait(false);
            _modifiedGlobalFields.Clear();
        }

        if (ProjectSettingsPath is not null && _modifiedProjectFields.Count > 0)
        {
            await MergeAndSaveAsync(ProjectSettingsPath, _projectSettings, _modifiedProjectFields, ct).ConfigureAwait(false);
            _modifiedProjectFields.Clear();
        }
    }

    private static void TrackChanges(CodingAgentSettings previous, CodingAgentSettings current, HashSet<string> modified)
    {
        if (!Equals(previous.DefaultProvider, current.DefaultProvider)) modified.Add("defaultProvider");
        if (!Equals(previous.DefaultModel, current.DefaultModel)) modified.Add("defaultModel");
        if (!Equals(previous.DefaultThinkingLevel, current.DefaultThinkingLevel)) modified.Add("defaultThinkingLevel");
        if (!Equals(previous.SessionDir, current.SessionDir)) modified.Add("sessionDir");
        if (!Equals(previous.ShellPath, current.ShellPath)) modified.Add("shellPath");
        if (!Equals(previous.SteeringMode, current.SteeringMode)) modified.Add("steeringMode");
        if (!Equals(previous.FollowUpMode, current.FollowUpMode)) modified.Add("followUpMode");
        if (!Equals(previous.Theme, current.Theme)) modified.Add("theme");
        if (!Equals(previous.QuietStartup, current.QuietStartup)) modified.Add("quietStartup");
        if (!Equals(previous.Compaction, current.Compaction)) modified.Add("compaction");
        if (!Equals(previous.Retry, current.Retry)) modified.Add("retry");
    }

    private static async Task MergeAndSaveAsync(
        string path,
        CodingAgentSettings settings,
        IReadOnlySet<string> modifiedFields,
        CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        JsonNode existing;
        if (File.Exists(path))
        {
            try
            {
                var json = await File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
                existing = JsonNode.Parse(json) ?? new JsonObject();
            }
            catch
            {
                existing = new JsonObject();
            }
        }
        else
        {
            existing = new JsonObject();
        }

        var newNode = JsonSerializer.SerializeToNode(settings, JsonOptions);
        if (newNode is JsonObject newObj && existing is JsonObject existingObj)
        {
            foreach (var field in modifiedFields)
            {
                if (newObj.ContainsKey(field))
                {
                    existingObj[field] = newObj[field]?.DeepClone();
                }
                else
                {
                    existingObj.Remove(field);
                }
            }
        }

        var output = existing.ToJsonString(JsonOptions);
        await File.WriteAllTextAsync(path, output, ct).ConfigureAwait(false);
    }

    private static CodingAgentSettings LoadFromFile(string path)
    {
        if (!File.Exists(path))
        {
            return new CodingAgentSettings();
        }

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<CodingAgentSettings>(json, JsonOptions) ?? new CodingAgentSettings();
        }
        catch (JsonException)
        {
            return new CodingAgentSettings();
        }
    }

    private static async Task<CodingAgentSettings> LoadFromFileAsync(string path, CancellationToken ct)
    {
        if (!File.Exists(path))
        {
            return new CodingAgentSettings();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<CodingAgentSettings>(stream, JsonOptions, ct).ConfigureAwait(false)
                ?? new CodingAgentSettings();
        }
        catch (JsonException)
        {
            return new CodingAgentSettings();
        }
    }
}
