using AudioTastic.Data.Settings;

namespace AudioTastic.Data.Tests;

public sealed class SettingsStoreTests
{
    [Fact]
    public void Creates_default_file_when_missing()
    {
        using var dir = new TempDir();
        var file = dir.Combine("settings.json");
        var store = new SettingsStore(file);
        Assert.True(File.Exists(file));
        Assert.Equal(AppSettings.CurrentSchemaVersion, store.Current.SchemaVersion);
        Assert.Equal(LogLevelSetting.Information, store.Current.General.LogLevel);
    }

    [Fact]
    public void Update_persists_and_survives_restart()
    {
        using var dir = new TempDir();
        var file = dir.Combine("settings.json");
        var store = new SettingsStore(file);
        store.Update(SettingsGroup.General, s => s.General.LogLevel = LogLevelSetting.Debug);
        store.Update(SettingsGroup.Interface, s => s.Interface.MainWindow = new WindowPlacement(10, 20));

        var reloaded = new SettingsStore(file);
        Assert.Equal(LogLevelSetting.Debug, reloaded.Current.General.LogLevel);
        Assert.Equal(new WindowPlacement(10, 20), reloaded.Current.Interface.MainWindow);
        Assert.Contains("\"logLevel\": \"Debug\"", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void Update_raises_changed_with_group_and_snapshot()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Combine("settings.json"));
        SettingsChangedEventArgs? seen = null;
        store.Changed += (_, e) => seen = e;
        store.Update(SettingsGroup.General, s => s.General.LogLevel = LogLevelSetting.Warning);
        Assert.NotNull(seen);
        Assert.Equal(SettingsGroup.General, seen!.Group);
        Assert.Equal(LogLevelSetting.Warning, seen.Settings.General.LogLevel);
    }

    [Fact]
    public void Current_returns_a_copy()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Combine("settings.json"));
        store.Current.General.LogLevel = LogLevelSetting.Error;
        Assert.Equal(LogLevelSetting.Information, store.Current.General.LogLevel);
    }

    [Fact]
    public void Corrupt_file_is_backed_up_and_replaced_by_defaults()
    {
        using var dir = new TempDir();
        var file = dir.Combine("settings.json");
        File.WriteAllText(file, "{ this is not json");
        var store = new SettingsStore(file);
        Assert.Equal(LogLevelSetting.Information, store.Current.General.LogLevel);
        Assert.Single(Directory.GetFiles(dir.Path, "settings.json.bad-*"));
    }

    [Fact]
    public void Unversioned_file_is_migrated()
    {
        using var dir = new TempDir();
        var file = dir.Combine("settings.json");
        File.WriteAllText(file, """{ "general": { "logLevel": "Warning" } }""");
        var store = new SettingsStore(file);
        Assert.Equal(LogLevelSetting.Warning, store.Current.General.LogLevel);
        Assert.Contains("\"schemaVersion\": 1", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public void Newer_schema_is_not_overwritten()
    {
        using var dir = new TempDir();
        var file = dir.Combine("settings.json");
        const string future = """{ "schemaVersion": 999, "general": { "logLevel": "Error" } }""";
        File.WriteAllText(file, future);
        var store = new SettingsStore(file);
        Assert.Equal(LogLevelSetting.Information, store.Current.General.LogLevel);
        Assert.Equal(future, File.ReadAllText(file));
    }

    [Fact]
    public void ResetGroup_restores_defaults_for_that_group_only()
    {
        using var dir = new TempDir();
        var store = new SettingsStore(dir.Combine("settings.json"));
        store.Update(SettingsGroup.General, s => s.General.LogLevel = LogLevelSetting.Error);
        store.Update(SettingsGroup.Interface, s => s.Interface.MainWindow = new WindowPlacement(1, 2));
        store.ResetGroup(SettingsGroup.General);
        Assert.Equal(LogLevelSetting.Information, store.Current.General.LogLevel);
        Assert.Equal(new WindowPlacement(1, 2), store.Current.Interface.MainWindow);
    }
}
