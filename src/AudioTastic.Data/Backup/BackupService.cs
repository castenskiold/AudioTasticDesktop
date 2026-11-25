using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Data.Backup;

/// <summary>TL-02: what a backup holds, read from its manifest and contents.</summary>
/// <param name="Created">When it was exported.</param>
/// <param name="AppVersion">The AudioTastic version that exported it.</param>
/// <param name="SettingsGroups">The settings groups in it (empty without settings).</param>
/// <param name="Playlists">The number of playlist tabs.</param>
/// <param name="HasPresets">Whether it has user equalizer presets.</param>
/// <param name="Skins">The installed skins in it (file or folder names).</param>
public sealed record BackupInfo(DateTimeOffset Created, string AppVersion, IReadOnlyList<SettingsGroup> SettingsGroups, int Playlists, bool HasPresets,
    IReadOnlyList<string> Skins);

/// <summary>TL-02: what to import from a backup.</summary>
/// <param name="SettingsGroups">Settings groups to replace with the backup's (the rest stay as they are).</param>
/// <param name="Playlists">Replace all playlist tabs with the backup's.</param>
/// <param name="Presets">Replace the user equalizer presets with the backup's.</param>
/// <param name="Skins">Add the backup's skins (a skin with the same name is replaced; others are kept).</param>
public sealed record BackupSelection(IReadOnlyList<SettingsGroup> SettingsGroups, bool Playlists, bool Presets, bool Skins)
{
    public bool IsEmpty => SettingsGroups.Count == 0 && !Playlists && !Presets && !Skins;

    public static BackupSelection Everything(BackupInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return new BackupSelection(info.SettingsGroups, info.Playlists > 0, info.HasPresets, info.Skins.Count > 0);
    }
}

/// <summary>
/// TL-02: settings export and import. An .atbackup file is a zip with manifest.json, settings.json, playlists/,
/// presets/ and skins/ (installed skins); caches, logs, plugins and their data are left out. Importing is staged:
/// <see cref="Stage"/> copies the backup into the data folder and the app restarts; at the next start, before
/// anything reads the data folder, <see cref="ApplyPending"/> first exports the current state to
/// backups/before-import-*.atbackup, then applies the chosen parts. Older settings are migrated; settings from a newer
/// AudioTastic are refused.
/// </summary>
public static class BackupService
{
    public const string Extension = ".atbackup";
    public const int FormatVersion = 1;

