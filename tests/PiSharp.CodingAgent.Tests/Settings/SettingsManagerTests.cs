using PiSharp.CodingAgent;

namespace PiSharp.CodingAgent.Tests;

public sealed class SettingsManagerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"pisharp_settings_{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void InMemory_ReturnsDefaultSettings()
    {
        var manager = SettingsManager.InMemory();

        Assert.NotNull(manager.Settings);
        Assert.NotNull(manager.Settings.Compaction);
        Assert.True(manager.Settings.Compaction!.Enabled);
    }

    [Fact]
    public void InMemory_AcceptsCustomSettings()
    {
        var custom = new CodingAgentSettings { DefaultModel = "gpt-4o" };
        var manager = SettingsManager.InMemory(custom);

        Assert.Equal("gpt-4o", manager.Settings.DefaultModel);
    }

    [Fact]
    public void MergeWith_OverlayOverridesBase()
    {
        var baseSettings = new CodingAgentSettings { DefaultModel = "base", Theme = "dark" };
        var overlay = new CodingAgentSettings { DefaultModel = "overlay" };

        var merged = baseSettings.MergeWith(overlay);

        Assert.Equal("overlay", merged.DefaultModel);
        Assert.Equal("dark", merged.Theme);
    }

    [Fact]
    public void MergeWith_NullOverlayReturnsSelf()
    {
        var settings = new CodingAgentSettings { DefaultModel = "test" };

        var result = settings.MergeWith(null);

        Assert.Same(settings, result);
    }

    [Fact]
    public void UpdateGlobal_AffectsMergedSettings()
    {
        var manager = SettingsManager.InMemory();

        manager.UpdateGlobal(s => s with { DefaultModel = "claude-4" });

        Assert.Equal("claude-4", manager.Settings.DefaultModel);
        Assert.Equal("claude-4", manager.GlobalSettings.DefaultModel);
    }

    [Fact]
    public void UpdateProject_OverridesGlobal()
    {
        var manager = SettingsManager.InMemory(new CodingAgentSettings { DefaultModel = "global-model" });

        manager.UpdateProject(s => s with { DefaultModel = "project-model" });

        Assert.Equal("project-model", manager.Settings.DefaultModel);
        Assert.Equal("global-model", manager.GlobalSettings.DefaultModel);
        Assert.Equal("project-model", manager.ProjectSettings.DefaultModel);
    }

    [Fact]
    public void UpdateGlobal_TracksModifiedTopLevelFields()
    {
        var manager = SettingsManager.InMemory();

        manager.UpdateGlobal(s => s with { DefaultModel = "claude-4", Theme = "dark" });

        Assert.Contains(nameof(CodingAgentSettings.DefaultModel), manager.ModifiedGlobalFields);
        Assert.Contains(nameof(CodingAgentSettings.Theme), manager.ModifiedGlobalFields);
    }

    [Fact]
    public void Create_LoadsFromDisk_WhenFilesExist()
    {
        var agentDir = Path.Combine(_tempDir, "agent");
        var cwd = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(agentDir);
        Directory.CreateDirectory(Path.Combine(cwd, ".pi-sharp"));

        File.WriteAllText(Path.Combine(agentDir, "settings.json"),
            """{"defaultModel":"global-m"}""");
        File.WriteAllText(Path.Combine(cwd, ".pi-sharp", "settings.json"),
            """{"theme":"dark"}""");

        var manager = SettingsManager.Create(cwd, agentDir);

        Assert.Equal("global-m", manager.Settings.DefaultModel);
        Assert.Equal("dark", manager.Settings.Theme);
    }

    [Fact]
    public void Create_HandlesNonExistentFiles()
    {
        var manager = SettingsManager.Create(
            Path.Combine(_tempDir, "nonexistent"),
            Path.Combine(_tempDir, "also_nonexistent"));

        Assert.NotNull(manager.Settings);
        Assert.NotNull(manager.Settings.Compaction);
    }

    [Fact]
    public async Task FlushAndReload_RoundTrips()
    {
        var agentDir = Path.Combine(_tempDir, "agent");
        var cwd = Path.Combine(_tempDir, "project");

        var manager = SettingsManager.Create(cwd, agentDir);
        manager.UpdateGlobal(s => s with { DefaultModel = "saved-model" });
        await manager.FlushAsync();

        var reloaded = SettingsManager.Create(cwd, agentDir);

        Assert.Equal("saved-model", reloaded.Settings.DefaultModel);
    }

    [Fact]
    public async Task FlushAsync_MergesOnlyModifiedGlobalFields()
    {
        var agentDir = Path.Combine(_tempDir, "agent");
        var cwd = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(agentDir);

        var globalPath = Path.Combine(agentDir, "settings.json");
        await File.WriteAllTextAsync(
            globalPath,
            """{"defaultModel":"disk-model","theme":"light","defaultProvider":"openai"}""");

        var manager = SettingsManager.Create(cwd, agentDir);
        manager.UpdateGlobal(settings => settings with { DefaultModel = "manager-model" });

        await File.WriteAllTextAsync(
            globalPath,
            """{"defaultModel":"external-model","theme":"dark","defaultProvider":"anthropic"}""");

        await manager.FlushAsync();

        var reloaded = SettingsManager.Create(cwd, agentDir);
        Assert.Equal("manager-model", reloaded.GlobalSettings.DefaultModel);
        Assert.Equal("dark", reloaded.GlobalSettings.Theme);
        Assert.Equal("anthropic", reloaded.GlobalSettings.DefaultProvider);
        Assert.Empty(manager.ModifiedGlobalFields);
    }

    [Fact]
    public async Task FlushAsync_MergesOnlyModifiedProjectFields()
    {
        var agentDir = Path.Combine(_tempDir, "agent");
        var cwd = Path.Combine(_tempDir, "project");
        Directory.CreateDirectory(agentDir);
        Directory.CreateDirectory(Path.Combine(cwd, ".pi-sharp"));

        var projectPath = Path.Combine(cwd, ".pi-sharp", "settings.json");
        await File.WriteAllTextAsync(
            projectPath,
            """{"theme":"light","defaultModel":"disk-project"}""");

        var manager = SettingsManager.Create(cwd, agentDir);
        manager.UpdateProject(settings => settings with { Theme = "dark" });

        await File.WriteAllTextAsync(
            projectPath,
            """{"theme":"external-theme","defaultModel":"external-project"}""");

        await manager.FlushAsync();

        var reloaded = SettingsManager.Create(cwd, agentDir);
        Assert.Equal("dark", reloaded.ProjectSettings.Theme);
        Assert.Equal("external-project", reloaded.ProjectSettings.DefaultModel);
        Assert.Empty(manager.ModifiedProjectFields);
    }
}
