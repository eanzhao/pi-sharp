using System.Text.Json;

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
        RecordModifiedFields(GetModifiedFieldNames(previous, _globalSettings), _modifiedGlobalFields);
        RefreshMergedSettings();
    }

    public void UpdateProject(Func<CodingAgentSettings, CodingAgentSettings> transform)
    {
        var previous = _projectSettings;
        _projectSettings = transform(_projectSettings);
        RecordModifiedFields(GetModifiedFieldNames(previous, _projectSettings), _modifiedProjectFields);
        RefreshMergedSettings();
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
        RefreshMergedSettings();
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (GlobalSettingsPath is not null && _modifiedGlobalFields.Count > 0)
        {
            var existingGlobalSettings = await LoadFromFileAsync(GlobalSettingsPath, ct).ConfigureAwait(false);
            _globalSettings = MergeModifiedFields(existingGlobalSettings, _globalSettings, _modifiedGlobalFields);
            await SaveToFileAsync(GlobalSettingsPath, _globalSettings, ct).ConfigureAwait(false);
            _modifiedGlobalFields.Clear();
        }

        if (ProjectSettingsPath is not null && _modifiedProjectFields.Count > 0)
        {
            var existingProjectSettings = await LoadFromFileAsync(ProjectSettingsPath, ct).ConfigureAwait(false);
            _projectSettings = MergeModifiedFields(existingProjectSettings, _projectSettings, _modifiedProjectFields);
            await SaveToFileAsync(ProjectSettingsPath, _projectSettings, ct).ConfigureAwait(false);
            _modifiedProjectFields.Clear();
        }

        RefreshMergedSettings();
    }

    private static IReadOnlyList<string> GetModifiedFieldNames(CodingAgentSettings previous, CodingAgentSettings current)
    {
        var modifiedFields = new List<string>();

        if (!Equals(previous.DefaultProvider, current.DefaultProvider)) modifiedFields.Add(nameof(CodingAgentSettings.DefaultProvider));
        if (!Equals(previous.DefaultModel, current.DefaultModel)) modifiedFields.Add(nameof(CodingAgentSettings.DefaultModel));
        if (!Equals(previous.DefaultThinkingLevel, current.DefaultThinkingLevel)) modifiedFields.Add(nameof(CodingAgentSettings.DefaultThinkingLevel));
        if (!Equals(previous.SessionDir, current.SessionDir)) modifiedFields.Add(nameof(CodingAgentSettings.SessionDir));
        if (!Equals(previous.ShellPath, current.ShellPath)) modifiedFields.Add(nameof(CodingAgentSettings.ShellPath));
        if (!Equals(previous.SteeringMode, current.SteeringMode)) modifiedFields.Add(nameof(CodingAgentSettings.SteeringMode));
        if (!Equals(previous.FollowUpMode, current.FollowUpMode)) modifiedFields.Add(nameof(CodingAgentSettings.FollowUpMode));
        if (!Equals(previous.Theme, current.Theme)) modifiedFields.Add(nameof(CodingAgentSettings.Theme));
        if (!Equals(previous.QuietStartup, current.QuietStartup)) modifiedFields.Add(nameof(CodingAgentSettings.QuietStartup));
        if (!Equals(previous.Compaction, current.Compaction)) modifiedFields.Add(nameof(CodingAgentSettings.Compaction));
        if (!Equals(previous.Retry, current.Retry)) modifiedFields.Add(nameof(CodingAgentSettings.Retry));

        return modifiedFields;
    }

    private static void RecordModifiedFields(IEnumerable<string> modifiedFields, HashSet<string> destination)
    {
        foreach (var modifiedField in modifiedFields)
        {
            destination.Add(modifiedField);
        }
    }

    private static CodingAgentSettings MergeModifiedFields(
        CodingAgentSettings existing,
        CodingAgentSettings updated,
        IReadOnlySet<string> modifiedFields)
    {
        return new CodingAgentSettings
        {
            DefaultProvider = Select(nameof(CodingAgentSettings.DefaultProvider), existing.DefaultProvider, updated.DefaultProvider),
            DefaultModel = Select(nameof(CodingAgentSettings.DefaultModel), existing.DefaultModel, updated.DefaultModel),
            DefaultThinkingLevel = Select(nameof(CodingAgentSettings.DefaultThinkingLevel), existing.DefaultThinkingLevel, updated.DefaultThinkingLevel),
            SessionDir = Select(nameof(CodingAgentSettings.SessionDir), existing.SessionDir, updated.SessionDir),
            ShellPath = Select(nameof(CodingAgentSettings.ShellPath), existing.ShellPath, updated.ShellPath),
            SteeringMode = Select(nameof(CodingAgentSettings.SteeringMode), existing.SteeringMode, updated.SteeringMode),
            FollowUpMode = Select(nameof(CodingAgentSettings.FollowUpMode), existing.FollowUpMode, updated.FollowUpMode),
            Theme = Select(nameof(CodingAgentSettings.Theme), existing.Theme, updated.Theme),
            QuietStartup = Select(nameof(CodingAgentSettings.QuietStartup), existing.QuietStartup, updated.QuietStartup),
            Compaction = Select(nameof(CodingAgentSettings.Compaction), existing.Compaction, updated.Compaction),
            Retry = Select(nameof(CodingAgentSettings.Retry), existing.Retry, updated.Retry),
        };

        T? Select<T>(string fieldName, T? existingValue, T? updatedValue) =>
            modifiedFields.Contains(fieldName) ? updatedValue : existingValue;
    }

    private static async Task SaveToFileAsync(string path, CodingAgentSettings settings, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, settings, JsonOptions, ct).ConfigureAwait(false);
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

    private void RefreshMergedSettings() =>
        _merged = CodingAgentSettings.Default.MergeWith(_globalSettings).MergeWith(_projectSettings);
}
