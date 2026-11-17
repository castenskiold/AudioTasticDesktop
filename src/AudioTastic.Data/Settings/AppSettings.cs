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

    /// <summary>CO-03: shuffle on/off (playback state).</summary>
    public bool Shuffle { get; set; }

    /// <summary>CO-04: repeat mode (playback state).</summary>
    public RepeatMode Repeat { get; set; }
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

    /// <summary>UI-05: saved mini player position.</summary>
    public WindowPlacement? MiniPlayerWindow { get; set; }

    /// <summary>UI-01: saved playlist window position, size (layout units) and visibility.</summary>
    public WindowPlacement? PlaylistWindow { get; set; }
    public WindowSize? PlaylistSize { get; set; }
    public bool PlaylistVisible { get; set; } = true;

    /// <summary>UI-05: the mini player was showing when the app closed.</summary>
    public bool MiniPlayerActive { get; set; }

    /// <summary>UI-03: skin id ("default", or a file/folder name in the skins folder).</summary>
    public string Skin { get; set; } = "default";

    /// <summary>UI-08: player window size in percent of the skin's layout size (Anders: default 150 %, 17 Nov 2026).</summary>
    public int PlayerScale { get; set; } = 150;

    /// <summary>UI-08: Winamp-style double size for the main window and mini player (doubles <see cref="PlayerScale"/>).</summary>
    public bool DoubleSize { get; set; }

    /// <summary>UI-05: keep the main window above other windows.</summary>
    public bool AlwaysOnTop { get; set; }

    /// <summary>UI-05: keep the mini player above other windows.</summary>
    public bool MiniPlayerAlwaysOnTop { get; set; } = true;

    /// <summary>UI-06: scroll the title when it does not fit.</summary>
    public bool ScrollTitle { get; set; } = true;

    /// <summary>UI-06: scroll speed in pixels per second; 0 = the skin's speed.</summary>
    public int ScrollSpeed { get; set; }

    /// <summary>CO-06: time display shows remaining instead of elapsed time.</summary>
    public bool ShowRemainingTime { get; set; }

    /// <summary>UI-09: theme of dialogs and the settings window.</summary>
    public DialogTheme DialogTheme { get; set; } = DialogTheme.System;
}

/// <summary>UI-09: dialog theme choice.</summary>
public enum DialogTheme
{
    System,
    Dark,
    Light,
}

public sealed class SystemIntegrationSettings
{
}

public sealed class EqualizerDspSettings
{
}

public sealed class PluginSettings
{
    /// <summary>PG-04: ids of plugins the user switched off.</summary>
    public List<string> Disabled { get; set; } = [];

    /// <summary>PG-05: each plugin's own settings (plugin id → JSON object), written through IPluginSettings.</summary>
    public Dictionary<string, System.Text.Json.Nodes.JsonObject> Data { get; set; } = new(StringComparer.Ordinal);

    /// <summary>PG-04: plugin folders to delete at the next start (files that were still in use when uninstalled).</summary>
    public List<string> PendingRemoval { get; set; } = [];
}

public sealed class GeneralSettings
{
    /// <summary>TL-03: minimum level written to the log files.</summary>
    public LogLevelSetting LogLevel { get; set; } = LogLevelSetting.Information;
}

/// <summary>CO-04: repeat modes, cycled by the REP button (Off → Playlist → Track).</summary>
public enum RepeatMode
{
    Off,
    Playlist,
    Track,
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

/// <summary>Saved window size in layout units (before player size / double size).</summary>
public sealed record WindowSize(double Width, double Height);

/// <summary>Saved window position in device-independent pixels.</summary>
public sealed record WindowPlacement(int X, int Y);
