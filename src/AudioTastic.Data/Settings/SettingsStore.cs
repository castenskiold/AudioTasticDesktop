using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Data.Settings;

/// <summary>
/// UI-10 part 1: loads, saves and publishes changes to <see cref="AppSettings"/>.
/// The file is written atomically (temp file + replace). A corrupt file is kept as a backup and
/// replaced by defaults, so a bad settings file never stops the app from starting.
/// </summary>
public sealed class SettingsStore
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private AppSettings _current;

    public SettingsStore(AppPaths paths, ILogger<SettingsStore>? logger = null)
        : this(paths?.SettingsFile ?? throw new ArgumentNullException(nameof(paths)), logger)
    {
    }

    internal SettingsStore(string path, ILogger? logger = null)
    {
        _path = path;
        _logger = logger ?? NullLogger.Instance;
        _current = Load();
    }

    /// <summary>Raised after settings changed and were saved. Raised on the thread that called <see cref="Update"/>.</summary>
    public event EventHandler<SettingsChangedEventArgs>? Changed;

    /// <summary>A copy of the current settings. Changing the copy has no effect; use <see cref="Update"/>.</summary>
    public AppSettings Current
    {
        get
        {
            lock (_gate)
            {
                return Clone(_current);
            }
        }
    }

    /// <summary>Path of the settings file.</summary>
    public string FilePath => _path;

    /// <summary>Changes one or more settings, saves the file and raises <see cref="Changed"/>.</summary>
    public void Update(SettingsGroup group, Action<AppSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        AppSettings snapshot;
        lock (_gate)
        {
            var next = Clone(_current);
            change(next);
            next.SchemaVersion = AppSettings.CurrentSchemaVersion;
            Save(next);
            _current = next;
            snapshot = Clone(next);
        }
        Changed?.Invoke(this, new SettingsChangedEventArgs(group, snapshot));
    }

    /// <summary>Resets one group to its defaults.</summary>
    public void ResetGroup(SettingsGroup group)
    {
        var defaults = new AppSettings();
        Update(group, s =>
        {
            switch (group)
            {
                case SettingsGroup.Playback: s.Playback = defaults.Playback; break;
                case SettingsGroup.Output: s.Output = defaults.Output; break;
                case SettingsGroup.Crossfading: s.Crossfading = defaults.Crossfading; break;
                case SettingsGroup.Metadata: s.Metadata = defaults.Metadata; break;
                case SettingsGroup.CastingNetwork: s.CastingNetwork = defaults.CastingNetwork; break;
                case SettingsGroup.Interface: s.Interface = defaults.Interface; break;
                case SettingsGroup.SystemIntegration: s.SystemIntegration = defaults.SystemIntegration; break;
                case SettingsGroup.EqualizerDsp: s.EqualizerDsp = defaults.EqualizerDsp; break;
                case SettingsGroup.Plugins: s.Plugins = defaults.Plugins; break;
                case SettingsGroup.General: s.General = defaults.General; break;
                default: throw new ArgumentOutOfRangeException(nameof(group), group, null);
            }
        });
    }

    private AppSettings Load()
    {
        if (!File.Exists(_path))
        {
            var defaults = new AppSettings();
            Save(defaults);
            _logger.LogInformation("Created default settings at {Path}", _path);
            return defaults;
        }

        try
        {
            var root = JsonNode.Parse(File.ReadAllText(_path)) as JsonObject
                       ?? throw new JsonException("Settings root is not a JSON object.");
            var from = SettingsMigrations.MigrateToCurrent(root);
            var settings = root.Deserialize<AppSettings>(JsonOptions)
                           ?? throw new JsonException("Settings deserialised to null.");
            if (from != AppSettings.CurrentSchemaVersion)
            {
                _logger.LogInformation("Migrated settings from schema {From} to {To}", from, AppSettings.CurrentSchemaVersion);
                Save(settings);
            }
            return settings;
        }
        catch (SettingsVersionException ex)
        {
            // Written by a newer version: do not overwrite it, run with defaults in memory only.
            _logger.LogWarning(ex, "Settings file is from a newer version; using defaults without saving");
            return new AppSettings();
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException or FormatException)
        {
            var backup = $"{_path}.bad-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
            try
            {
                File.Move(_path, backup, overwrite: true);
            }
            catch (IOException moveEx)
            {
                _logger.LogError(moveEx, "Could not back up corrupt settings file {Path}", _path);
            }
            _logger.LogWarning(ex, "Settings file was unreadable and was moved to {Backup}; using defaults", backup);
            var defaults = new AppSettings();
            Save(defaults);
            return defaults;
        }
    }

    private void Save(AppSettings settings)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, JsonOptions));
        File.Move(temp, _path, overwrite: true);
    }

    private static AppSettings Clone(AppSettings s) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(s, JsonOptions), JsonOptions)!;
}

/// <summary>Arguments of <see cref="SettingsStore.Changed"/>.</summary>
public sealed class SettingsChangedEventArgs(SettingsGroup group, AppSettings settings) : EventArgs
{
    public SettingsGroup Group { get; } = group;
    public AppSettings Settings { get; } = settings;
}
