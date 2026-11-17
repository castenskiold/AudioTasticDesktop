namespace AudioTastic.Data.Settings;

/// <summary>
/// UI-10 part 1: root of all settings. One property per group of the General settings window.
/// Groups are filled in as features are built; every property must have a sensible default.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Current schema version. Increase it and add a migration in <see cref="SettingsMigrations"/> when the shape changes incompatibly.</summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public PlaybackSettings Playback { get; set; } = new();
    public OutputSettings Output { get; set; } = new();
    public CrossfadingSettings Crossfading { get; set; } = new();
    public MetadataSettings Metadata { get; set; } = new();
    public CastingNetworkSettings CastingNetwork { get; set; } = new();
    public InterfaceSettings Interface { get; set; } = new();
    public SystemIntegrationSettings SystemIntegration { get; set; } = new();
    public EqualizerDspSettings EqualizerDsp { get; set; } = new();
    public PluginSettings Plugins { get; set; } = new();
    public GeneralSettings General { get; set; } = new();
}

/// <summary>Names of the settings groups, used in change notifications.</summary>
public enum SettingsGroup
{
    Playback,
    Output,
    Crossfading,
    Metadata,
    CastingNetwork,
    Interface,
    SystemIntegration,
    EqualizerDsp,
    Plugins,
    General,
}

public sealed class PlaybackSettings
{
    /// <summary>CO-05: master volume 0–100 (perceptual curve). Saved playback state, not a settings page item.</summary>
    public int Volume { get; set; } = 80;

    /// <summary>CO-05: mute keeps <see cref="Volume"/> unchanged.</summary>
    public bool Muted { get; set; }
}

public sealed class OutputSettings
{
    /// <summary>PB-04: playback device name; null = system default (follows default-device changes).</summary>
    public string? DeviceName { get; set; }

    /// <summary>PB-04: total output buffer in milliseconds (P1 exit criterion: no dropouts at 20 ms).</summary>
    public int BufferMilliseconds { get; set; } = 20;
}

public sealed class CrossfadingSettings
{
}

public sealed class MetadataSettings
{
}

public sealed class CastingNetworkSettings
{
}

public sealed class InterfaceSettings
{
    /// <summary>Saved main window position; null means "centre on the primary screen".</summary>
    public WindowPlacement? MainWindow { get; set; }
}

public sealed class SystemIntegrationSettings
{
}

public sealed class EqualizerDspSettings
{
}

public sealed class PluginSettings
{
}

public sealed class GeneralSettings
{
    /// <summary>TL-03: minimum level written to the log files.</summary>
    public LogLevelSetting LogLevel { get; set; } = LogLevelSetting.Information;
}

/// <summary>Log levels offered in Settings › General.</summary>
public enum LogLevelSetting
{
    Verbose,
    Debug,
    Information,
    Warning,
    Error,
}

/// <summary>Saved window position in device-independent pixels.</summary>
public sealed record WindowPlacement(int X, int Y);
