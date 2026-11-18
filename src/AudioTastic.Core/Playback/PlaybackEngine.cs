using System.Runtime;
using AudioTastic.Core.Audio;
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
    public string? OutputDeviceName => _output.IsOpen ? _output.DeviceName : null;

    public TimeSpan Position => _state == PlaybackState.Stopped || _track is null
        ? TimeSpan.Zero
        : _sourceFormat.FramesToTime(_deck.PositionFrames);

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

    public Task PlayAsync() => RunAsync(async () =>
    {
        switch (_state)
        {
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
        return Task.CompletedTask;
    });

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _monitor.Dispose();
        _saveTimer.Dispose();
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
        var frame = _sourceFormat.TimeToFrames(position < TimeSpan.Zero ? TimeSpan.Zero : position);
        if (_track.Duration is { } duration)
        {
            frame = Math.Min(frame, _sourceFormat.TimeToFrames(duration));
        }
        var fades = _settings.Current.Playback.Fades;
        var fade = fades.OnSeek && _state == PlaybackState.Playing && !_bitPerfect ? fades.SeekMilliseconds : 0;
        await FadeOutAsync(fade).ConfigureAwait(false);
        await _deck.SeekAsync(frame).ConfigureAwait(false);
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

            var deck = _deck;
            var underruns = deck.Underruns;
            if (underruns > _lastUnderruns && state == PlaybackState.Playing)
            {
                _logger.LogWarning("Audio underrun: {Count} buffer(s) ran empty (total {Total}) on {Device}",
                    underruns - _lastUnderruns, underruns, _output.DeviceName);
            }
            _lastUnderruns = underruns;

            if (_graph.Handovers != _handoversSeen || (deck.HasEnded && state == PlaybackState.Playing && _graph.Next is null))
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
                if (!deck.HasEnded || _state != PlaybackState.Playing || _graph.Next is not null)
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
        TrackInfo Track, ReplayGainDecision? Gain, AudioFormat Format, bool CanSeek);

    private bool ShouldPreload() =>
        _preload is null && _graph.Next is null && !_stopAfterCurrent && _abLoop?.B is null && !_bitPerfect &&
        _track?.Duration is { } duration && _settings.Current.Playback.Gapless &&
        duration - Position <= PreloadLead && _entry is { IsUrl: false };

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
                await _spare.LoadAsync(source, armed: true).ConfigureAwait(false);
                _preload = new Preloaded(next.Playlist, next.Playlist.Version, Queue.Version, entry, next.Index, track, gain, source.Format, source.CanSeek);
                _graph.Next = _spare;
                _logger.LogDebug("Preloaded {Path} for a gapless start", entry.Path);
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
        if (_preload is not null || _spare.IsActive)
        {
            _preload = null;
            await _spare.UnloadAsync().ConfigureAwait(false);
        }
        _preloadSkipped = null;
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
        _preload = null;
        if (Queue.Peek() is { } queued && ReferenceEquals(queued.Entry, p.Entry))
        {
            Queue.Dequeue(); // PB-09: the queued entry is playing now
        }
        var newPlaylist = !ReferenceEquals(_playlist, p.Playlist);
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
        await finished.UnloadAsync().ConfigureAwait(false);
        ApplyFileSpeed(p.Entry);
        SaveSession();
        _logger.LogInformation("Gapless: playing {Path}", p.Entry.Path);
        Raise(TrackChanged, new TrackChangedEventArgs(p.Track, p.Index, p.Entry));
        return true;
    }

    // ---- PB-06 speed, PB-07 resume -----------------------------------------------------------------------

    private void ApplySpeed()
    {
        var speed = _bitPerfect ? 1.0 : _speed; // FM-05: DoP must not be time-stretched
        _deck.SetSpeed(speed, _keepPitch);
        _spare.SetSpeed(speed, _keepPitch);
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

    private void ApplyGain() => _graph.Master.TargetGain = _muted ? 0f : _bitPerfect ? 1f : GainStage.VolumeToGain(_volume);

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
