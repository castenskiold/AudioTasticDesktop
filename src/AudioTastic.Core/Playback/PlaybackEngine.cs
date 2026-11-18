using System.Runtime;
using AudioTastic.Core.Audio;
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
/// the next track when a deck ends, and reports decoder faults. In P1 the engine runs at the sample rate of the
/// current file (the output is reopened when the rate changes); per-deck resampling for gapless and crossfading
/// across rates comes with PB-01 in P6.
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
        ILogger<PlaybackEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(factories);
        _factories = factories; // PG-01: may be a live view of the enabled input plugins; enumerated per open
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _graph = new MixerGraph(new AudioFormat(48_000, EngineChannels));
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
        : _sourceFormat.FramesToTime(_graph.DeckA.PositionFrames);

    public TimeSpan? Duration => _track?.Duration;

    /// <summary>Total underruns on the active deck since the current track was loaded.</summary>
    public long Underruns => _graph.DeckA.Underruns;

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
                Resume();
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

    public Task PauseAsync() => RunAsync(() =>
    {
        if (_state == PlaybackState.Playing)
        {
            _graph.Paused = true;
            SetState(PlaybackState.Paused);
        }
        else if (_state == PlaybackState.Paused)
        {
            Resume();
        }
        return Task.CompletedTask;
    });

    public Task StopAsync() => RunAsync(StopCoreAsync);

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
        SyncOrder();
        if (_playOrder.Next(CurrentIndexCore(), trackEnded: false) is int next)
        {
            await GoToAsync(next, _state is PlaybackState.Playing or PlaybackState.Paused).ConfigureAwait(false);
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
            OpenOutput(format);
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
            try
            {
                await StartSourceAsync(source).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or AudioSourceException or IOException)
            {
                // The deck owns the source once loading starts; unloading disposes it.
                await _graph.DeckA.UnloadAsync().ConfigureAwait(false);
                ReportError(path, ex);
                await StopCoreAsync().ConfigureAwait(false);
                return;
            }

            _index = i;
            _entry = entry;
            _track = track;
            _lastUnderruns = 0;
            AttachStreamMetadata(metadata);
            _logger.LogInformation("Playing {Path} ({Codec}, {Rate} Hz, {Channels} ch) on {Device}",
                path, track.Stream.Codec, track.Stream.SampleRate, track.Stream.Channels, _output.DeviceName);
            Raise(TrackChanged, new TrackChangedEventArgs(track, i, entry));
            SetState(PlaybackState.Playing);
            return;
        }
        await StopCoreAsync().ConfigureAwait(false);
    }

    private async Task StartSourceAsync(IAudioSource source)
    {
        var engineFormat = new AudioFormat(source.Format.SampleRate, EngineChannels);
        if (!_output.IsOpen || _output.Format != engineFormat)
        {
            await StopOutputAsync().ConfigureAwait(false);
            _graph.Configure(engineFormat);
            OpenOutput(engineFormat);
        }
        _graph.Paused = false;
        _sourceFormat = source.Format;
        _canSeek = source.CanSeek;
        await _graph.DeckA.LoadAsync(source).ConfigureAwait(false);
        if (!_output.IsRunning)
        {
            StartOutput();
        }
    }

    private async Task StopCoreAsync()
    {
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
        await _graph.DeckA.SeekAsync(frame).ConfigureAwait(false);
        Raise(PositionChanged, new PlaybackPositionEventArgs(Position, Duration));
    }

    private void Resume()
    {
        _graph.Paused = false;
        SetState(PlaybackState.Playing);
    }

    private async Task StopOutputAsync()
    {
        if (_output.IsOpen)
        {
            _output.StopOutput();
        }
        _graph.OutputActive = false;
        await _graph.DeckA.UnloadAsync().ConfigureAwait(false);
    }

    private void OpenOutput(AudioFormat format)
    {
        var output = _settings.Current.Output;
        AudioDeviceInfo? device = null;
        if (output.DeviceName is { } name)
        {
            device = _output.GetDevices().FirstOrDefault(d => d.Name == name);
            if (device is null)
            {
                _logger.LogWarning("Output device {Device} not found; using the default device", name);
            }
        }
        try
        {
            _output.Open(device, format, output.BufferMilliseconds, _graph);
        }
        catch (InvalidOperationException ex) when (device is not null)
        {
            _logger.LogWarning(ex, "Could not open {Device}; using the default device", device.Name);
            _output.Open(null, format, output.BufferMilliseconds, _graph);
        }
    }

    private void StartOutput()
    {
        _graph.OutputActive = true;
        _output.StartOutput();
    }

    private IAudioSource OpenSource(PlaylistEntry entry)
    {
        var path = entry.Path;
        var factory = _factories.FirstOrDefault(f => f.CanOpen(path))
            ?? throw new AudioSourceException($"Unsupported file type '{System.IO.Path.GetExtension(path)}'.");
        if (!entry.IsUrl && !File.Exists(path))
        {
            entry.IsMissing = true;
            throw new AudioSourceException("File not found.");
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
            }

            var deck = _graph.DeckA;
            var underruns = deck.Underruns;
            if (underruns > _lastUnderruns && state == PlaybackState.Playing)
            {
                _logger.LogWarning("Audio underrun: {Count} buffer(s) ran empty (total {Total}) on {Device}",
                    underruns - _lastUnderruns, underruns, _output.DeviceName);
            }
            _lastUnderruns = underruns;

            if (deck.HasEnded && state == PlaybackState.Playing && Interlocked.CompareExchange(ref _advancing, 1, 0) == 0)
            {
                _ = Task.Run(AdvanceAfterEndAsync);
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
                var deck = _graph.DeckA;
                if (!deck.HasEnded || _state != PlaybackState.Playing)
                {
                    return;
                }
                if (deck.Fault is { } fault && _track is not null)
                {
                    ReportError(_track.Path, fault);
                }
                SyncOrder();
                if (_playOrder.Next(CurrentIndexCore(), trackEnded: true) is int next)
                {
                    await PlayIndexCoreAsync(next).ConfigureAwait(false);
                }
                else
                {
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
                    _output.Open(null, format, _settings.Current.Output.BufferMilliseconds, _graph);
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

    private void ApplyGain() => _graph.Master.TargetGain = _muted ? 0f : GainStage.VolumeToGain(_volume);

    /// <summary>PB-03: asks the provider and sets deck A's gain (ramped, 10 ms).</summary>
    private void ApplyReplayGain(PlaylistEntry entry)
    {
        ReplayGainDecision? decision = null;
        try
        {
            decision = _gainProvider?.GetGain(_playlist, entry);
        }
#pragma warning disable CA1031 // ReplayGain is optional: a failure plays the track unchanged.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "ReplayGain could not be determined for {Path}", entry.Path);
        }
        _graph.DeckA.Gain.TargetGain = decision?.Linear ?? 1f;
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