    private const string ManifestEntry = "manifest.json";
    private const string SettingsEntry = "settings.json";
    private const string PlaylistsPrefix = "playlists/";
    private const string PresetsPrefix = "presets/";
    private const string SkinsPrefix = "skins/";
    private const string PendingFile = "import-pending" + Extension;
    private const string PendingSelectionFile = "import-pending.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    /// <summary>Where automatic "before import" backups go.</summary>
    public static string BackupsFolder(AppPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);
        return Path.Combine(paths.Root, "backups");
    }

    /// <summary>
    /// Writes a backup of the data folder to <paramref name="target"/>. Call it after the settings and playlists are
    /// saved (the playlist service flushes first).
    /// </summary>
    public static void Export(AppPaths paths, string target, string appVersion)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentException.ThrowIfNullOrEmpty(target);
        var folder = Path.GetDirectoryName(Path.GetFullPath(target));
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }
        var temp = target + ".writing";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            var manifest = new Manifest(FormatVersion, appVersion, DateTimeOffset.Now, AppSettings.CurrentSchemaVersion);
            using (var writer = new StreamWriter(zip.CreateEntry(ManifestEntry).Open()))
            {
                writer.Write(JsonSerializer.Serialize(manifest, Json));
            }
            if (File.Exists(paths.SettingsFile))
            {
                using var writer = new StreamWriter(zip.CreateEntry(SettingsEntry).Open());
                writer.Write(WithoutSecrets(File.ReadAllText(paths.SettingsFile)));
            }
            AddFolder(zip, paths.PlaylistsFolder, PlaylistsPrefix);
            AddFolder(zip, paths.PresetsFolder, PresetsPrefix);
            AddFolder(zip, paths.SkinsFolder, SkinsPrefix);
        }
        File.Move(temp, target, overwrite: true);
    }

    /// <summary>The settings JSON without personal secrets (the Discogs token, MD-03), so a shared backup leaks nothing.</summary>
    internal static string WithoutSecrets(string settingsJson)
    {
        if (JsonNode.Parse(settingsJson) is not JsonObject root)
        {
            return settingsJson;
        }
        if (root["metadata"] is JsonObject metadata)
        {
            metadata.Remove("discogsToken");
        }
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static void AddFolder(ZipArchive zip, string folder, string prefix)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(folder, file).Replace('\\', '/');
            if (!relative.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
            {
                zip.CreateEntryFromFile(file, prefix + relative);
            }
        }
    }

    /// <summary>
    /// Reads a backup's manifest and contents. Throws <see cref="InvalidDataException"/> when the file is not an
    /// AudioTastic backup, or comes from a newer version this one cannot read.
    /// </summary>
    public static BackupInfo Read(string path)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var manifest = ReadManifest(zip);
            var groups = new List<SettingsGroup>();
            if (zip.GetEntry(SettingsEntry) is { } settingsEntry)
            {
                var root = ReadSettings(settingsEntry);
                groups.AddRange(Enum.GetValues<SettingsGroup>().Where(g => root[JsonName(g)] is JsonObject));
            }
            var playlists = zip.Entries.Count(e => e.FullName.StartsWith(PlaylistsPrefix, StringComparison.Ordinal) && e.FullName.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase));
            var presets = zip.Entries.Any(e => e.FullName.StartsWith(PresetsPrefix, StringComparison.Ordinal) && e.Length > 0);
            var skins = zip.Entries
                .Where(e => e.FullName.StartsWith(SkinsPrefix, StringComparison.Ordinal) && e.FullName.Length > SkinsPrefix.Length)
                .Select(e => e.FullName[SkinsPrefix.Length..].Split('/')[0])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new BackupInfo(manifest.Created, manifest.AppVersion, groups, playlists, presets, skins);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            throw new InvalidDataException($"{Path.GetFileName(path)} is not an AudioTastic backup: {ex.Message}", ex);
        }
    }

    private static Manifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("It has no manifest.json.");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<Manifest>(stream, Json) ?? throw new InvalidDataException("Its manifest is empty.");
        if (manifest.Format > FormatVersion)
        {
            throw new InvalidDataException("It was written by a newer AudioTastic. Update AudioTastic to import it.");
        }
        if (manifest.SettingsSchema > AppSettings.CurrentSchemaVersion)
        {
            throw new InvalidDataException($"Its settings come from a newer AudioTastic ({manifest.AppVersion}). Update AudioTastic to import them.");
        }
        return manifest;
    }

    /// <summary>The backup's settings, migrated to this version's schema.</summary>
    private static JsonObject ReadSettings(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        var root = JsonNode.Parse(stream) as JsonObject ?? throw new InvalidDataException("Its settings are not a JSON object.");
        try
        {
            SettingsMigrations.MigrateToCurrent(root);
        }
        catch (SettingsVersionException ex)
        {
            throw new InvalidDataException("Its settings come from a newer AudioTastic. Update AudioTastic to import them.", ex);
        }
        return root;
    }

    private static string JsonName(SettingsGroup group) => JsonNamingPolicy.CamelCase.ConvertName(group.ToString());

    /// <summary>Copies the backup into the data folder to be applied at the next start (the app restarts).</summary>
    public static void Stage(AppPaths paths, string backup, BackupSelection selection)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(selection);
        Read(backup); // refuse a broken or newer file now, not at the next start
        File.Copy(backup, Path.Combine(paths.Root, PendingFile), overwrite: true);
        File.WriteAllText(Path.Combine(paths.Root, PendingSelectionFile), JsonSerializer.Serialize(selection, Json));
    }

    /// <summary>
    /// At start-up, before the settings are loaded: applies a staged import, if any. Returns what was imported (null
    /// when nothing was staged). A failed import leaves the current data as it was and is logged.
    /// </summary>
    public static BackupSelection? ApplyPending(AppPaths paths, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(logger);
        var pending = Path.Combine(paths.Root, PendingFile);
        var selectionFile = Path.Combine(paths.Root, PendingSelectionFile);
        if (!File.Exists(pending))
        {
            return null;
        }
        try
        {
            var selection = File.Exists(selectionFile) ? JsonSerializer.Deserialize<BackupSelection>(File.ReadAllText(selectionFile), Json) : null;
            if (selection is null || selection.IsEmpty)
            {
                return null;
            }
            var safety = Path.Combine(BackupsFolder(paths), $"before-import-{DateTime.Now:yyyyMMdd-HHmmss}{Extension}");
            Export(paths, safety, "current");
            Apply(paths, pending, selection);
            logger.LogInformation("Imported from a backup: settings {Groups}, playlists {Playlists}, presets {Presets}, skins {Skins}; the previous state is in {Safety}",
                string.Join(", ", selection.SettingsGroups), selection.Playlists, selection.Presets, selection.Skins, safety);
            return selection;
        }
#pragma warning disable CA1031 // Never stop the start-up over an import: log it and start with the current data.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            logger.LogError(ex, "The staged settings import failed; the current settings were kept");
            return null;
        }
        finally
        {
            File.Delete(pending);
            File.Delete(selectionFile);
        }
    }

    /// <summary>Applies the chosen parts of a backup to the data folder (nothing may be using it).</summary>
    public static void Apply(AppPaths paths, string backup, BackupSelection selection)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(selection);
        using var zip = ZipFile.OpenRead(backup);
        ReadManifest(zip);
        if (selection.SettingsGroups.Count > 0 && zip.GetEntry(SettingsEntry) is { } settingsEntry)
        {
            var incoming = ReadSettings(settingsEntry);
            var current = File.Exists(paths.SettingsFile) ? JsonNode.Parse(File.ReadAllText(paths.SettingsFile)) as JsonObject ?? [] : [];
            SettingsMigrations.MigrateToCurrent(current);
            foreach (var group in selection.SettingsGroups)
            {
                var name = JsonName(group);
                if (incoming[name] is JsonObject value)
                {
                    var token = group == SettingsGroup.Metadata ? current["metadata"]?["discogsToken"]?.DeepClone() : null;
                    current[name] = value.DeepClone();
                    if (token is not null && current[name] is JsonObject metadata && metadata["discogsToken"] is null)
                    {
                        metadata["discogsToken"] = token; // backups carry no token: keep this computer's
                    }
                }
            }
            // Must still read as settings (a hand-edited backup must not leave a file the app cannot load).
            _ = current.Deserialize<AppSettings>(SettingsStore.JsonOptions) ?? throw new InvalidDataException("The merged settings are empty.");
            var temp = paths.SettingsFile + ".tmp";
            File.WriteAllText(temp, current.ToJsonString(SettingsStore.JsonOptions));
            File.Move(temp, paths.SettingsFile, overwrite: true);
        }
        if (selection.Playlists && zip.Entries.Any(e => e.FullName.StartsWith(PlaylistsPrefix, StringComparison.Ordinal)))
        {
            ReplaceFolder(zip, PlaylistsPrefix, paths.PlaylistsFolder);
        }
        if (selection.Presets && zip.Entries.Any(e => e.FullName.StartsWith(PresetsPrefix, StringComparison.Ordinal)))
        {
            ReplaceFolder(zip, PresetsPrefix, paths.PresetsFolder);
        }
        if (selection.Skins)
        {
            Extract(zip, SkinsPrefix, paths.SkinsFolder);
        }
    }

    private static void ReplaceFolder(ZipArchive zip, string prefix, string folder)
    {
        if (Directory.Exists(folder))
        {
            foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
            {
                File.Delete(file);
            }
        }
        Extract(zip, prefix, folder);
    }

    /// <summary>Extracts the entries under <paramref name="prefix"/> into <paramref name="folder"/>, never outside it.</summary>
    private static void Extract(ZipArchive zip, string prefix, string folder)
    {
        var root = Path.GetFullPath(folder) + Path.DirectorySeparatorChar;
        foreach (var entry in zip.Entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal) && !e.FullName.EndsWith('/')))
        {
            var target = Path.GetFullPath(Path.Combine(folder, entry.FullName[prefix.Length..]));
            if (!target.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The backup entry {entry.FullName} points outside the data folder.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private sealed record Manifest(int Format, string AppVersion, DateTimeOffset Created, int SettingsSchema);
}
