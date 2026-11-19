namespace AudioTastic.Data.Settings;

/// <summary>
/// UI-10 part 1: root of all settings. One property per group of the General settings window.
/// Groups are filled in as features are built; every property must have a sensible default.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Current schema version. Increase it and add a migration in <see cref="SettingsMigrations"/> when the shape changes incompatibly.</summary>
    public const int CurrentSchemaVersion = 2;

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

/// <summary>XF-01..XF-04 (Required Features 4.4).</summary>
public sealed class CrossfadingSettings
{
    /// <summary>The XF switch (MW10).</summary>
    public bool Enabled { get; set; }

    public CrossfadeMode Mode { get; set; } = CrossfadeMode.IntroOutro;

    /// <summary>
    /// Intro/Outro: how long the tracks overlap (fade-out/fade-in). Beat matching: how long both play at full volume
    /// before the first track is cut or faded out. 0.5–30 s.
    /// </summary>
    public double OverlapSeconds { get; set; } = 8;

    /// <summary>
    /// Beat matching (Anders, 18 Nov 2026): the incoming track fades in over this many seconds before the overlap at
    /// full volume begins (0.5–30 s).
    /// </summary>
    public double FadeInSeconds { get; set; } = 8;

    /// <summary>
    /// (Anders, 18 Nov 2026) The incoming track's bass starts low (−24 dB below 200 Hz) and rises to normal over its
    /// fade-in (beat matching: <see cref="FadeInSeconds"/>; Intro/Outro: the overlap).
    /// </summary>
    public bool BassFadeIn { get; set; } = true;

    /// <summary>Beat matching: what happens to the first track when the overlap ends.</summary>
    public CrossfadeEnd EndBehaviour { get; set; } = CrossfadeEnd.FadeOut;

    /// <summary>Beat matching with <see cref="CrossfadeEnd.FadeOut"/>: fade-out length (s).</summary>
    public double FadeOutSeconds { get; set; } = 4;

    /// <summary>Beat matching: the largest tempo change (percent, 1–20); bigger differences use Intro/Outro.</summary>
    public double TempoLimitPercent { get; set; } = 8;

    /// <summary>Beat matching: how the new track gets back to its own tempo after the first one has ended.</summary>
    public TempoReturnMode TempoReturn { get; set; } = TempoReturnMode.Gradual;

    /// <summary>Beat matching with <see cref="TempoReturnMode.Gradual"/>: how long the return takes (s).</summary>
    public double TempoReturnSeconds { get; set; } = 16;

    /// <summary>
    /// Beat matching (Anders, 18 Nov 2026): the mix starts this many seconds before the outgoing track's beat ends
    /// (its drums stop; outro not counted), on the phrase boundary at or before that point.
    /// </summary>
    public double MixBeforeBeatEndSeconds { get; set; } = 30;

    /// <summary>
    /// Beat matching (Anders, 18 Nov 2026): the incoming track enters this many seconds after its beat starts (its
    /// drums come in; intro skipped), on the phrase boundary at or after that point.
    /// </summary>
    public double MixInAfterBeatStartSeconds { get; set; }

    /// <summary>AN-02: analyse BPM, beats and breaks of the whole playing playlist in the background.</summary>
    public bool AnalyseInBackground { get; set; } = true;
}

public enum CrossfadeMode
{
    IntroOutro,
    BeatMatch,
}

public enum CrossfadeEnd
{
    Cut,
    FadeOut,
}

public enum TempoReturnMode
{
    CutAtNextBreak,
    Gradual,
}

public sealed class MetadataSettings
{
    /// <summary>MD-02: show album art (MW2 thumbnail and the Art &amp; Lyrics window); off shows the logo.</summary>
    public bool AlbumArt { get; set; } = true;

    /// <summary>MD-04: show lyrics in the Art &amp; Lyrics window (.lrc files and embedded lyrics).</summary>
    public bool Lyrics { get; set; } = true;

    /// <summary>MD-03: Look up online… in the tag editor (only when the user asks; title, artist and length are sent).</summary>
    public bool OnlineLookup { get; set; } = true;

