using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Core.Playback;

/// <summary>PB-10: when the sleep timer stops playback.</summary>
public enum SleepMode
{
    /// <summary>After a set time (15, 30, 45, 60, 90 minutes or custom).</summary>
    AfterTime,
    EndOfTrack,
    EndOfPlaylist,
}

/// <summary>
/// PB-10: stops playback after a time or at the end of the current track or playlist, optionally fading out over the
/// last seconds (Settings › Playback › sleep timer), and optionally asks the app to close (<see cref="Finished"/>).
/// Checks once a second with a <see cref="PeriodicTimer"/>. Events are raised on background threads.
/// </summary>
public sealed class SleepTimerService : IDisposable
{
    private readonly PlaybackEngine _engine;
    private readonly SettingsStore _settings;
    private readonly ILogger<SleepTimerService> _logger;
    private readonly Lock _lock = new();
    private CancellationTokenSource? _run;
    private DateTime _deadline;
    private bool _fading;
    private bool _setStopAfterCurrent;

    public SleepTimerService(PlaybackEngine engine, SettingsStore settings, ILogger<SleepTimerService> logger)
    {
        _engine = engine;
        _settings = settings;
        _logger = logger;
        _engine.TrackEndedStop += OnTrackEndedStop;
    }

    /// <summary>The active mode; null when the timer is off.</summary>
    public SleepMode? Mode { get; private set; }

    /// <summary>Time left (after-time mode, or the rest of the current track); null when unknown or off.</summary>
    public TimeSpan? Remaining => Mode switch
    {
        SleepMode.AfterTime => _deadline - DateTime.UtcNow is var left && left > TimeSpan.Zero ? left : TimeSpan.Zero,
        SleepMode.EndOfTrack or SleepMode.EndOfPlaylist when _engine.Duration is { } d => d - _engine.Position,
        _ => null,
    };

    /// <summary>The mode or the remaining time changed (about once a second while running).</summary>
    public event EventHandler? Changed;

    /// <summary>Playback was stopped by the timer. <see cref="CloseApp"/> says whether the app should close now.</summary>
    public event EventHandler? Finished;

    public bool CloseApp => _settings.Current.Playback.SleepTimer.CloseApp;

    public void StartAfter(TimeSpan time) => Start(SleepMode.AfterTime, time);

    public void StartAtEndOfTrack() => Start(SleepMode.EndOfTrack, TimeSpan.Zero);

    public void StartAtEndOfPlaylist() => Start(SleepMode.EndOfPlaylist, TimeSpan.Zero);

    /// <summary>Switches the timer off (a fade in progress is undone).</summary>
    public void Cancel()
    {
        StopRun();
        lock (_lock)
        {
            if (_fading)
            {
                _engine.Graph.Fade.FadeIn(500);
            }
            ResetState();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        _engine.TrackEndedStop -= OnTrackEndedStop;
        StopRun();
    }

    private void Start(SleepMode mode, TimeSpan time)
    {
        Cancel();
        CancellationTokenSource run;
        lock (_lock)
        {
            Mode = mode;
            _deadline = DateTime.UtcNow + time;
            if (mode == SleepMode.EndOfTrack && !_engine.StopAfterCurrent)
            {
                _engine.StopAfterCurrent = true;
                _setStopAfterCurrent = true;
            }
            _run = run = new CancellationTokenSource();
        }
        _logger.LogInformation("Sleep timer: {Mode} {Time}", mode, mode == SleepMode.AfterTime ? time : null);
        _ = LoopAsync(run.Token);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task LoopAsync(CancellationToken token)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                await TickAsync().ConfigureAwait(false);
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
        }
#pragma warning disable CA1031 // Background loop: log, never crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Sleep timer failed");
        }
    }

    /// <summary>One check; public for tests.</summary>
    public async Task TickAsync()
    {
        var options = _settings.Current.Playback.SleepTimer;
        var fadeLength = TimeSpan.FromSeconds(Math.Clamp(options.FadeSeconds, 1, 300));
        var mode = Mode;
        if (mode is null)
        {
            return;
        }
        var remaining = Remaining;
        var fadeApplies = mode == SleepMode.AfterTime || mode == SleepMode.EndOfTrack || _engine.WillStopAtEnd;
        if (options.FadeOut && fadeApplies && !_fading && remaining is { } left && left <= fadeLength && _engine.State == PlaybackState.Playing)
        {
            _fading = true;
            _engine.Graph.Fade.FadeOut((int)Math.Max(100, left.TotalMilliseconds));
        }
        if (mode == SleepMode.AfterTime && remaining <= TimeSpan.Zero)
        {
            await _engine.StopAsync().ConfigureAwait(false);
            Finish();
        }
    }

    private void OnTrackEndedStop(object? sender, EventArgs e)
    {
        if (Mode is SleepMode.EndOfTrack or SleepMode.EndOfPlaylist)
        {
            Finish();
        }
    }

    private void Finish()
    {
        StopRun();
        lock (_lock)
        {
            ResetState();
        }
        _logger.LogInformation("Sleep timer finished");
        Changed?.Invoke(this, EventArgs.Empty);
        Finished?.Invoke(this, EventArgs.Empty);
    }

    private void ResetState()
    {
        if (_setStopAfterCurrent && _engine.StopAfterCurrent)
        {
            _engine.StopAfterCurrent = false;
        }
        _setStopAfterCurrent = false;
        _fading = false;
        Mode = null;
    }

    private void StopRun()
    {
        CancellationTokenSource? run;
        lock (_lock)
        {
            run = _run;
            _run = null;
        }
        run?.Cancel();
        run?.Dispose();
    }
}
