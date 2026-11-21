using System.Runtime;
using AudioTastic.Core.Audio;
using AudioTastic.Core.Audio.Beats;
using AudioTastic.Core.Audio.Output;
using AudioTastic.Core.Playlists;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Audio;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Core.Playback;

/// <summary>
/// CO-02 / CO-05 / CO-06 / PB-04 part 1: the playback engine. Owns the <see cref="MixerGraph"/>, opens files through
/// the registered input plugins, drives the output device and implements the transport state machine
/// (Stopped, Buffering, Playing, Paused).
/// <para>
/// Commands are serialised by one async gate. A 10 Hz monitor raises position events, logs underruns, advances to
/// the next track when a deck ends, and reports decoder faults.
/// </para>
/// <para>
/// PB-01 gapless: 10 s before the end, the next entry is opened on the spare deck (resampled to the output rate if
/// needed) and armed; the mixer starts it in the same render the current deck ends, and the monitor then makes it the
/// current deck. An explicit play opens the output at the track's own rate (no forced resampling, PB-05); in exclusive
/// mode (PB-04) rate and bit depth are chosen from the device's native formats.
/// </para>
/// </summary>
public sealed class PlaybackEngine : IPlayerController, IDisposable
{
    public const int EngineChannels = 2;

    private static readonly TimeSpan MonitorInterval = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan RestartThreshold = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan SaveDelay = TimeSpan.FromMilliseconds(500);

    private readonly IEnumerable<IAudioSourceFactory> _factories;
    private readonly IAudioOutput _output;
    private readonly SettingsStore _settings;
    private readonly ILogger<PlaybackEngine> _logger;
    private readonly MixerGraph _graph;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _monitor;
    private readonly Timer _saveTimer;
    private readonly PlayOrder _playOrder = new();

    // PL-01: the engine plays entries of a playlist (the 'playing' tab, PL-02). The entry is tracked by identity,
    // so edits, sorting and moves in the playlist keep the current track; _index is its last known position.
    private Playlist _playlist = new("Now playing");
    private PlaylistEntry? _entry;
    private int _index = -1;
    private int _orderVersion = -1;
    private volatile PlaybackState _state = PlaybackState.Stopped;
    private TrackInfo? _track;
    private Deck _deck;
    private Deck _spare;
    private Preloaded? _preload;
    private PlaylistEntry? _preloadSkipped;
    private long _handoversSeen;
    private int _preloading;
    private OutputOptions? _outputOptions;
    private bool _stopAfterCurrent;
    private AbLoop? _abLoop;
    private readonly ResumeStore _resume;
    private double _speed = 1;
    private bool _keepPitch = true;
    private int _saveTicks;
    private TimeSpan? _startAt;
    private bool _startPaused;
    private bool _endedNaturally;
    private bool _bitPerfect; // FM-05: a DoP track is playing: no gain, fades, speed or ReplayGain may touch it
    private IReplayGainProvider? _gainProvider;

    // XF-01..XF-04 crossfading (all changed inside the gate, except the tempo timer which takes the gate itself).
    private IBeatInfoProvider? _beatProvider;
    private ICrossfadeAligner? _crossfadeAligner;
    private BeatInfo? _trackBeats;          // AN-02 analysis of the current track, null until it is known
    private long _trackBeatsCheckedAt;
    private PlaylistEntry? _nextBeatsRequested; // AN-02: the next track's analysis was asked for
    private long _preloadRetryAt;              // XF-02: waiting for beat analysis before planning
    private CrossfadePlan? _plan;           // the transition planned for the armed incoming deck
    private CrossfadePlan? _mixing;         // after the hand-over: the outgoing deck still sounds, or the tempo returns
    private Deck? _fading;                  // the outgoing deck after the hand-over
    private string? _fadingPath;            // MD-01: its file, open until the deck is unloaded
    private int _finishingFade;
    private double? _deckSpeedOverride;     // XF-02: matched speed of _deck (null = the normal speed)
    private double? _spareSpeedOverride;
    private TempoReturnState? _tempoReturn;
    private readonly Timer _tempoTimer;

    /// <summary>XF-04: a running tempo return of the current deck.</summary>
    private sealed class TempoReturnState(TempoReturnMode mode, double from, double to, double seconds, double? at)
    {
        public TempoReturnMode Mode { get; } = mode;
        public double From { get; } = from;
        public double To { get; } = to;
        public double Seconds { get; } = seconds;
        public double? At { get; } = at;
        public double Elapsed { get; set; }
    }
    private ReplayGainDecision? _replayGain;
    private IStreamMetadata? _streamMetadata;
    private AudioFormat _sourceFormat = new(48_000, EngineChannels);
    private bool _canSeek;
    private long _lastUnderruns;
    private int _advancing;
    private int _volume;
    private bool _muted;
    private volatile bool _disposed;