    /// <summary>MD-03: services used: tags from MusicBrainz, covers from the Cover Art Archive.</summary>
    public bool LookupTags { get; set; } = true;
    public bool LookupCovers { get; set; } = true;
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
    public bool MiniPlayerAlwaysOnTop { get; set; } // off by default (Anders, 18 Nov 2026)

    /// <summary>UI-06: scroll the title when it does not fit.</summary>
    public bool ScrollTitle { get; set; } = true;

    /// <summary>UI-06: scroll speed in pixels per second; 0 = the skin's speed.</summary>
    public int ScrollSpeed { get; set; }

    /// <summary>CO-06: time display shows remaining instead of elapsed time.</summary>
    public bool ShowRemainingTime { get; set; }

    /// <summary>UI-09: theme of dialogs and the settings window.</summary>
    public DialogTheme DialogTheme { get; set; } = DialogTheme.System;

    /// <summary>UI-01 / EQ-01: saved equalizer window position and visibility.</summary>
    public WindowPlacement? EqualizerWindow { get; set; }
    public bool EqualizerVisible { get; set; }

    /// <summary>UI-01 / MD-02: saved Art &amp; Lyrics window position, size (layout units) and visibility.</summary>
    public WindowPlacement? ArtWindow { get; set; }
    public WindowSize? ArtWindowSize { get; set; }
    public bool ArtVisible { get; set; }

    /// <summary>UI-07: what MW6 shows (clicking it cycles).</summary>
    public VisualisationType Visualisation { get; set; } = VisualisationType.Spectrum;

    /// <summary>UI-07: number of spectrum bars (8–64).</summary>
    public int SpectrumBars { get; set; } = 20;

    /// <summary>UI-07: how fast the peak caps fall, in bar heights per second.</summary>
    public double PeakFallSpeed { get; set; } = 0.6;

    /// <summary>UI-07: drawing rate of MW6 (15–60 fps).</summary>
    public int VisualisationFps { get; set; } = 60;
}

/// <summary>UI-07: built-in visualisation in MW6.</summary>
public enum VisualisationType
{
    Spectrum,
    Oscilloscope,
    Off,
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
    /// <summary>
    /// SI-05: "Open in AudioTastic" and "Enqueue in AudioTastic" in the file manager's context menu.
    /// Null = the default: on, except in portable mode (a portable copy leaves the system alone).
    /// </summary>
    public bool? ContextMenu { get; set; }

    /// <summary>
    /// SI-05: AudioTastic registered for the audio and playlist types it plays ("Open with", Settings › Default apps).
    /// Null = not decided yet: the first start asks (not in portable mode).
    /// </summary>
    public bool? FileAssociations { get; set; }

    /// <summary>SI-04: the tray icon (menu bar icon on macOS) with the player menu.</summary>
    public bool TrayIcon { get; set; } = true;

    /// <summary>SI-04: minimising the player hides it to the tray (needs the tray icon).</summary>
    public bool MinimiseToTray { get; set; }

    /// <summary>SI-04: closing the player hides it to the tray instead of exiting (needs the tray icon).</summary>
    public bool CloseToTray { get; set; }

    /// <summary>SI-06: what files opened from the OS (double-click, file association, command line) do.</summary>
    public OpenFilesAction OpenFiles { get; set; } = OpenFilesAction.Play;
}

/// <summary>SI-06: files opened from the OS are added and played, or only added (enqueued).</summary>
public enum OpenFilesAction
{
    Play,
    Enqueue,
}

public sealed class EqualizerDspSettings
{
    /// <summary>EQ-01 / EQ-04: the equalizer (graphic or parametric). Its ON switch is the "equalizer" chain entry.</summary>
    public EqualizerSettings Equalizer { get; set; } = new();

    /// <summary>
    /// EQ-05: the DSP chain in processing order. Ids: equalizer, compressor, widener, reverb, limiter, and
    /// "plugin:&lt;plugin id&gt;:&lt;effect name&gt;" for plugin effects. Effects missing here are appended (off).
    /// </summary>
    public List<DspChainEntry> Chain { get; set; } = DspChainEntry.Defaults();

