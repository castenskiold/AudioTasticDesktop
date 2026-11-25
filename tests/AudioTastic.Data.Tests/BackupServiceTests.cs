using System.IO.Compression;
using System.Text.Json.Nodes;
using AudioTastic.Data.Backup;
using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Data.Tests;

/// <summary>TL-02: .atbackup export, selective import at the next start, migration and safety.</summary>
public sealed class BackupServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("audiotastic-backup-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AppPaths Paths(string name) =>
        AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, Path.Combine(_dir, name), Path.Combine(_dir, name), null, null, [AppPaths.PortableSwitch])).EnsureCreated();

    /// <summary>A data folder with settings, two playlists, presets and a skin.</summary>
    private static void Fill(AppPaths paths, int volume, string playlist, string skin)
    {
        var settings = new SettingsStore(paths);
        settings.Update(SettingsGroup.Playback, s => s.Playback.Volume = volume);
        settings.Update(SettingsGroup.SystemIntegration, s => s.SystemIntegration.Hotkeys["playPause"] = $"Ctrl+Alt+{volume}");
        settings.Update(SettingsGroup.EqualizerDsp, s => s.EqualizerDsp.Equalizer.PreampDb = volume / 10.0);
        File.WriteAllText(Path.Combine(paths.PlaylistsFolder, $"{playlist}.m3u8"), $"#EXTM3U\n{playlist}.flac\n");
        File.WriteAllText(Path.Combine(paths.PlaylistsFolder, "playlists.json"), $"{{\"tabs\":[\"{playlist}\"]}}");
        File.WriteAllText(Path.Combine(paths.PresetsFolder, "equalizer.json"), $"[{{\"name\":\"{playlist}\"}}]");
        File.WriteAllText(Path.Combine(paths.SkinsFolder, $"{skin}.atskin"), skin);
    }

    [Fact]
    public void Export_then_import_restores_the_chosen_parts_and_keeps_a_copy_of_what_was_there()
    {
        var source = Paths("source");
        Fill(source, 42, "party", "Light");
        var backup = Path.Combine(_dir, "my.atbackup");
        BackupService.Export(source, backup, "0.10.0");

        var info = BackupService.Read(backup);
        Assert.Equal("0.10.0", info.AppVersion);
        Assert.Equal(Enum.GetValues<SettingsGroup>(), info.SettingsGroups);
        Assert.Equal(1, info.Playlists);
        Assert.True(info.HasPresets);
        Assert.Equal(["Light.atskin"], info.Skins);

        var target = Paths("target");
        Fill(target, 7, "work", "Dark");
        BackupService.Stage(target, backup, new BackupSelection([SettingsGroup.Playback, SettingsGroup.SystemIntegration], Playlists: true, Presets: false, Skins: true));
        Assert.NotNull(BackupService.ApplyPending(target, NullLogger.Instance));
        Assert.Null(BackupService.ApplyPending(target, NullLogger.Instance)); // applied once

        var settings = new SettingsStore(target).Current;
        Assert.Equal(42, settings.Playback.Volume);
        Assert.Equal("Ctrl+Alt+42", settings.SystemIntegration.Hotkeys["playPause"]);
        Assert.Equal(0.7, settings.EqualizerDsp.Equalizer.PreampDb); // not chosen: unchanged
        Assert.Equal(["party.m3u8", "playlists.json"], Directory.GetFiles(target.PlaylistsFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        Assert.Contains("work", File.ReadAllText(Path.Combine(target.PresetsFolder, "equalizer.json")), StringComparison.Ordinal);
        Assert.Equal(["Dark.atskin", "Light.atskin"], Directory.GetFiles(target.SkinsFolder).Select(Path.GetFileName).Order(StringComparer.Ordinal));

        // The state before the import was saved and can be imported back.
        var safety = Assert.Single(Directory.GetFiles(BackupService.BackupsFolder(target), "before-import-*.atbackup"));
        BackupService.Apply(target, safety, BackupSelection.Everything(BackupService.Read(safety)));
        Assert.Equal(7, new SettingsStore(target).Current.Playback.Volume);
        Assert.True(File.Exists(Path.Combine(target.PlaylistsFolder, "work.m3u8")));
    }

    [Fact]
    public void Settings_from_an_older_version_are_migrated_and_newer_ones_refused()
    {
        var old = Path.Combine(_dir, "old.atbackup");
        WriteBackup(old, settingsSchema: 1, new JsonObject
        {
            ["schemaVersion"] = 1,
            ["interface"] = new JsonObject { ["miniPlayerAlwaysOnTop"] = true, ["playerScale"] = 175 },
        });
        var target = Paths("target");
        BackupService.Apply(target, old, new BackupSelection([SettingsGroup.Interface], false, false, false));
        var settings = new SettingsStore(target).Current;
        Assert.Equal(175, settings.Interface.PlayerScale);
        Assert.False(settings.Interface.MiniPlayerAlwaysOnTop); // migration 1 → 2 ran on the backup's settings

        var newer = Path.Combine(_dir, "newer.atbackup");
        WriteBackup(newer, settingsSchema: AppSettings.CurrentSchemaVersion + 1, new JsonObject { ["schemaVersion"] = AppSettings.CurrentSchemaVersion + 1 });
        Assert.Throws<InvalidDataException>(() => BackupService.Read(newer));
        Assert.Throws<InvalidDataException>(() => BackupService.Stage(target, newer, new BackupSelection([SettingsGroup.Playback], false, false, false)));
    }

    [Fact]
    public void Files_that_are_not_backups_and_entries_outside_the_data_folder_are_refused()
    {
        var text = Path.Combine(_dir, "notes.atbackup");
        File.WriteAllText(text, "hello");
        Assert.Throws<InvalidDataException>(() => BackupService.Read(text));

        var evil = Path.Combine(_dir, "evil.atbackup");
        WriteBackup(evil, AppSettings.CurrentSchemaVersion, null, ("skins/../../outside.txt", "x"));
        var target = Paths("target");
        Assert.Throws<InvalidDataException>(() => BackupService.Apply(target, evil, new BackupSelection([], false, false, Skins: true)));
        Assert.False(File.Exists(Path.Combine(_dir, "outside.txt")));
    }

    [Fact]
    public void A_backup_never_carries_the_discogs_token_and_an_import_keeps_this_computers()
    {
        var source = Paths("source");
        new SettingsStore(source).Update(SettingsGroup.Metadata, s =>
        {
            s.Metadata.LookupDiscogs = true;
            s.Metadata.DiscogsToken = "source-secret";
        });
        var backup = Path.Combine(_dir, "shared.atbackup");
        BackupService.Export(source, backup, "0.10.0");
        using (var zip = ZipFile.OpenRead(backup))
        using (var reader = new StreamReader(zip.GetEntry("settings.json")!.Open()))
        {
            var text = reader.ReadToEnd();
            Assert.DoesNotContain("source-secret", text, StringComparison.Ordinal);
            Assert.Contains("lookupDiscogs", text, StringComparison.Ordinal);
        }

        var target = Paths("target");
        new SettingsStore(target).Update(SettingsGroup.Metadata, s => s.Metadata.DiscogsToken = "target-secret");
        BackupService.Apply(target, backup, new BackupSelection([SettingsGroup.Metadata], false, false, false));
        var metadata = new SettingsStore(target).Current.Metadata;
        Assert.True(metadata.LookupDiscogs);
        Assert.Equal("target-secret", metadata.DiscogsToken);
    }

    [Fact]
    public void A_failed_import_keeps_the_current_settings()
    {
        var target = Paths("target");
        new SettingsStore(target).Update(SettingsGroup.Playback, s => s.Playback.Volume = 33);
        var broken = Path.Combine(_dir, "broken.atbackup");
        WriteBackup(broken, AppSettings.CurrentSchemaVersion, new JsonObject { ["schemaVersion"] = AppSettings.CurrentSchemaVersion, ["playback"] = new JsonObject { ["volume"] = "loud" } });
        BackupService.Stage(target, broken, new BackupSelection([SettingsGroup.Playback], false, false, false));
        Assert.Null(BackupService.ApplyPending(target, NullLogger.Instance));
        Assert.Equal(33, new SettingsStore(target).Current.Playback.Volume);
        Assert.False(File.Exists(Path.Combine(target.Root, "import-pending.atbackup")));
    }

    private static void WriteBackup(string path, int settingsSchema, JsonObject? settings, params (string Name, string Text)[] extra)
    {
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string text)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(text);
        }
        Add("manifest.json", $"{{\"format\":1,\"appVersion\":\"0.9.0\",\"created\":\"2026-11-01T10:00:00+01:00\",\"settingsSchema\":{settingsSchema}}}");
        if (settings is not null)
        {
            Add("settings.json", settings.ToJsonString());
        }
        foreach (var (name, text) in extra)
        {
            Add(name, text);
        }
    }
}
