using System.Runtime;
using AudioTastic.Core.Audio;
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

    private readonly IReadOnlyList<IAudioSourceFactory> _factories;
    private readonly IAudioOutput _output;
    private readonly SettingsStore _settings;
    private readonly ILogger<PlaybackEngine> _logger;
    private readonly MixerGraph _graph;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Timer _monitor;
    private readonly Timer _saveTimer;

    private string[] _tracks = [];
    private int _index = -1;
    private volatile PlaybackState _state = PlaybackState.Stopped;
    private TrackInfo? _track;
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
        _factories = [.. factories];
        _output = output ?? throw new ArgumentNullException(nameof(output));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _graph = new MixerGraph(new AudioFormat(48_000, EngineChannels));
        _volume = Math.Clamp(settings.Current.Playback.Volume, 0, 100);
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
    public IReadOnlyList<string> Tracks => _tracks;
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

    public Task OpenAsync(IEnumerable<string> paths, bool play = true)
    {
        ArgumentNullException.ThrowIfNull(paths);
        var list = paths.ToArray();
        return RunAsync(async () =>
        {
            _tracks = list;
            _index = list.Length > 0 ? 0 : -1;
            if (play && _index >= 0)
            {
                await PlayIndexCoreAsync(_index).ConfigureAwait(false);
            }
            else
            {
                await StopCoreAsync().ConfigureAwait(false);
                _track = null;
                Raise(TrackChanged, new TrackChangedEventArgs(null, _index));
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
            case PlaybackState.Stopped when _index >= 0:
                await PlayIndexCoreAsync(_index).ConfigureAwait(false);
                break;
        }
    });

    public Task PlayIndexAsync(int index) => RunAsync(() =>
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, _tracks.Length);
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
        if (active && (Position > RestartThreshold || _index <= 0))
        {
            await SeekCoreAsync(TimeSpan.Zero).ConfigureAwait(false);
        }
        else if (_index > 0)
        {
            await GoToAsync(_index - 1, active).ConfigureAwait(false);
        }
    });

    public Task NextAsync() => RunAsync(async () =>
    {
        if (_index + 1 < _tracks.Length)
        {
            await GoToAsync(_index + 1, _state is PlaybackState.Playing or PlaybackState.Paused).ConfigureAwait(false);
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
        _index = index;
        _track = null;
        Raise(TrackChanged, new TrackChangedEventArgs(null, index));
    }

    /// <summary>Plays the track at <paramref name="index"/>; unplayable files are reported and skipped.</summary>
    private async Task PlayIndexCoreAsync(int index)
    {
        for (var i = index; i < _tracks.Length; i++)
        {
            var path = _tracks[i];
            SetState(PlaybackState.Buffering);
            IAudioSource source;
            try
            {
                source = await Task.Run(() => OpenSource(path)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AudioSourceException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                ReportError(path, ex);
                continue;
            }

            var track = BuildTrack(path, source);
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
            _track = track;
            _lastUnderruns = 0;
            _logger.LogInformation("Playing {Path} ({Codec}, {Rate} Hz, {Channels} ch) on {Device}",
                path, track.Stream.Codec, track.Stream.SampleRate, track.Stream.Channels, _output.DeviceName);
            Raise(TrackChanged, new TrackChangedEventArgs(track, i));
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

    private IAudioSource OpenSource(string path)
    {
        var factory = _factories.FirstOrDefault(f => f.CanOpen(path))
            ?? throw new AudioSourceException($"Unsupported file type '{System.IO.Path.GetExtension(path)}'.");
        if (!File.Exists(path))
        {
            throw new AudioSourceException("File not found.");
        }
        return factory.Open(path);
    }

    private static TrackInfo BuildTrack(string path, IAudioSource source)
    {
        var title = source.Tags.TryGetValue("title", out var t) && !string.IsNullOrWhiteSpace(t)
            ? t
            : System.IO.Path.GetFileNameWithoutExtension(path);
        source.Tags.TryGetValue("artist", out var artist);
        TimeSpan? duration = source.LengthFrames is long frames ? source.Format.FramesToTime(frames) : null;
        return new TrackInfo(path, title, artist, source.Info, duration);
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
                if (_index + 1 < _tracks.Length)
                {
                    await PlayIndexCoreAsync(_index + 1).ConfigureAwait(false);
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

    private void ScheduleSave()
    {
        if (!_disposed)
        {
            _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
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