    public PlaybackEngine(IEnumerable<IAudioSourceFactory> factories, IAudioOutput output, SettingsStore settings,
        ILogger<PlaybackEngine> logger, ResumeStore? resume = null)
    {
        _resume = resume ?? new ResumeStore(null);
        ArgumentNullException.ThrowIfNull(factories);
        _factories = factories; // PG-01: may be a live view of the enabled input plugins; enumerated per open
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _graph = new MixerGraph(new AudioFormat(48_000, EngineChannels));
        _deck = _graph.DeckA;
        _spare = _graph.DeckB;
        _speed = Math.Clamp(settings.Current.Playback.Speed, 0.5, 2.0);
        _keepPitch = settings.Current.Playback.KeepPitch;
        ApplySpeed();
        _volume = Math.Clamp(settings.Current.Playback.Volume, 0, 100);
        _playOrder.SetShuffle(settings.Current.Playback.Shuffle, -1);
        _playOrder.Repeat = settings.Current.Playback.Repeat;
        _muted = settings.Current.Playback.Muted;
        ApplyGain();
        _graph.Master.SnapToTarget();

        _output.OutputLost += OnOutputLost;
        _monitor = new Timer(_ => Monitor(), null, MonitorInterval, MonitorInterval);
        _saveTimer = new Timer(_ => SavePlaybackState(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _tempoTimer = new Timer(_ => TempoTick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _settings.Changed += OnSettingsChanged;

        // Risk noted in the plan: keep blocking gen-2 collections away from the audio thread.
        GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
    }

    /// <summary>The mixer graph (for diagnostics and later phases: DSP chain, visualisation tap).</summary>
    public MixerGraph Graph => _graph;

    public PlaybackState State => _state;
    public TrackInfo? CurrentTrack => _track;
    public int CurrentIndex => _index;
    public Playlist PlayingPlaylist => _playlist;
    public PlaylistEntry? CurrentEntry => _entry;

    /// <summary>
    /// MD-01: true while a deck has <paramref name="path"/> open: the current track (unless stopped), the preloaded next
    /// track, or the outgoing track of a crossfade. Tag writes to such a file wait until it is released.
    /// </summary>
    public bool IsDecoding(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        bool Same(string? other) => other is not null &&
            string.Equals(other, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        return (_state != PlaybackState.Stopped && Same(Volatile.Read(ref _entry)?.Path))
            || Same(Volatile.Read(ref _preload)?.Entry.Path)
            || (Volatile.Read(ref _fading) is not null && Same(Volatile.Read(ref _fadingPath)));
    }
    public string? OutputDeviceName => _output.IsOpen ? _output.DeviceName : null;

    public TimeSpan Position => _state == PlaybackState.Stopped || _track is null
        ? TimeSpan.Zero
        : _remote is { } remote ? remote.Position : _sourceFormat.FramesToTime(_deck.PositionFrames);

    private IRemotePlayer? _remote;
    private int _remoteFailures; // tracks the device refused in a row

    /// <summary>CN-01 / CN-02: the cast device playing instead of the local output, or null.</summary>
    public IRemotePlayer? Remote => _remote;

    /// <summary>Raised (on a worker thread) when playback moves to or from a cast device.</summary>
    public event EventHandler<EventArgs>? RemoteChanged;

    /// <summary>
    /// CN-01 / CN-02: moves playback to <paramref name="remote"/> (null = back to this computer) at the current track
    /// and position, keeping playing or paused as it was. A stopped player just remembers the target.
    /// </summary>
    public Task SetRemoteAsync(IRemotePlayer? remote) => RunAsync(async () =>
    {
        if (ReferenceEquals(remote, _remote))
        {
            return;
        }
        var state = _state;
        var position = Position;
        var previous = _remote;
        if (previous is not null)
        {
            previous.Lost -= OnRemoteLost;
            try
            {
                await previous.StopAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // A device that is already gone cannot be stopped: keep switching.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogDebug(ex, "Could not stop playback on {Device}", previous.Name);
            }
        }
        else if (state is PlaybackState.Playing or PlaybackState.Paused)
        {
            await CancelPreloadAsync().ConfigureAwait(false);
            await StopOutputAsync().ConfigureAwait(false);
            _graph.Paused = false;
        }
        _remote = remote;
        _remoteFailures = 0;
        if (remote is not null)
        {
            remote.Lost += OnRemoteLost;
        }
        _logger.LogInformation("Playback now on {Device}", remote?.Name ?? "this computer");
        Raise(RemoteChanged, EventArgs.Empty);
        if (state is PlaybackState.Playing or PlaybackState.Paused or PlaybackState.Buffering && _index >= 0 && _index < _playlist.Count)
        {
            _startAt = position;
            _startPaused = state == PlaybackState.Paused;
            await PlayIndexCoreAsync(_index).ConfigureAwait(false);
        }
    });

    /// <summary>The cast device went away: continue on this computer, paused, where it was.</summary>
    private void OnRemoteLost(object? sender, EventArgs e) => _ = Task.Run(async () =>
    {
        if (!ReferenceEquals(sender, _remote))
        {
            return;
        }
        _logger.LogWarning("Lost the cast device {Device}; playback returns to this computer, paused", _remote?.Name);
        var position = Position;
        await RunAsync(async () =>
        {
            if (!ReferenceEquals(sender, _remote))
            {
                return;
            }
            _remote!.Lost -= OnRemoteLost;
            _remote = null;
            Raise(RemoteChanged, EventArgs.Empty);
            if (_state is PlaybackState.Playing or PlaybackState.Paused && _index >= 0 && _index < _playlist.Count)
            {
                _startAt = position;
                _startPaused = true;
                await PlayIndexCoreAsync(_index).ConfigureAwait(false);
            }
        }).ConfigureAwait(false);
    });

    public TimeSpan? Duration => _track?.Duration;

    /// <summary>Total underruns on the active deck since the current track was loaded.</summary>
    public long Underruns => _deck.Underruns;

    public int Volume
    {
        get => _volume;
        set
        {
            var v = Math.Clamp(value, 0, 100);
            if (v == _volume)
            {
                return;
            }
            _volume = v;
            ApplyGain();
            ScheduleSave();
            ForwardVolume();
            Raise(VolumeChanged, EventArgs.Empty);
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            if (value == _muted)
            {
                return;
            }
            _muted = value;
            ApplyGain();
            ScheduleSave();
            ForwardVolume();
            Raise(VolumeChanged, EventArgs.Empty);
        }
    }

    /// <summary>The volume or mute changed (any thread).</summary>
    public event EventHandler<EventArgs>? VolumeChanged;

    private volatile bool _volumeOnDevice;

    /// <summary>
    /// CN-09 audio and video: while a TV plays the mix, the mix goes at full level and the volume slider moves the
    /// TV's own volume (through <see cref="VolumeChanged"/>); mute still silences the mix.
    /// </summary>
    public bool VolumeOnDevice
    {
        get => _volumeOnDevice;
        set
        {
            _volumeOnDevice = value;
            ApplyGain();
        }
    }

    public event EventHandler<PlaybackStateEventArgs>? StateChanged;
    public event EventHandler<TrackChangedEventArgs>? TrackChanged;
    public event EventHandler<PlaybackPositionEventArgs>? PositionChanged;
    public event EventHandler<PlaybackErrorEventArgs>? PlaybackError;
    public event EventHandler<EventArgs>? PlayModeChanged;

    /// <summary>PB-09 / PB-10: playback stopped at the end of a track (stop after current, end of the playlist).</summary>
    public event EventHandler<EventArgs>? TrackEndedStop;

    /// <summary>PB-04 / PB-05: the output was (re)opened: see <see cref="OutputStatus"/>.</summary>
    public event EventHandler<EventArgs>? OutputChanged;

    /// <summary>PB-05: how the output is running: mode, sample format and the hardware rate (for the OUT indicator).</summary>
    public OutputStatus? OutputStatus => _output.IsOpen && _output.Format is { } format
        ? new OutputStatus(_output.IsExclusive, _output.SampleFormat, format.SampleRate, _output.DeviceSampleRate)
        : null;

    /// <summary>AN-02 / XF-02: beat analysis for beat matching (set by the app; without it crossfades use Intro/Outro).</summary>
    public IBeatInfoProvider? BeatProvider
    {
        get => _beatProvider;
        set => _beatProvider = value;
    }

    /// <summary>XF-02: lines up the kicks from the audio of the overlap (set by the app; without it the grids decide).</summary>
    public ICrossfadeAligner? CrossfadeAligner
    {
        get => _crossfadeAligner;
        set => _crossfadeAligner = value;
    }

    public CrossfadeStatus? Crossfade
    {
        get
        {
            if (_mixing is { } mixing)
            {
                var to = mixing.IncomingBpm is { } bpm ? bpm * (_deckSpeedOverride ?? NormalSpeed) : (double?)null;
                return new CrossfadeStatus(mixing.Mode, null, null, mixing.OutgoingBpm, to, true);
            }
            if (_plan is { } plan)
            {
                var end = plan.MixStart + (plan.OutgoingTail * CurrentDeckSpeed);
                return new CrossfadeStatus(plan.Mode, TimeSpan.FromSeconds(plan.MixStart), TimeSpan.FromSeconds(Math.Min(end, Duration?.TotalSeconds ?? end)),
                    plan.OutgoingBpm, plan.MatchedBpm, false);
            }
            return null;
        }
    }

    /// <summary>PB-09: the "play next" queue.</summary>
    public QueueService Queue { get; } = new();

    /// <summary>PB-07 / PB-06: per-file resume positions and speeds.</summary>
    public ResumeStore ResumePositions => _resume;

    /// <summary>PB-06: playback speed 0.5–2.0 (saved; per file when "remember speed per file" is on).</summary>
    public double Speed
    {
        get => _speed;
        set
        {
            var speed = Math.Round(Math.Clamp(value, 0.5, 2.0), 2);
            if (speed == _speed)
            {
                return;
            }
            _speed = speed;
            ApplySpeed();
            _settings.Update(SettingsGroup.Playback, s => s.Playback.Speed = speed);
            if (_settings.Current.Playback.RememberSpeedPerFile && _entry is { IsUrl: false } entry)
            {
                _resume.SetSpeed(entry.Path, speed);
            }
            Raise(SpeedChanged, EventArgs.Empty);
        }
    }

    /// <summary>PB-06: keep the pitch when the speed changes (time-stretch) or change both (like a turntable).</summary>
    public bool KeepPitch
    {
        get => _keepPitch;
        set
        {
            if (value == _keepPitch)
            {
                return;
            }
            _keepPitch = value;
            ApplySpeed();
            _settings.Update(SettingsGroup.Playback, s => s.Playback.KeepPitch = value);
            Raise(SpeedChanged, EventArgs.Empty);
        }
    }

    /// <summary>PB-06: <see cref="Speed"/> or <see cref="KeepPitch"/> changed.</summary>
    public event EventHandler<EventArgs>? SpeedChanged;

    /// <summary>
    /// PB-07: plays <paramref name="entry"/> from <paramref name="position"/> (session resume at start-up), paused
    /// unless <paramref name="play"/>.
    /// </summary>
    public Task ResumeSessionAsync(Playlist playlist, PlaylistEntry entry, TimeSpan position, bool play)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(entry);
        return RunAsync(() =>
        {
            _playlist = playlist;
            _entry = entry;
            SyncOrder(force: true);
            var index = CurrentIndexCore();
            if (index < 0)
            {
                return Task.CompletedTask;
            }
            _startAt = position;
            _startPaused = !play;
            return PlayIndexCoreAsync(index);
        });
    }

    /// <summary>PB-09: stop when the current track ends (switches itself off). Raises PlayModeChanged.</summary>
    public bool StopAfterCurrent
    {
        get => _stopAfterCurrent;
        set => _ = RunAsync(async () =>
        {
            if (value != _stopAfterCurrent)
            {
                _stopAfterCurrent = value;
                if (value)
                {
                    await CancelPreloadAsync().ConfigureAwait(false);
                }
                Raise(PlayModeChanged, EventArgs.Empty);
            }
        });
    }

    /// <summary>PB-08: the A-B loop (B null while only A is set); null when off.</summary>
    public AbLoop? AbLoop => _abLoop;

    /// <summary>
    /// PB-08 ([ key): first press sets A at the current position, the second sets B and starts looping, the third
    /// clears the loop. Not for streams.
    /// </summary>
    public Task AbRepeatAsync() => RunAsync(async () =>
    {
        if (_track is null || !_canSeek || _state == PlaybackState.Stopped || _track.Duration is null)
        {
            return;
        }
        var position = Position;
        if (_abLoop is null)
        {
            _abLoop = new AbLoop(position, null);
        }
        else if (_abLoop.B is null)
        {
            await ApplyLoopAsync(_abLoop.A, position).ConfigureAwait(false);
            return;
        }
        else
        {
            ClearLoop();
        }
        Raise(PlayModeChanged, EventArgs.Empty);
    });

    /// <summary>PB-08: sets the loop directly (markers dragged on the waveform); at least 1 s long.</summary>
    public Task SetAbLoopAsync(TimeSpan a, TimeSpan b) => RunAsync(() =>
        _track is null || !_canSeek || _state == PlaybackState.Stopped ? Task.CompletedTask : ApplyLoopAsync(a, b));

    /// <summary>PB-08: removes the loop.</summary>
    public Task ClearAbLoopAsync() => RunAsync(() =>
    {
        if (_abLoop is not null)
        {
            ClearLoop();
            Raise(PlayModeChanged, EventArgs.Empty);
        }
        return Task.CompletedTask;
    });

    /// <summary>PB-03: <see cref="ReplayGain"/> changed (new track, analysis finished, settings changed).</summary>
    public event EventHandler<EventArgs>? ReplayGainChanged;

    /// <summary>PB-03: the gain applied to the current track, or null when none.</summary>
    public ReplayGainDecision? ReplayGain => _replayGain;

    /// <summary>CO-07: recent peaks of the mixed signal (live waveform for streams).</summary>
    public PeakTap Peaks => _graph.Peaks;

    /// <summary>PB-03: decides the gain per track; set once after construction (null: no ReplayGain).</summary>
    public IReplayGainProvider? ReplayGainProvider
    {
        get => _gainProvider;
        set
        {
            if (_gainProvider is not null)
            {
                _gainProvider.GainChanged -= OnGainChanged;
            }
            _gainProvider = value;
            if (value is not null)
            {
                value.GainChanged += OnGainChanged;
            }
        }
    }

    /// <summary>CO-03: shuffle; saved across restarts.</summary>
    public bool Shuffle
    {
        get => _playOrder.Shuffle;
        set => _ = RunAsync(() =>
        {
            if (value != _playOrder.Shuffle)
            {
                _playOrder.SetShuffle(value, CurrentIndexCore());
                _orderVersion = _playlist.Version;
                SavePlayMode();
            }
            return Task.CompletedTask;
        });
    }

    /// <summary>CO-04: repeat mode; saved across restarts.</summary>
    public RepeatMode Repeat
    {
        get => _playOrder.Repeat;
        set => _ = RunAsync(() =>
        {
            if (value != _playOrder.Repeat)
            {
                _playOrder.Repeat = value;
                SavePlayMode();
            }
            return Task.CompletedTask;
        });
    }

    public Task OpenAsync(IEnumerable<string> paths, bool play = true)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var playlist = new Playlist("Now playing", paths.Select(p => new PlaylistEntry(p)));
        return RunAsync(async () =>
        {
            _playlist = playlist;
            _entry = null;
            _index = playlist.Count > 0 ? 0 : -1;
            SyncOrder(force: true);
            if (_playOrder.Shuffle && playlist.Count > 0)
            {
                _index = _playOrder.Order[0]; // CO-03: shuffle starts anywhere in the list
            }
            if (play && _index >= 0)
            {
                await PlayIndexCoreAsync(_index).ConfigureAwait(false);
            }
            else
            {
                await StopCoreAsync().ConfigureAwait(false);
                _track = null;
                Raise(TrackChanged, new TrackChangedEventArgs(null, _index, null));
            }
        });
    }

    /// <summary>CN-01: the player's volume controls the cast device's own volume.</summary>
    private void ForwardVolume()
    {
        if (_remote is { } remote)
        {
            var (volume, muted) = (_volume, _muted);
            _ = Task.Run(async () =>
            {
                try
                {
                    await remote.SetVolumeAsync(volume, muted).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Volume is best effort.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _logger.LogDebug(ex, "Could not set the volume on {Device}", remote.Name);
                }
            });
        }
    }

    public Task PlayAsync() => RunAsync(async () =>
    {
        switch (_state)
        {
            case PlaybackState.Paused when _remote is { } remote:
                await remote.ResumeAsync().ConfigureAwait(false);
                SetState(PlaybackState.Playing);
                break;
            case PlaybackState.Paused:
                ResumeCore();
                break;
            case PlaybackState.Playing:
                await SeekCoreAsync(TimeSpan.Zero).ConfigureAwait(false);
                break;
            case PlaybackState.Stopped when _playlist.Count > 0:
                await PlayIndexCoreAsync(Math.Max(0, CurrentIndexCore())).ConfigureAwait(false);
                break;
        }
    });

    public Task PlayAsync(Playlist playlist, PlaylistEntry entry)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(entry);
        return RunAsync(() =>
        {
            _playlist = playlist;
            _entry = entry;
            SyncOrder(force: true);
            var index = CurrentIndexCore();
            return index >= 0 ? PlayIndexCoreAsync(index) : Task.CompletedTask;
        });
    }

    public Task PlayIndexAsync(int index) => RunAsync(() =>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _playlist.Count);
        return PlayIndexCoreAsync(index);
    });

    public Task PauseAsync() => RunAsync(async () =>
    {
        if (_remote is { } remote && _state is PlaybackState.Playing or PlaybackState.Paused)
        {
            if (_state == PlaybackState.Playing)
            {
                await remote.PauseAsync().ConfigureAwait(false);
                SetState(PlaybackState.Paused);
            }
            else
            {
                await remote.ResumeAsync().ConfigureAwait(false);
                SetState(PlaybackState.Playing);
            }
            return;
        }
        if (_state == PlaybackState.Playing)
        {
            var fades = _settings.Current.Playback.Fades;
            await FadeOutAsync(fades.OnPause ? fades.PauseMilliseconds : 0).ConfigureAwait(false);
            _graph.Paused = true;
            SetState(PlaybackState.Paused);
        }
        else if (_state == PlaybackState.Paused)
        {
            ResumeCore();
        }
    });

    public Task StopAsync() => RunAsync(async () =>
    {
        await StopCoreAsync().ConfigureAwait(false);
        if (_settings.Current.Playback.ClearQueueOnStop)
        {
            Queue.Clear(); // PB-09
        }
    });

    public Task PreviousAsync() => RunAsync(async () =>
    {
        var active = _state is PlaybackState.Playing or PlaybackState.Paused;
        SyncOrder();
        var current = CurrentIndexCore();
        if (active && (Position > RestartThreshold || _playOrder.Previous(current) is null))
        {
            await SeekCoreAsync(TimeSpan.Zero).ConfigureAwait(false);
        }
        else if (_playOrder.Previous(current) is int previous)
        {
            await GoToAsync(previous, active).ConfigureAwait(false);
        }
    });

    public Task NextAsync() => RunAsync(async () =>
    {
        var play = _state is PlaybackState.Playing or PlaybackState.Paused;
        if (TakeFromQueue() is { } queued)
        {
            await GoToAsync(queued, play).ConfigureAwait(false);
            return;
        }
        SyncOrder();
        if (_playOrder.Next(CurrentIndexCore(), trackEnded: false) is int next)
        {
            await GoToAsync(next, play).ConfigureAwait(false);
        }
    });

    public Task SeekAsync(TimeSpan position) => RunAsync(() => SeekCoreAsync(position));

    public IReadOnlyList<AudioDeviceInfo> GetOutputDevices() => _output.GetDevices();

    public Task SetOutputDeviceAsync(string? deviceName) => RunAsync(() =>
    {
        _settings.Update(SettingsGroup.Output, s => s.Output.DeviceName = deviceName);
        ReopenOutput();
        return Task.CompletedTask;
    });

    /// <summary>
    /// CN-09: reopens the open output where the output layer now routes it (e.g. diverted to a TV and back),
    /// without changing Settings › Output. Playing stays playing.
    /// </summary>
    public Task ReopenOutputAsync() => RunAsync(() =>
    {
        ReopenOutput();
        return Task.CompletedTask;
    });

    private void ReopenOutput()
    {
        if (_output.IsOpen && _output.Format is { } format)
        {
            var wasRunning = _output.IsRunning;
            _output.StopOutput();
            _graph.OutputActive = false;
            OpenOutput(format, _outputOptions ?? new OutputOptions(_settings.Current.Output.BufferMilliseconds));
            if (wasRunning)
            {
                StartOutput();
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _monitor.Dispose();
        _saveTimer.Dispose();
        _tempoTimer.Dispose();
        _settings.Changed -= OnSettingsChanged;
        SavePlaybackState();
        SaveResumePosition(finished: false);
        SaveSession();
        _output.OutputLost -= OnOutputLost;
        _graph.OutputActive = false;
        _output.Close();
        _graph.Dispose();
        _gate.Dispose();
    }

    // ---- commands (run inside the gate) -------------------------------------------------------------

    private async Task GoToAsync(int index, bool play)
    {
        if (play)
        {
            await PlayIndexCoreAsync(index).ConfigureAwait(false);
            return;
        }
        var entries = _playlist.Entries;
        if (index < 0 || index >= entries.Length)
        {
            return;
        }
        _index = index;
        _entry = entries[index];
        _track = null;
        Raise(TrackChanged, new TrackChangedEventArgs(null, index, _entry));
    }

    /// <summary>Plays the track at <paramref name="index"/>; unplayable files are reported and skipped.</summary>
    private async Task PlayIndexCoreAsync(int index)
    {
        SaveResumePosition(finished: _endedNaturally);
        _endedNaturally = false;
        var startAt = _startAt;
        var startPaused = _startPaused;
        _startAt = null;
        _startPaused = false;
        // Unplayable files are skipped in play order (CO-03), trying each track at most once.
        var entries = _playlist.Entries;
        int? next = index;
        for (var attempt = 0; next is int i && i < entries.Length && attempt < entries.Length; attempt++, next = _playOrder.Next(i, trackEnded: false))
        {
            var entry = entries[i];
            var path = entry.Path;
            SetState(PlaybackState.Buffering);
            IAudioSource source;
            try
            {
                source = await Task.Run(() => OpenSource(entry)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AudioSourceException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                ReportError(path, ex);
                continue;
            }

            // FM-10: only sources without a length are live streams (a file's decoder may carry metadata members too).
            var metadata = source.LengthFrames is null ? source as IStreamMetadata ?? (source as IStreamMetadataSource)?.StreamMetadata : null;
            var track = BuildTrack(entry, source, metadata);
            DetachStreamMetadata();
            ApplyReplayGain(entry); // before the deck starts, so the track begins at its own level
            ApplyFileSpeed(entry);
            if (_remote is { } remote)
            {
                // CN-01: the device plays the file itself; the decoder was only opened for the track's details.
                var remoteStart = StartPosition(entry, track, startAt) ?? TimeSpan.Zero;
                _canSeek = source.CanSeek;
                _sourceFormat = source.Format;
                source.Dispose();
                try
                {
                    await remote.LoadAsync(entry, track, remoteStart, startPaused).ConfigureAwait(false);
                }
#pragma warning disable CA1031 // Any device failure is reported like an unplayable file.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    ReportError(path, new IOException($"{remote.Name} could not play this track: {ex.Message}", ex));
                    if (++_remoteFailures >= 2)
                    {
                        // CN-01/02: a device that refuses two tracks in a row is given up on: back to this computer,
                        // paused at the track that was asked for, rather than skipping through the playlist.
                        _logger.LogWarning("{Device} refused two tracks in a row; playback returns to this computer, paused", remote.Name);
                        remote.Lost -= OnRemoteLost;
                        _remote = null;
                        _remoteFailures = 0;
                        Raise(RemoteChanged, EventArgs.Empty);
                        _startAt = startAt;
                        _startPaused = true;
                        await PlayIndexCoreAsync(index).ConfigureAwait(false);
                        return;
                    }
                    continue;
                }
                _remoteFailures = 0;
                _index = i;
                _entry = entry;
                _track = track;
                _logger.LogInformation("Playing {Path} on {Device}", path, remote.Name);
                Raise(TrackChanged, new TrackChangedEventArgs(track, i, entry));
                SetState(startPaused ? PlaybackState.Paused : PlaybackState.Playing);
                SaveSession();
                return;
            }
            if (StartPosition(entry, track, startAt) is { } start && source.CanSeek)
            {
                source.Seek(source.Format.TimeToFrames(start)); // PB-07: before the deck starts, so nothing plays from 0
            }
            try
            {
                try
                {
                    await StartSourceAsync(source, startPaused).ConfigureAwait(false);
                }
                catch (DopRefusedException)
                {
                    // FM-05: exclusive mode is not available: play the DSD file as PCM instead.
                    _logger.LogWarning("DoP needs exclusive mode, which the device refused; converting DSD to PCM");
                    source.Dispose();
                    source = await Task.Run(() => OpenSource(entry, allowDop: false)).ConfigureAwait(false);
                    track = BuildTrack(entry, source, metadata);
                    await StartSourceAsync(source, startPaused).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or AudioSourceException or IOException)
            {
                // The deck owns the source once loading starts; unloading disposes it.
                await _deck.UnloadAsync().ConfigureAwait(false);
                ReportError(path, ex);
                await StopCoreAsync().ConfigureAwait(false);
                return;
            }

            _index = i;
            _entry = entry;
            _track = track;
            _trackBeats = null; // XF: fetched again for the new track
            _trackBeatsCheckedAt = 0;
            _nextBeatsRequested = null;
            _preloadRetryAt = 0;
            _lastUnderruns = 0;
            if (_abLoop is not null)
            {
                _abLoop = null; // the deck was reloaded, so its loop is gone
                Raise(PlayModeChanged, EventArgs.Empty);
            }
            AttachStreamMetadata(metadata);
            _logger.LogInformation("Playing {Path} ({Codec}, {Rate} Hz, {Channels} ch) on {Device}",
                path, track.Stream.Codec, track.Stream.SampleRate, track.Stream.Channels, _output.DeviceName);
            Raise(TrackChanged, new TrackChangedEventArgs(track, i, entry));
            SetState(startPaused ? PlaybackState.Paused : PlaybackState.Playing);
            SaveSession();
            return;
        }
        await StopCoreAsync().ConfigureAwait(false);
    }

    private async Task StartSourceAsync(IAudioSource source, bool paused = false)
    {
        await CancelPreloadAsync().ConfigureAwait(false);
        var dop = IsDop(source);
        var bits = source.Info.IsLossless ? source.Info.BitsPerSample : 0;
        var (graphFormat, options) = dop ? ChooseDopOutput(source.Format) : ChooseOutput(source.Format, bits);
        if (!_output.IsOpen || _output.Format != graphFormat || !SameOutputMode(options))
        {
            await StopOutputAsync().ConfigureAwait(false);
            _graph.Configure(graphFormat);
            OpenOutput(graphFormat, options);
        }
        else
        {
            _output.SetDither(options.Dither);
        }
        if (dop && (!_output.IsExclusive || _graph.Format.SampleRate != source.Format.SampleRate))
        {
            throw new DopRefusedException();
        }
        if (source.Format.SampleRate != _graph.Format.SampleRate)
        {
            source = new ResamplingSource(source, _graph.Format.SampleRate); // PB-05: device cannot do the track's rate
        }
        SetBitPerfect(dop);
        _graph.Paused = paused;
        _graph.Fade.Snap(true); // a new track starts at full level (after a stop fade)
        _graph.Dsp.RequestReset(); // EQ-05: no reverb tail etc. from the previous track
        _sourceFormat = source.Format;
        _canSeek = source.CanSeek;
        await _deck.LoadAsync(source).ConfigureAwait(false);
        if (!_output.IsRunning)
        {
            StartOutput();
        }
    }

    /// <summary>FM-05: DoP is wanted for this file (Settings › Output: exclusive mode and DSD mode DoP).</summary>
    private bool WantDop(IAudioSourceFactory factory, string path)
    {
        var output = _settings.Current.Output;
        return output.Exclusive && output.DsdMode == DsdMode.Dop && factory is IDopSourceFactory dop && dop.IsDsd(path);
    }

    private bool DeviceSupportsDop(int rate)
    {
        var formats = _output.GetDeviceFormats(FindDevice(_settings.Current.Output.DeviceName));
        return formats.Count == 0 || formats.Any(f => f.SampleRate is 0 || f.SampleRate == rate) &&
            formats.Any(f => (f.SampleRate is 0 || f.SampleRate == rate) && f.SampleFormat is OutputSampleFormat.S24 or OutputSampleFormat.S32);
    }

    private static bool IsDop(IAudioSource source) => source.Info.Codec == "dop";

    private bool WantDopFor(PlaylistEntry entry) =>
        !entry.IsUrl && _factories.FirstOrDefault(f => f.CanOpen(entry.Path)) is { } factory && WantDop(factory, entry.Path);

    /// <summary>FM-05: exclusive mode could not be used for a DoP track (it then plays as PCM).</summary>
    private sealed class DopRefusedException() : Exception("DoP needs exclusive mode.");

    private bool SameOutputMode(OutputOptions wanted) =>
        _outputOptions is { } current && current.Exclusive == wanted.Exclusive && current.SampleFormat == wanted.SampleFormat;

    /// <summary>
    /// PB-04 / PB-05: the graph format and output options for a source. Shared mode: the source's rate, float.
    /// Exclusive mode: the source's rate if the device supports it (else the nearest higher one, or the highest, when
    /// resampling is allowed), and the smallest integer format that holds the source's bits (or the setting's).
    /// </summary>
    private (AudioFormat Format, OutputOptions Options) ChooseOutput(AudioFormat source, int sourceBits)
    {
        var output = _settings.Current.Output;
        var shared = (new AudioFormat(source.SampleRate, EngineChannels), new OutputOptions(output.BufferMilliseconds));
        if (!output.Exclusive)
        {
            return shared;
        }
        var formats = _output.GetDeviceFormats(FindDevice(output.DeviceName))
            .Where(f => f.Channels is 0 or EngineChannels && f.SampleFormat != OutputSampleFormat.F32)
            .ToList();
        var rate = source.SampleRate;
        if (formats.Count > 0 && !formats.Any(f => f.SampleRate is 0 || f.SampleRate == rate))
        {
            if (!output.AllowResampling)
            {
                _logger.LogWarning("The device has no exclusive format at {Rate} Hz and resampling is off; using shared mode", rate);
                return shared;
            }
            var rates = formats.Select(f => f.SampleRate).Distinct().Order().ToList();
            rate = rates.FirstOrDefault(r => r > rate, rates[^1]);
        }
        var wantedBits = output.BitDepth > 0 ? output.BitDepth : sourceBits > 0 ? sourceBits : 24;
        var candidates = formats.Where(f => f.SampleRate is 0 || f.SampleRate == rate)
            .Select(f => f.SampleFormat).Distinct()
            .OrderBy(f => SampleConverter.Bits(f)).ToList();
        var format = candidates.Count == 0
            ? wantedBits <= 16 ? OutputSampleFormat.S16 : wantedBits <= 24 ? OutputSampleFormat.S24 : OutputSampleFormat.S32
            : candidates.FirstOrDefault(f => SampleConverter.Bits(f) >= wantedBits, candidates[^1]);
        var outBits = SampleConverter.Bits(format);
        var dither = output.Dither && outBits < 32 && (sourceBits == 0 ? outBits <= 16 : outBits < sourceBits);
        return (new AudioFormat(rate, EngineChannels), new OutputOptions(output.BufferMilliseconds, Exclusive: true, format, dither));
    }

    /// <summary>FM-05: DoP goes out exclusively at its own rate, 24 bit (or 32), without dither.</summary>
    private (AudioFormat Format, OutputOptions Options) ChooseDopOutput(AudioFormat source)
    {
        var output = _settings.Current.Output;
        var formats = _output.GetDeviceFormats(FindDevice(output.DeviceName))
            .Where(f => (f.SampleRate is 0 || f.SampleRate == source.SampleRate) && f.SampleFormat is OutputSampleFormat.S24 or OutputSampleFormat.S32)
            .Select(f => f.SampleFormat).ToList();
        var format = formats.Count == 0 || formats.Contains(OutputSampleFormat.S24) ? OutputSampleFormat.S24 : OutputSampleFormat.S32;
        return (new AudioFormat(source.SampleRate, EngineChannels), new OutputOptions(output.BufferMilliseconds, Exclusive: true, format, Dither: false));
    }

    /// <summary>FM-05: while DoP plays, volume, ReplayGain, fades and speed are bypassed (mute still works).</summary>
    private void SetBitPerfect(bool on)
    {
        if (_bitPerfect == on)
        {
            return;
        }
        _bitPerfect = on;
        _graph.Dsp.Bypass = on; // EQ-05: no effects on DoP
        ApplyGain();
        _graph.Master.SnapToTarget();
        ApplySpeed();
        if (on)
        {
            _deck.Gain.TargetGain = 1f;
            _deck.Gain.SnapToTarget();
        }
    }

    private AudioDeviceInfo? FindDevice(string? name) =>
        name is null ? null : _output.GetDevices().FirstOrDefault(d => d.Name == name);

    private async Task StopCoreAsync()
    {
        SaveResumePosition(finished: _endedNaturally);
        _endedNaturally = false;
        SaveSession();
        if (_remote is { } remote)
        {
            try
            {
                await remote.StopAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Stopping a device that went away must still stop the player.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogDebug(ex, "Could not stop {Device}", remote.Name);
            }
            SetState(PlaybackState.Stopped);
            Raise(PositionChanged, new PlaybackPositionEventArgs(TimeSpan.Zero, Duration));
            return;
        }
        var fades = _settings.Current.Playback.Fades;
        await FadeOutAsync(fades.OnStop ? fades.StopMilliseconds : 0).ConfigureAwait(false);
        await CancelPreloadAsync().ConfigureAwait(false);
        await StopOutputAsync().ConfigureAwait(false);
        _graph.Paused = false;
        SetState(PlaybackState.Stopped);
        Raise(PositionChanged, new PlaybackPositionEventArgs(TimeSpan.Zero, Duration));
    }

    private async Task SeekCoreAsync(TimeSpan position)
    {
        if (_track is null || !_canSeek || _state is PlaybackState.Stopped)
        {
            return;
        }
        if (_remote is { } remote)
        {
            var target = position < TimeSpan.Zero ? TimeSpan.Zero : _track.Duration is { } length && position > length ? length : position;
            await remote.SeekAsync(target).ConfigureAwait(false);
            Raise(PositionChanged, new PlaybackPositionEventArgs(target, Duration));
            return;
        }
        var frame = _sourceFormat.TimeToFrames(position < TimeSpan.Zero ? TimeSpan.Zero : position);
        if (_track.Duration is { } duration)
        {
            frame = Math.Min(frame, _sourceFormat.TimeToFrames(duration));
        }
        if (_plan is not null || _fading is not null || _tempoReturn is not null)
        {
            await CancelPreloadAsync().ConfigureAwait(false); // XF: re-planned from the new position
        }
        var fades = _settings.Current.Playback.Fades;
        var fade = fades.OnSeek && _state == PlaybackState.Playing && !_bitPerfect ? fades.SeekMilliseconds : 0;
        await FadeOutAsync(fade).ConfigureAwait(false);
        await _deck.SeekAsync(frame).ConfigureAwait(false);
        _graph.Dsp.RequestReset(); // EQ-05: effect tails belong to the old position
        if (fade > 0)
        {
            _graph.Fade.FadeIn(fade);
        }
        Raise(PositionChanged, new PlaybackPositionEventArgs(Position, Duration));
    }

    private void ResumeCore()
    {
        var fades = _settings.Current.Playback.Fades;
        if (fades.OnPause && fades.PauseMilliseconds > 0 && !_bitPerfect)
        {
            _graph.Fade.FadeIn(fades.PauseMilliseconds, fromSilence: true); // PB-02: fade in on resume
            _graph.Paused = false;
        }
        else
        {
            _graph.Fade.Snap(true);
            _graph.Paused = false;
        }
        SetState(PlaybackState.Playing);
    }

    /// <summary>PB-02: fades the output out and waits until it is silent (no-op when not playing or 0 ms).</summary>
    private async Task FadeOutAsync(int milliseconds)
    {
        if (milliseconds <= 0 || _state != PlaybackState.Playing || !_output.IsRunning || _bitPerfect)
        {
            return;
        }
        _graph.Fade.FadeOut(Math.Clamp(milliseconds, 0, 2000));
        var deadline = Environment.TickCount64 + milliseconds + _settings.Current.Output.BufferMilliseconds + 500; // ends early when silent
        while (!_graph.Fade.IsSilent && Environment.TickCount64 < deadline)
        {
            await Task.Delay(5).ConfigureAwait(false);
        }
        // Let the device play out what is already in its buffer.
        await Task.Delay(_settings.Current.Output.BufferMilliseconds).ConfigureAwait(false);
    }

    private async Task StopOutputAsync()
    {
        if (_output.IsOpen)
        {
            _output.StopOutput();
        }
        _graph.OutputActive = false;
        await _deck.UnloadAsync().ConfigureAwait(false);
    }

    private void OpenOutput(AudioFormat format, OutputOptions options)
    {
        var output = _settings.Current.Output;
        var device = FindDevice(output.DeviceName);
        if (output.DeviceName is { } name && device is null)
        {
            _logger.LogWarning("Output device {Device} not found; using the default device", name);
        }
        try
        {
            _output.Open(device, format, options, _graph);
        }
        catch (InvalidOperationException ex) when (options.Exclusive)
        {
            // PB-04: exclusive mode refused (in use by another app, or the format): keep playing in shared mode.
            _logger.LogWarning(ex, "Exclusive mode is not available ({Format}, {Rate} Hz); using shared mode", options.SampleFormat, format.SampleRate);
            options = new OutputOptions(options.BufferMilliseconds);
            _output.Open(device, format, options, _graph);
        }
        catch (InvalidOperationException ex) when (device is not null)
        {
            _logger.LogWarning(ex, "Could not open {Device}; using the default device", device.Name);
            _output.Open(null, format, options, _graph);
        }
        _outputOptions = options;
        Raise(OutputChanged, EventArgs.Empty);
    }

    private void StartOutput()
    {
        _graph.OutputActive = true;
        _output.StartOutput();
    }

    private IAudioSource OpenSource(PlaylistEntry entry, bool allowDop = true)
    {
        var path = entry.Path;
        var factory = _factories.FirstOrDefault(f => f.CanOpen(path))
            ?? throw new AudioSourceException($"Unsupported file type '{System.IO.Path.GetExtension(path)}'.");
        if (!entry.IsUrl && !File.Exists(path))
        {
            entry.IsMissing = true;
            throw new AudioSourceException("File not found.");
        }
        if (allowDop && entry.Start is null && WantDop(factory, path))
        {
            var dop = ((IDopSourceFactory)factory).OpenDop(path);
            if (DeviceSupportsDop(dop.Format.SampleRate))
            {
                return dop;
            }
            dop.Dispose();
            _logger.LogInformation("The device has no 24/32-bit exclusive format at {Rate} Hz for DoP; converting DSD to PCM", dop.Format.SampleRate);
        }
        var source = factory.Open(path);
        if (entry.Start is not { } start)
        {
            return source;
        }
        try
        {
            return new RangeAudioSource(source, start, entry.End); // FM-09 virtual track
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    /// <summary>FM-10: follows the now-playing title of an internet radio stream.</summary>
    private void AttachStreamMetadata(IStreamMetadata? metadata)
    {
        DetachStreamMetadata();
        if (metadata is not null)
        {
            _streamMetadata = metadata;
            metadata.StreamTitleChanged += OnStreamTitleChanged;
        }
    }

    private void DetachStreamMetadata()
    {
        if (Interlocked.Exchange(ref _streamMetadata, null) is { } old)
        {
            old.StreamTitleChanged -= OnStreamTitleChanged;
        }
    }

    private void OnStreamTitleChanged(object? sender, string? title)
    {
        if (sender is not IStreamMetadata metadata || !ReferenceEquals(metadata, _streamMetadata) || _track is not { } track || _entry is not { } entry)
        {
            return;
        }
        var updated = track with { Title = StreamDisplayTitle(entry, metadata), Artist = metadata.StreamTitle is null ? null : metadata.StationName };
        if (updated.Title == track.Title)
        {
            return;
        }
        _track = updated;
        _logger.LogInformation("Stream title: {Title}", updated.Title);
        Raise(TrackChanged, new TrackChangedEventArgs(updated, _index, entry));
    }

    /// <summary>FM-10: "now playing" title, else the station name, else the playlist title, else the URL.</summary>
    private static string StreamDisplayTitle(PlaylistEntry entry, IStreamMetadata metadata) =>
        metadata.StreamTitle ?? metadata.StationName ?? (string.IsNullOrWhiteSpace(entry.Tags?.Title) ? entry.Path : entry.Tags.Title);

    /// <summary>Playlist/CUE tags win over the file's own tags (a CUE track has its own title and performer).</summary>
    private static TrackInfo BuildTrack(PlaylistEntry entry, IAudioSource source, IStreamMetadata? metadata)
    {
        if (metadata is not null)
        {
            return new TrackInfo(entry.Path, StreamDisplayTitle(entry, metadata), metadata.StreamTitle is null ? null : metadata.StationName,
                source.Info, null);
        }
        var tags = entry.Tags;
        var title = !string.IsNullOrWhiteSpace(tags?.Title) ? tags.Title
            : source.Tags.TryGetValue("title", out var t) && !string.IsNullOrWhiteSpace(t) ? t
            : entry.FileName;
        var artist = !string.IsNullOrWhiteSpace(tags?.Artist) ? tags.Artist : source.Tags.GetValueOrDefault("artist");
        TimeSpan? duration = source.LengthFrames is long frames ? source.Format.FramesToTime(frames) : null;
        return new TrackInfo(entry.Path, title, artist, source.Info, duration);
    }

    /// <summary>Where the current entry is now (it may have moved); falls back to the last known position.</summary>
    private int CurrentIndexCore()
    {
        var entries = _playlist.Entries;
        if (_entry is not null && entries.IndexOf(_entry) is var i and >= 0)
        {
            _index = i;
        }
        else
        {
            _index = Math.Min(_index, entries.Length - 1);
        }
        return _index;
    }

    /// <summary>CO-03: a new shuffle order whenever the playing playlist changed.</summary>
    private void SyncOrder(bool force = false)
    {
        var version = _playlist.Version;
        if (force || version != _orderVersion)
        {
            _playOrder.Reset(_playlist.Count, CurrentIndexCore());
            _orderVersion = version;
        }
    }
    // ---- background ---------------------------------------------------------------------------------

    private void Monitor()
    {
        if (_disposed)
        {
            return;
        }
        try
        {
            var state = _state;
            if (state is PlaybackState.Playing or PlaybackState.Paused)
            {
                Raise(PositionChanged, new PlaybackPositionEventArgs(Position, Duration));
                if (++_saveTicks >= 100) // PB-07: every 10 s
                {
                    _saveTicks = 0;
                    SaveSession();
                    SaveResumePosition(finished: false);
                }
            }

            if (_fading is { } fading && (fading.HasEnded || fading.EnvelopeFinished || !fading.IsActive) &&
                Interlocked.CompareExchange(ref _finishingFade, 1, 0) == 0)
            {
                _ = Task.Run(FinishFadeAsync); // XF: the outgoing track is silent now
            }
            FetchTrackBeats();

            var deck = _deck;
            var underruns = deck.Underruns;
            if (underruns > _lastUnderruns && state == PlaybackState.Playing)
            {
                _logger.LogWarning("Audio underrun: {Count} buffer(s) ran empty (total {Total}) on {Device}",
                    underruns - _lastUnderruns, underruns, _output.DeviceName);
            }
            _lastUnderruns = underruns;

            var ended = _remote is { } remotePlayer ? remotePlayer.HasEnded : deck.HasEnded;
            if (_graph.Handovers != _handoversSeen || (ended && state == PlaybackState.Playing && _graph.Next is null))
            {
                if (Interlocked.CompareExchange(ref _advancing, 1, 0) == 0)
                {
                    _ = Task.Run(AdvanceAfterEndAsync);
                }
            }
            else if (state == PlaybackState.Playing && ShouldPreload() && Interlocked.CompareExchange(ref _preloading, 1, 0) == 0)
            {
                _ = Task.Run(PreloadAsync);
            }
            else if (_preload is { } p && (p.Playlist.Version != p.PlaylistVersion || Queue.Version != p.QueueVersion))
            {
                _ = Task.Run(() => RunAsync(CancelPreloadAsync)); // the playlist changed: the next track may be another one
            }
        }
#pragma warning disable CA1031 // Timer callback: never let an exception escape.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Playback monitor failed");
        }
    }

    private async Task AdvanceAfterEndAsync()
    {
        try
        {
            await RunAsync(async () =>
            {
                if (await CommitHandoverAsync().ConfigureAwait(false))
                {
                    return;
                }
                var deck = _deck;
                var ended = _remote is { } remote ? remote.HasEnded : deck.HasEnded;
                if (!ended || _state != PlaybackState.Playing || _graph.Next is not null)
                {
                    return;
                }
                if (deck.Fault is { } fault && _track is not null)
                {
                    ReportError(_track.Path, fault);
                }
                _endedNaturally = true; // PB-07: a long file played to the end loses its resume position
                if (_stopAfterCurrent)
                {
                    _stopAfterCurrent = false; // PB-09: one-shot
                    Raise(PlayModeChanged, EventArgs.Empty);
                    await StopCoreAsync().ConfigureAwait(false);
                    TrackEndedStop?.Invoke(this, EventArgs.Empty);
                    return;
                }
                if (TakeFromQueue() is { } queued)
                {
                    await PlayIndexCoreAsync(queued).ConfigureAwait(false);
                    return;
                }
                SyncOrder();
                if (_playOrder.Next(CurrentIndexCore(), trackEnded: true) is int next)
                {
                    await PlayIndexCoreAsync(next).ConfigureAwait(false);
                }
                else
                {
                    await StopCoreAsync().ConfigureAwait(false);
                    TrackEndedStop?.Invoke(this, EventArgs.Empty);
                }
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
#pragma warning disable CA1031 // Background continuation: log, never crash the process.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Advancing to the next track failed");
        }
        finally
        {
            Volatile.Write(ref _advancing, 0);
        }
    }

    private void OnOutputLost(object? sender, AudioOutputLostEventArgs e) => _ = Task.Run(async () =>
    {
        try
        {
            await RunAsync(async () =>
            {
                _graph.OutputActive = false;
                if (_state == PlaybackState.Stopped || _output.Format is not { } format)
                {
                    return;
                }
                try
                {
                    // PB-04 fallback: keep playing on the system default device.
                    _output.Open(null, format, new OutputOptions(_settings.Current.Output.BufferMilliseconds), _graph);
                    _outputOptions = new OutputOptions(_settings.Current.Output.BufferMilliseconds);
                    StartOutput();
                    _logger.LogWarning("Output lost ({Reason}); continuing on the default device {Device}", e.Reason, _output.DeviceName);
                }
                catch (InvalidOperationException ex)
                {
                    ReportError(_track?.Path ?? string.Empty, ex);
                    await StopCoreAsync().ConfigureAwait(false);
                }
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
#pragma warning disable CA1031 // Background continuation: log, never crash the process.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Output fallback failed");
        }
    });

    // ---- PB-01 gapless -------------------------------------------------------------------------------

    /// <summary>Preloading starts this long before the end of the current track.</summary>
    private static readonly TimeSpan PreloadLead = TimeSpan.FromSeconds(10);

    private sealed record Preloaded(Playlist Playlist, int PlaylistVersion, int QueueVersion, PlaylistEntry Entry, int Index,
        TrackInfo Track, ReplayGainDecision? Gain, AudioFormat Format, bool CanSeek, BeatInfo? Beats = null);

    /// <summary>
    /// XF-01..XF-04: plans the transition to <paramref name="entry"/> (null when crossfading is off or it is too late:
    /// then the track follows gaplessly). The caller seeks the source to the incoming entry point for beat matching.
    /// <paramref name="startConsumed"/> is the outgoing deck's consumed-frame count at which the incoming deck starts.
    /// </summary>
    private CrossfadePlan? PlanCrossfade(PlaylistEntry entry, TrackInfo track, IAudioSource source, out long startConsumed, out BeatInfo? incomingBeats)
    {
        startConsumed = -1;
        incomingBeats = null;
        var xf = _settings.Current.Crossfading;
        if (!xf.Enabled || _track?.Duration is not { } outDuration || track.Duration is not { } inDuration || _entry is null)
        {
            return null;
        }
        if (xf.Mode == CrossfadeMode.BeatMatch && _beatProvider is not null)
        {
            try
            {
                incomingBeats = _beatProvider.TryGet(entry);
                _trackBeats ??= _beatProvider.TryGet(_entry);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Beat analysis not available");
            }
        }
        // Beat matching keeps the pitch; with "keep pitch" off and a speed other than 1.0 it would change it: Intro/Outro.
        var pitchOk = _keepPitch || Math.Abs(_speed - 1) < 1e-9;
        var outSpeed = CurrentDeckSpeed;
        var plan = CrossfadePlanner.Plan(xf,
            new CrossfadeTrack(outDuration.TotalSeconds, pitchOk ? _trackBeats : null, outSpeed),
            new CrossfadeTrack(inDuration.TotalSeconds, pitchOk ? incomingBeats : null, NormalSpeed),
            Position.TotalSeconds, source.CanSeek);
        var (consumed, position) = _deck.Snapshot();
        var mixFrame = _sourceFormat.TimeToFrames(TimeSpan.FromSeconds(plan.MixStart));
        startConsumed = consumed + (long)Math.Round((mixFrame - position) / outSpeed);
        if (startConsumed < consumed + (_graph.Format.SampleRate / 5))
        {
            _logger.LogInformation("Too late to crossfade into {Path}; it follows gaplessly", entry.Path);
            startConsumed = -1;
            return null;
        }
        return plan;
    }

    private bool ShouldPreload() =>
        _remote is null && _preload is null && _graph.Next is null && _fading is null && !_stopAfterCurrent && _abLoop?.B is null && !_bitPerfect &&
        _track?.Duration is { } duration && (_settings.Current.Playback.Gapless || _settings.Current.Crossfading.Enabled) &&
        Position >= PreloadAt(duration) && _entry is { IsUrl: false } && Environment.TickCount64 >= _preloadRetryAt;

    /// <summary>
    /// When to preload: 10 s before the end (gapless), or 10 s before the earliest mix start a crossfade may need
    /// (Intro/Outro: the overlap before the end; beat matching: before the beat end minus the mix setting).
    /// </summary>
    private TimeSpan PreloadAt(TimeSpan duration)
    {
        var xf = _settings.Current.Crossfading;
        var speed = CurrentDeckSpeed;
        var start = duration.TotalSeconds;
        if (xf.Enabled)
        {
            start -= Math.Clamp(xf.OverlapSeconds, 0.5, 30) * speed;
            if (xf.Mode == CrossfadeMode.BeatMatch && _trackBeats is { } beats)
            {
                var beatEnd = beats.BeatEnd > 0 ? beats.BeatEnd : duration.TotalSeconds;
                start = Math.Min(start, beatEnd - Math.Max(0, xf.MixBeforeBeatEndSeconds) - (BeatInfo.PhraseBeats * beats.Period));
            }
        }
        return TimeSpan.FromSeconds(Math.Max(0, start - (PreloadLead.TotalSeconds * speed)));
    }

    /// <summary>XF-02: there are more than 5 s left before an Intro/Outro mix would have to start.</summary>
    private bool StillTimeToWait()
    {
        if (_track?.Duration is not { } duration)
        {
            return false;
        }
        var ioStart = duration.TotalSeconds - (Math.Clamp(_settings.Current.Crossfading.OverlapSeconds, 0.5, 30) * CurrentDeckSpeed);
        return Position.TotalSeconds < ioStart - 5 - PreloadLead.TotalSeconds;
    }

    /// <summary>The current deck's speed (matched tempo, or the normal speed).</summary>
    private double CurrentDeckSpeed => _deckSpeedOverride ?? NormalSpeed;

    private double NormalSpeed => _bitPerfect ? 1.0 : _speed;

    /// <summary>AN-02: the current track's beats for beat matching (asked for every 2 s until known).</summary>
    private void FetchTrackBeats()
    {
        var xf = _settings.Current.Crossfading;
        if (_trackBeats is not null || !xf.Enabled || xf.Mode != CrossfadeMode.BeatMatch || _beatProvider is null ||
            _entry is not { IsUrl: false } entry || Environment.TickCount64 - _trackBeatsCheckedAt < 2000)
        {
            return;
        }
        _trackBeatsCheckedAt = Environment.TickCount64;
        try
        {
            _trackBeats = _beatProvider.TryGet(entry);
            // The next track's beats too, so they are ready when the mix is planned (TryGet queues the analysis).
            if (PeekNext() is { } next && next.Playlist.Entries[next.Index] is { IsUrl: false } nextEntry && !ReferenceEquals(nextEntry, _nextBeatsRequested))
            {
                _nextBeatsRequested = nextEntry;
                _ = _beatProvider.TryGet(nextEntry);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _logger.LogDebug(ex, "Beat analysis not available for {Path}", entry.Path);
        }
    }

    /// <summary>The entry that follows the current one at its end, without changing any state; null for none.</summary>
    private (Playlist Playlist, int Index)? PeekNext()
    {
        if (Queue.Peek() is { } queued)
        {
            return (queued.Playlist, queued.Index); // PB-09: the queue comes first
        }
        SyncOrder();
        return _playOrder.Next(CurrentIndexCore(), trackEnded: true) is int next ? (_playlist, next) : null;
    }

    /// <summary>PB-09: takes the next queued entry and makes its playlist the playing one; returns its index.</summary>
    private int? TakeFromQueue()
    {
        if (Queue.Dequeue() is not { } queued)
        {
            return null;
        }
        _playlist = queued.Playlist;
        _entry = queued.Entry;
        SyncOrder(force: true);
        return queued.Index;
    }

    /// <summary>True when the playback stops at the end of the current track (no gapless preload then).</summary>
    public bool WillStopAtEnd => _stopAfterCurrent || PeekNextForSleep() is null;

    private (Playlist, int)? PeekNextForSleep()
    {
        if (Queue.Peek() is { } queued)
        {
            return (queued.Playlist, queued.Index);
        }
        return _playOrder.Next(Math.Max(0, _index), trackEnded: true) is int next ? (_playlist, next) : null;
    }

    private async Task ApplyLoopAsync(TimeSpan a, TimeSpan b)
    {
        var duration = _track?.Duration ?? TimeSpan.Zero;
        if (b < a)
        {
            (a, b) = (b, a);
        }
        a = a < TimeSpan.Zero ? TimeSpan.Zero : a;
        if (b - a < MinimumLoop)
        {
            b = a + MinimumLoop;
        }
        if (b > duration)
        {
            b = duration;
            a = b - MinimumLoop < TimeSpan.Zero ? TimeSpan.Zero : (a > b - MinimumLoop ? b - MinimumLoop : a);
        }
        _abLoop = new AbLoop(a, b);
        await CancelPreloadAsync().ConfigureAwait(false);
        _deck.SetLoop(_sourceFormat.TimeToFrames(a), _sourceFormat.TimeToFrames(b));
        if (Position >= b || Position < a)
        {
            await _deck.SeekAsync(_sourceFormat.TimeToFrames(a)).ConfigureAwait(false);
        }
        Raise(PlayModeChanged, EventArgs.Empty);
    }

    private void ClearLoop()
    {
        _abLoop = null;
        _deck.ClearLoop();
    }

    /// <summary>PB-08: the shortest loop (longer than the deck's buffer, so the loop position mapping holds).</summary>
    private static readonly TimeSpan MinimumLoop = TimeSpan.FromSeconds(1);

    private async Task PreloadAsync()
    {
        try
        {
            await RunAsync(async () =>
            {
                if (_state != PlaybackState.Playing || !ShouldPreload() || PeekNext() is not { } next)
                {
                    return;
                }
                var entry = next.Playlist.Entries[next.Index];
                if (entry.IsUrl || ReferenceEquals(entry, _preloadSkipped) || WantDopFor(entry))
                {
                    return; // FM-05: a DoP track starts on its own (exclusive output at its rate)
                }
                IAudioSource source;
                try
                {
                    source = await Task.Run(() => OpenSource(entry, allowDop: false)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is AudioSourceException or IOException or UnauthorizedAccessException or NotSupportedException)
                {
                    _preloadSkipped = entry; // the normal end-of-track path reports it and skips on
                    return;
                }
                var track = BuildTrack(entry, source, null);
                if (StartPosition(entry, track, null) is { } resumeAt && source.CanSeek)
                {
                    source.Seek(source.Format.TimeToFrames(resumeAt));
                }
                if (source.Format.SampleRate != _graph.Format.SampleRate)
                {
                    var output = _settings.Current.Output;
                    if (output.MatchOutputRate || !output.AllowResampling)
                    {
                        source.Dispose(); // reopen the device at the new rate when the track starts (a short gap)
                        _preloadSkipped = entry;
                        return;
                    }
                    source = new ResamplingSource(source, _graph.Format.SampleRate);
                }
                ReplayGainDecision? gain = null;
                try
                {
                    gain = _gainProvider?.GetGain(next.Playlist, entry);
                }
#pragma warning disable CA1031 // ReplayGain is optional.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _logger.LogWarning(ex, "ReplayGain could not be determined for {Path}", entry.Path);
                }
                _spare.Gain.TargetGain = gain?.Linear ?? 1f;
                _spare.Gain.SnapToTarget();
                var crossfade = PlanCrossfade(entry, track, source, out var startConsumed, out var incomingBeats);
                if (crossfade is { Mode: CrossfadeMode.IntroOutro, Fallback: "beat analysis missing" } && StillTimeToWait())
                {
                    // XF-02: the analysis is queued; plan again in a second rather than giving up on beat matching.
                    source.Dispose();
                    _preloadRetryAt = Environment.TickCount64 + 1000;
                    return;
                }
                _preloadRetryAt = 0;
                if (crossfade is { Mode: CrossfadeMode.BeatMatch } bm)
                {
                    // XF-02: line the kicks up from the audio of the overlap itself, then seek to the entry point.
                    if (_crossfadeAligner is { } aligner && entry.Start is null && _entry is { Start: null } current && bm.OutgoingBpm is { } outBpm)
                    {
                        var request = new CrossfadeAlignment(current.Path, bm.MixStart, CurrentDeckSpeed, entry.Path, bm.IncomingStart, bm.IncomingSpeed,
                            bm.IncomingFadeLength, bm.Overlap, 60 / outBpm);
                        double? shift = null;
                        try
                        {
                            shift = await Task.Run(() => aligner.Refine(request)).ConfigureAwait(false);
                        }
#pragma warning disable CA1031 // Alignment is a refinement: without it the grids decide.
                        catch (Exception ex)
#pragma warning restore CA1031
                        {
                            _logger.LogWarning(ex, "Aligning the kicks failed; the beat grids decide");
                        }
                        if (shift is { } s)
                        {
                            crossfade = bm with { IncomingStart = Math.Max(0, bm.IncomingStart + s) };
                            _logger.LogInformation("Kicks aligned from the audio: incoming entry moved {Shift:+0.0;-0.0} ms", s * 1000);
                        }
                        if (startConsumed < _deck.Snapshot().Consumed + (_graph.Format.SampleRate / 5))
                        {
                            // The mix start passed while aligning: plan again (the next phrase).
                            source.Dispose();
                            _preloadRetryAt = Environment.TickCount64;
                            return;
                        }
                    }
                    source.Seek(source.Format.TimeToFrames(TimeSpan.FromSeconds(crossfade.IncomingStart)));
                }
                _spareSpeedOverride = crossfade is { Mode: CrossfadeMode.BeatMatch } ? crossfade.IncomingSpeed : null;
                ApplySpeed();
                await _spare.LoadAsync(source, armed: true).ConfigureAwait(false);
                _preload = new Preloaded(next.Playlist, next.Playlist.Version, Queue.Version, entry, next.Index, track, gain, source.Format, source.CanSeek,
                    incomingBeats);
                if (crossfade is not null)
                {
                    // XF-01/02: the envelopes (in each deck's own output frames) and the exact start of the incoming deck.
                    var rate = _graph.Format.SampleRate;
                    long Frames(double seconds) => (long)Math.Round(seconds * rate);
                    _deck.Envelope = new DeckEnvelope(startConsumed + Frames(crossfade.OutgoingFadeStart), Frames(crossfade.OutgoingFadeLength), crossfade.OutgoingShape);
                    _spare.Envelope = crossfade.IncomingFadeLength > 0 ? new DeckEnvelope(0, Frames(crossfade.IncomingFadeLength), EnvelopeShape.FadeIn) : null;
                    _spare.BassRamp = crossfade.BassFadeLength > 0 ? new BassRamp(0, Frames(crossfade.BassFadeLength)) : null;
                    _graph.NextStartFrame = startConsumed;
                    _plan = crossfade;
                    _logger.LogInformation("Crossfade planned ({Mode}) at {MixStart:0.00} s into {Path}{Fallback}", crossfade.Mode, crossfade.MixStart,
                        _entry?.Path, crossfade.Fallback is null ? string.Empty : $" (beat matching not used: {crossfade.Fallback})");
                }
                else
                {
                    _graph.NextStartFrame = -1;
                }
                _graph.Next = _spare;
                _logger.LogDebug("Preloaded {Path} for a {Kind} start", entry.Path, crossfade is null ? "gapless" : "crossfaded");
                if (crossfade is not null)
                {
                    Raise(PlayModeChanged, EventArgs.Empty);
                }
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
#pragma warning disable CA1031 // Background work: log, never crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Preloading the next track failed");
        }
        finally
        {
            Volatile.Write(ref _preloading, 0);
        }
    }

    /// <summary>Drops a preloaded track (explicit play, stop, playlist change). Inside the gate.</summary>
    private async Task CancelPreloadAsync()
    {
        _graph.Next = null;
        _graph.NextStartFrame = -1;
        var hadCrossfade = _plan is not null || _fading is not null || _tempoReturn is not null;
        _deck.Envelope = null; // XF: no scheduled fade-out without the incoming track
        _plan = null;
        _fading = null;        // XF: an outgoing deck still sounding is stopped (it is the spare deck)
        _mixing = null;
        _spareSpeedOverride = null;
        StopTempoReturn(snap: true);
        if (_preload is not null || _spare.IsActive)
        {
            _preload = null;
            await _spare.UnloadAsync().ConfigureAwait(false);
        }
        _preloadSkipped = null;
        ApplySpeed();
        if (hadCrossfade)
        {
            Raise(PlayModeChanged, EventArgs.Empty);
        }
    }

    /// <summary>XF: the outgoing deck has faded out: unload it, then start the tempo return (XF-04).</summary>
    private async Task FinishFadeAsync()
    {
        try
        {
            await RunAsync(async () =>
            {
                if (_fading is not { } fading)
                {
                    return;
                }
                _fading = null;
                if (ReferenceEquals(fading, _spare))
                {
                    await _spare.UnloadAsync().ConfigureAwait(false);
                }
                _spareSpeedOverride = null;
                if (_mixing is { TempoReturn: { } mode } mixing && _deckSpeedOverride is { } from)
                {
                    _tempoReturn = new TempoReturnState(mode, from, NormalSpeed, mixing.TempoReturnSeconds, mixing.TempoReturnAt);
                    _tempoTimer.Change(TempoTickInterval, TempoTickInterval);
                }
                else
                {
                    _mixing = null;
                }
                Raise(PlayModeChanged, EventArgs.Empty);
            }).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
        }
#pragma warning disable CA1031 // Background continuation: log, never crash the process.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Finishing a crossfade failed");
        }
        finally
        {
            Volatile.Write(ref _finishingFade, 0);
        }
    }

    /// <summary>XF-04: the tempo return runs in 20 ms steps.</summary>
    private static readonly TimeSpan TempoTickInterval = TimeSpan.FromMilliseconds(20);

    private void TempoTick()
    {
        if (_disposed || !_gate.Wait(0))
        {
            return; // busy: next tick
        }
        try
        {
            if (_tempoReturn is not { } r || _state != PlaybackState.Playing)
            {
                return;
            }
            if (r.Mode == TempoReturnMode.CutAtNextBreak)
            {
                if (r.At is { } at && Position.TotalSeconds >= at)
                {
                    StopTempoReturn(snap: true); // XF-04: jump back at the break
                    Raise(PlayModeChanged, EventArgs.Empty);
                }
                return;
            }
            r.Elapsed += TempoTickInterval.TotalSeconds;
            var progress = Math.Min(1, r.Elapsed / r.Seconds);
            if (progress >= 1)
            {
                StopTempoReturn(snap: true);
                Raise(PlayModeChanged, EventArgs.Empty);
                return;
            }
            _deckSpeedOverride = r.From + ((r.To - r.From) * progress);
            ApplySpeed();
            if ((int)(r.Elapsed / TempoTickInterval.TotalSeconds) % 25 == 0)
            {
                Raise(PlayModeChanged, EventArgs.Empty); // BPM readout, twice a second
            }
        }
#pragma warning disable CA1031 // Timer callback: never let an exception escape.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Tempo return failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>XF-04: ends the tempo return (with <paramref name="snap"/>, the current deck goes to its normal speed).</summary>
    private void StopTempoReturn(bool snap)
    {
        _tempoTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_tempoReturn is null && !snap)
        {
            return;
        }
        _tempoReturn = null;
        if (snap)
        {
            _deckSpeedOverride = null;
            if (_fading is null)
            {
                _mixing = null;
            }
            ApplySpeed();
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Group != SettingsGroup.Crossfading)
        {
            return;
        }
        // XF: a planned transition follows the new settings (re-planned on the next monitor tick).
        _ = Task.Run(() => RunAsync(async () =>
        {
            if (_plan is not null || (_preload is not null && _graph.Next is not null))
            {
                await CancelPreloadAsync().ConfigureAwait(false);
            }
        }));
    }

    /// <summary>After the mixer started the armed deck: make it the current one. Inside the gate.</summary>
    private async Task<bool> CommitHandoverAsync()
    {
        var handovers = _graph.Handovers;
        if (handovers == _handoversSeen)
        {
            return false;
        }
        _handoversSeen = handovers;
        if (_preload is not { } p)
        {
            return false;
        }
        var finished = _deck;
        if (finished.Fault is { } fault && _track is not null)
        {
            ReportError(_track.Path, fault);
        }
        _endedNaturally = true;
        SaveResumePosition(finished: true);
        _endedNaturally = false;
        (_deck, _spare) = (_spare, _deck);
        (_deckSpeedOverride, _spareSpeedOverride) = (_spareSpeedOverride, _deckSpeedOverride);
        var crossfade = _plan;
        _plan = null;
        _trackBeats = p.Beats;
        _trackBeatsCheckedAt = 0;
        _preload = null;
        if (Queue.Peek() is { } queued && ReferenceEquals(queued.Entry, p.Entry))
        {
            Queue.Dequeue(); // PB-09: the queued entry is playing now
        }
        var newPlaylist = !ReferenceEquals(_playlist, p.Playlist);
        var previousPath = _entry?.Path;
        _preloadSkipped = null;
        _playlist = p.Playlist;
        _entry = p.Entry;
        _index = p.Index;
        if (newPlaylist)
        {
            SyncOrder(force: true);
        }
        _track = p.Track;
        _sourceFormat = p.Format;
        _canSeek = p.CanSeek;
        _lastUnderruns = 0;
        DetachStreamMetadata();
        if (p.Gain != _replayGain)
        {
            _replayGain = p.Gain;
            Raise(ReplayGainChanged, EventArgs.Empty);
        }
        if (crossfade is not null && finished.IsActive)
        {
            // XF: the outgoing track keeps playing under its envelope; the monitor unloads it once it is silent.
            _fadingPath = previousPath;
            _fading = finished;
            _mixing = crossfade;
            _logger.LogInformation("Crossfade ({Mode}): playing {Path}", crossfade.Mode, p.Entry.Path);
            Raise(PlayModeChanged, EventArgs.Empty);
        }
        else
        {
            await finished.UnloadAsync().ConfigureAwait(false);
            _spareSpeedOverride = null;
            _logger.LogInformation("Gapless: playing {Path}", p.Entry.Path);
        }
        if (_deckSpeedOverride is null)
        {
            ApplyFileSpeed(p.Entry); // a matched tempo wins until the tempo return
        }
        SaveSession();
        Raise(TrackChanged, new TrackChangedEventArgs(p.Track, p.Index, p.Entry));
        return true;
    }

    // ---- PB-06 speed, PB-07 resume -----------------------------------------------------------------------

    private void ApplySpeed()
    {
        var speed = NormalSpeed; // FM-05: DoP must not be time-stretched
        // XF-02: a beat-matched deck plays at its matched tempo, always keeping the pitch.
        _deck.SetSpeed(_deckSpeedOverride ?? speed, _deckSpeedOverride is null ? _keepPitch : true);
        _spare.SetSpeed(_spareSpeedOverride ?? speed, _spareSpeedOverride is null ? _keepPitch : true);
    }

    /// <summary>PB-06: with "remember speed per file", a file starts at its saved speed (or 1.0).</summary>
    private void ApplyFileSpeed(PlaylistEntry entry)
    {
        if (!_settings.Current.Playback.RememberSpeedPerFile || entry.IsUrl)
        {
            return;
        }
        var speed = _resume.GetSpeed(entry.Path) ?? 1.0;
        if (Math.Abs(speed - _speed) > 1e-6)
        {
            _speed = Math.Clamp(speed, 0.5, 2.0);
            ApplySpeed();
            Raise(SpeedChanged, EventArgs.Empty);
        }
    }

    /// <summary>PB-07: long files (per-file resume on) have their position remembered.</summary>
    private bool IsLongFile(PlaylistEntry? entry, TrackInfo? track)
    {
        var options = _settings.Current.Playback;
        return options.ResumeLongFiles && entry is { IsUrl: false, IsVirtual: false } && track?.Duration is { } duration &&
            duration >= TimeSpan.FromMinutes(Math.Max(1, options.ResumeMinimumMinutes));
    }

    /// <summary>Where a track starts: an explicit position (session resume), a long file's saved position, or 0 (null).</summary>
    private TimeSpan? StartPosition(PlaylistEntry entry, TrackInfo track, TimeSpan? explicitStart)
    {
        if (explicitStart is { } start && start > TimeSpan.Zero)
        {
            return track.Duration is { } d && start >= d ? null : start;
        }
        if (IsLongFile(entry, track) && _resume.GetPosition(entry.Path) is { } saved && saved < track.Duration!.Value - ResumeEndMargin)
        {
            _logger.LogInformation("Resuming {Path} at {Position}", entry.Path, saved);
            return saved;
        }
        return null;
    }

    /// <summary>Saves (or, when it was played to the end, clears) the current long file's position.</summary>
    private void SaveResumePosition(bool finished)
    {
        var (entry, track) = (_entry, _track);
        if (!IsLongFile(entry, track))
        {
            return;
        }
        try
        {
            var position = Position;
            if (finished || position >= track!.Duration!.Value - ResumeEndMargin)
            {
                _resume.ClearPosition(entry!.Path);
            }
            else if (position > ResumeEndMargin)
            {
                _resume.SetPosition(entry!.Path, position);
            }
        }
#pragma warning disable CA1031 // Saving the position is best effort.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Could not save the resume position");
        }
    }

    /// <summary>PB-07: where playback is, for the next start.</summary>
    private void SaveSession()
    {
        if (_entry is not { } entry)
        {
            return;
        }
        var session = new SessionState(_playlist.Id.ToString(), Math.Max(0, _index), entry.Path,
            Math.Round(Position.TotalSeconds, 1), _state == PlaybackState.Playing);
        if (_settings.Current.Playback.LastSession != session)
        {
            _settings.Update(SettingsGroup.Playback, s => s.Playback.LastSession = session);
        }
    }

    private static readonly TimeSpan ResumeEndMargin = TimeSpan.FromSeconds(10);

    // ---- helpers ------------------------------------------------------------------------------------

    private async Task RunAsync(Func<Task> command)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await command().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private void ApplyGain() => _graph.Master.TargetGain = _muted ? 0f : _bitPerfect || _volumeOnDevice ? 1f : GainStage.VolumeToGain(_volume);

    /// <summary>PB-03: asks the provider and sets deck A's gain (ramped, 10 ms).</summary>
    private void ApplyReplayGain(PlaylistEntry entry)
    {
        ReplayGainDecision? decision = null;
        try
        {
            decision = WantDopFor(entry) ? null : _gainProvider?.GetGain(_playlist, entry); // FM-05: no gain on DoP
        }
#pragma warning disable CA1031 // ReplayGain is optional: a failure plays the track unchanged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "ReplayGain could not be determined for {Path}", entry.Path);
        }
        _deck.Gain.TargetGain = decision?.Linear ?? 1f;
        if (decision != _replayGain)
        {
            _replayGain = decision;
            if (decision is not null)
            {
                _logger.LogInformation("ReplayGain {Gain:+0.00;-0.00} dB ({Source}, linear {Linear:0.000})", decision.GainDb, decision.Source, decision.Linear);
            }
            Raise(ReplayGainChanged, EventArgs.Empty);
        }
    }

    private void OnGainChanged(object? sender, ReplayGainChangedEventArgs e)
    {
        if (_disposed || _entry is not { } entry || (e.Path is not null && !string.Equals(e.Path, entry.Path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(() =>
                {
                    if (ReferenceEquals(_entry, entry) && _state != PlaybackState.Stopped)
                    {
                        ApplyReplayGain(entry);
                    }
                    return Task.CompletedTask;
                }).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
            }
        });
    }

    private void ScheduleSave()
    {
        if (!_disposed)
        {
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void SavePlayMode()
    {
        var (shuffle, repeat) = (_playOrder.Shuffle, _playOrder.Repeat);
        _settings.Update(SettingsGroup.Playback, s =>
        {
            s.Playback.Shuffle = shuffle;
            s.Playback.Repeat = repeat;
        });
        Raise(PlayModeChanged, EventArgs.Empty);
    }

    private void SavePlaybackState()
    {
        var (volume, muted) = (_volume, _muted);
        var current = _settings.Current.Playback;
        if (current.Volume != volume || current.Muted != muted)
        {
            _settings.Update(SettingsGroup.Playback, s =>
            {
                s.Playback.Volume = volume;
                s.Playback.Muted = muted;
            });
        }
    }

    private void SetState(PlaybackState state)
    {
        if (_state == state)
        {
            return;
        }
        _state = state;
        Raise(StateChanged, new PlaybackStateEventArgs(state));
    }

    private void ReportError(string path, Exception ex)
    {
        _logger.LogWarning(ex, "Cannot play {Path}", path);
        Raise(PlaybackError, new PlaybackErrorEventArgs(path, ex.Message));
    }

    private void Raise<T>(EventHandler<T>? handler, T args)
    {
        if (handler is null)
        {
            return;
        }
        try
        {
            handler(this, args);
        }
#pragma warning disable CA1031 // A failing subscriber must not break playback.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Playback event handler failed");
        }
    }
}
