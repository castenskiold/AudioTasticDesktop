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

    /// <summary>PB-03: ReplayGain / loudness normalisation.</summary>
    public ReplayGainSettings ReplayGain { get; set; } = new();

    /// <summary>PB-01: preload the next track so there is no gap between tracks.</summary>
    public bool Gapless { get; set; } = true;

    /// <summary>PB-02: short fades instead of hard cuts.</summary>
    public FadeSettings Fades { get; set; } = new();

    /// <summary>PB-06: playback speed 0.5–2.0 (playback state, saved).</summary>
    public double Speed { get; set; } = 1.0;

    /// <summary>PB-06: keep the pitch when the speed changes (time-stretch); off = like a turntable.</summary>
    public bool KeepPitch { get; set; } = true;

    /// <summary>PB-06: remember the speed per file.</summary>
    public bool RememberSpeedPerFile { get; set; }

    /// <summary>PB-07: restore the last track and position at start-up.</summary>
    public ResumeMode ResumeSession { get; set; } = ResumeMode.Paused;

    /// <summary>PB-07: remember the position per file for long files.</summary>
    public bool ResumeLongFiles { get; set; } = true;

    /// <summary>PB-07: minimum length (minutes) for per-file resume.</summary>
    public int ResumeMinimumMinutes { get; set; } = 20;

    /// <summary>PB-07: where playback was (saved every 10 s and on exit).</summary>
    public SessionState? LastSession { get; set; }

    /// <summary>PB-09: clear the "play next" queue when playback is stopped.</summary>
    public bool ClearQueueOnStop { get; set; }

    /// <summary>PB-10: sleep timer options.</summary>
    public SleepTimerSettings SleepTimer { get; set; } = new();
}

public sealed class OutputSettings
{
    /// <summary>PB-04: playback device name; null = system default (follows default-device changes).</summary>
    public string? DeviceName { get; set; }

    /// <summary>PB-04: total output buffer in milliseconds (P1 exit criterion: no dropouts at 20 ms).</summary>
    public int BufferMilliseconds { get; set; } = 20;

    /// <summary>PB-04: exclusive (bit-perfect) mode: no OS mixer, volume 100 % recommended.</summary>
    public bool Exclusive { get; set; }

    /// <summary>PB-05: output bit depth in exclusive mode; 0 = automatic (the source's, or the best the device has).</summary>
    public int BitDepth { get; set; }

    /// <summary>PB-01: reopen the device at each track's sample rate (a short gap) instead of resampling the next track.</summary>
    public bool MatchOutputRate { get; set; }

    /// <summary>PB-05: allow resampling when the device does not support a track's rate (exclusive mode) or for gapless.</summary>
    public bool AllowResampling { get; set; } = true;

    /// <summary>PB-05: TPDF dither when the bit depth is reduced.</summary>
    public bool Dither { get; set; } = true;

    /// <summary>FM-05: DSD playback: convert to PCM, or DoP (needs exclusive mode and a DoP DAC).</summary>
    public DsdMode DsdMode { get; set; } = DsdMode.Pcm;
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

    /// <summary>AN-01: background analysis worker threads (waveforms, loudness; BPM from P8). 1–8.</summary>
    public int AnalysisThreads { get; set; } = 2;
}

/// <summary>CO-04: repeat modes, cycled by the REP button (Off → Playlist → Track).</summary>
/// <summary>PB-02: fade on/off and length per action (0–2,000 ms).</summary>
public sealed class FadeSettings
{
    public bool OnPause { get; set; } = true;
    public int PauseMilliseconds { get; set; } = 150;
    public bool OnStop { get; set; } = true;
    public int StopMilliseconds { get; set; } = 150;
    public bool OnSeek { get; set; } = true;
    public int SeekMilliseconds { get; set; } = 150;
}

/// <summary>PB-07: what to do with the last session at start-up.</summary>
public enum ResumeMode
{
    Off,
    Paused,
    Playing,
}

/// <summary>PB-07: the last playing entry (playlist tab id, entry index and path, position).</summary>
public sealed record SessionState(string? PlaylistId, int Index, string Path, double PositionSeconds, bool WasPlaying);

/// <summary>PB-10 settings.</summary>
public sealed class SleepTimerSettings
{
    public bool FadeOut { get; set; } = true;
    public int FadeSeconds { get; set; } = 30;
    public bool CloseApp { get; set; }
}

/// <summary>FM-05: DSD output.</summary>
public enum DsdMode
{
    Pcm,
    Dop,
}

/// <summary>PB-03 settings (Settings › Playback).</summary>
public sealed class ReplayGainSettings
{
    public ReplayGainMode Mode { get; set; } = ReplayGainMode.Off;

    /// <summary>Extra gain for files with ReplayGain tags (dB, -15…+15).</summary>
    public double PreampTaggedDb { get; set; }

    /// <summary>Gain for files without tags and without analysis (or extra gain after analysis) (dB, -15…+15).</summary>
    public double PreampUntaggedDb { get; set; }

    /// <summary>Analyse loudness (EBU R128) of files without ReplayGain tags in the background.</summary>
    public bool AnalyseUntagged { get; set; } = true;

    /// <summary>Limit the gain so the track (or album) peak does not clip.</summary>
    public bool PreventClipping { get; set; } = true;

    /// <summary>Write analysed values to the files' ReplayGain tags.</summary>
    public bool WriteTags { get; set; }
}

/// <summary>PB-03 modes. Auto uses album gain when the neighbouring track is from the same album.</summary>
public enum ReplayGainMode
{
    Off,
    Track,
    Album,
    Auto,
}

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