    public CompressorSettings Compressor { get; set; } = new();
    public LimiterSettings Limiter { get; set; } = new();
    public ReverbSettings Reverb { get; set; } = new();
    public WidenerSettings Widener { get; set; } = new();

    /// <summary>EQ-03: load a preset automatically when a track starts (file rule first, then genre rule).</summary>
    public bool AutoEq { get; set; }

    /// <summary>EQ-03: genre (case-insensitive) → preset name. File rules live in cache.db (kind "autoeq").</summary>
    public Dictionary<string, string> GenrePresets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>EQ-01 / EQ-04 settings.</summary>
public sealed class EqualizerSettings
{
    /// <summary>Winamp's ten bands (Hz).</summary>
    public static readonly double[] GraphicFrequencies = [60, 170, 310, 600, 1000, 3000, 6000, 12000, 14000, 16000];

    public EqualizerMode Mode { get; set; } = EqualizerMode.Graphic;

    /// <summary>Preamp, ±12 dB.</summary>
    public double PreampDb { get; set; }

    /// <summary>EQ-01: the ten graphic bands, ±12 dB each.</summary>
    public double[] Bands { get; set; } = new double[10];

    /// <summary>EQ-04: up to ten parametric bands.</summary>
    public List<ParametricBand> Parametric { get; set; } = ParametricBand.Defaults();

    /// <summary>EQ-02: name of the preset last loaded (shown in the window); null after manual changes.</summary>
    public string? Preset { get; set; }

    public EqualizerSettings Clone() => new()
    {
        Mode = Mode,
        PreampDb = PreampDb,
        Bands = (double[])Bands.Clone(),
        Parametric = [.. Parametric],
        Preset = Preset,
    };
}

public enum EqualizerMode
{
    Graphic,
    Parametric,
}

/// <summary>EQ-04 filter types.</summary>
public enum FilterType
{
    Peak,
    LowShelf,
    HighShelf,
    LowPass,
    HighPass,
    Notch,
}

/// <summary>EQ-04: one parametric band: frequency 20 Hz–20 kHz, gain ±24 dB, Q 0.1–10.</summary>
public sealed record ParametricBand(FilterType Type, double Frequency, double GainDb, double Q, bool Enabled = true)
{
    public static List<ParametricBand> Defaults() =>
    [
        new(FilterType.LowShelf, 100, 0, 0.7),
        new(FilterType.Peak, 400, 0, 1),
        new(FilterType.Peak, 1500, 0, 1),
        new(FilterType.Peak, 5000, 0, 1),
        new(FilterType.HighShelf, 10000, 0, 0.7),
    ];
}

/// <summary>EQ-05: one effect in the chain.</summary>
public sealed record DspChainEntry(string Id, bool Enabled)
{
    public const string Equalizer = "equalizer";
    public const string Compressor = "compressor";
    public const string Widener = "widener";
    public const string Reverb = "reverb";
    public const string Limiter = "limiter";
    public const string PluginPrefix = "plugin:";

    public static List<DspChainEntry> Defaults() =>
        [new(Equalizer, false), new(Compressor, false), new(Widener, false), new(Reverb, false), new(Limiter, false)];
}

public sealed class CompressorSettings
{
    public double ThresholdDb { get; set; } = -20;
    public double Ratio { get; set; } = 4;
    public double AttackMs { get; set; } = 10;
    public double ReleaseMs { get; set; } = 200;
    public double MakeupDb { get; set; }
}

public sealed class LimiterSettings
{
    public double CeilingDb { get; set; } = -1;
    public double ReleaseMs { get; set; } = 100;
}

public sealed class ReverbSettings
{
    /// <summary>0–1.</summary>
    public double RoomSize { get; set; } = 0.5;

    /// <summary>0–1.</summary>
    public double Damping { get; set; } = 0.5;

    /// <summary>Wet level 0–1 (the dry signal stays at full level).</summary>
    public double Mix { get; set; } = 0.2;

    /// <summary>Stereo width of the reverb 0–1.</summary>
    public double Width { get; set; } = 1;
}

public sealed class WidenerSettings
{
    /// <summary>Side level: 0 = mono, 1 = unchanged, 2 = twice as wide.</summary>
    public double Width { get; set; } = 1.5;
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
