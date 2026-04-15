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
        _globalSettings = transform(_globalSettings);
        _merged = CodingAgentSettings.Default.MergeWith(_globalSettings).MergeWith(_projectSettings);
    }

    public void UpdateProject(Func<CodingAgentSettings, CodingAgentSettings> transform)
    {
        _projectSettings = transform(_projectSettings);
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

        _merged = CodingAgentSettings.Default.MergeWith(_globalSettings).MergeWith(_projectSettings);
    }

    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (GlobalSettingsPath is not null)
        {
            await SaveToFileAsync(GlobalSettingsPath, _globalSettings, ct).ConfigureAwait(false);
        }

        if (ProjectSettingsPath is not null)
        {
            await SaveToFileAsync(ProjectSettingsPath, _projectSettings, ct).ConfigureAwait(false);
        }
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
}
