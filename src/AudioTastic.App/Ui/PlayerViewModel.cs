using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using AudioTastic.Core.Playback;
using AudioTastic.Data.Settings;

namespace AudioTastic.App.Ui;

/// <summary>
/// Player state for the skinned windows, on the UI thread. Engine events (background threads) are marshalled here;
/// skinned controls observe <see cref="PropertyChanged"/>.
/// </summary>
public sealed class PlayerViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly IPlayerController _player;
    private readonly SettingsStore _settings;
    private readonly AnalysisCoordinator? _analysis;
    private readonly SleepTimerService? _sleep;
    private AbLoop? _abLoop;
    private Analysis.Waveform? _waveform;
    private double? _waveformProgress;
    private bool _isLive;
    private PlaybackState _state;
    private TrackInfo? _track;
    private TimeSpan _position;
    private TimeSpan? _duration;
    private int _volume;
    private bool _muted;
    private bool _shuffle;
    private RepeatMode _repeat;
    private bool _showRemaining;
    private string _title = DisplayText.Title(null);
    private IReadOnlyList<string> _indicators = [];
    private string? _status;

    public PlayerViewModel(IPlayerController player, SettingsStore settings, AnalysisCoordinator? analysis = null, SleepTimerService? sleep = null)
    {
        _player = player;
        _settings = settings;
        _analysis = analysis;
        _sleep = sleep;
        _state = player.State;
        _volume = player.Volume;
        _muted = player.IsMuted;
        _shuffle = player.Shuffle;
        _repeat = player.Repeat;
        _showRemaining = settings.Current.Interface.ShowRemainingTime;
        ApplyTrack(player.CurrentTrack);

        player.StateChanged += OnStateChanged;
        player.TrackChanged += OnTrackChanged;
        player.PositionChanged += OnPositionChanged;
        player.PlayModeChanged += OnPlayModeChanged;
        player.PlaybackError += OnPlaybackError;
        player.ReplayGainChanged += OnReplayGainChanged;
        player.SpeedChanged += OnBadgesChanged;
        player.OutputChanged += OnBadgesChanged;
        if (analysis is not null)
        {
            analysis.Changed += OnAnalysisChanged;
        }
        if (sleep is not null)
        {
            sleep.Changed += OnBadgesChanged;
        }
    }

    /// <summary>PB-08: the A-B loop (markers on the waveform).</summary>
    public AbLoop? AbLoop { get => _abLoop; private set => Set(ref _abLoop, value); }

    /// <summary>CO-07: the playing track's waveform, once analysed.</summary>
    public Analysis.Waveform? Waveform { get => _waveform; private set => Set(ref _waveform, value); }

    /// <summary>CO-07: 0–1 while the waveform is being made; null otherwise.</summary>
    public double? WaveformProgress { get => _waveformProgress; private set => Set(ref _waveformProgress, value); }

    /// <summary>A live stream is playing (scrolling waveform).</summary>
    public bool IsLive { get => _isLive; private set => Set(ref _isLive, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public IPlayerController Player => _player;
    public PlaybackState State { get => _state; private set => Set(ref _state, value); }
    public TrackInfo? Track { get => _track; private set => Set(ref _track, value); }
    public TimeSpan Position { get => _position; private set => Set(ref _position, value); }
    public TimeSpan? Duration { get => _duration; private set => Set(ref _duration, value); }
    public bool Shuffle { get => _shuffle; private set => Set(ref _shuffle, value); }
    public RepeatMode Repeat { get => _repeat; private set => Set(ref _repeat, value); }
    public string Title { get => _title; private set => Set(ref _title, value); }
    public IReadOnlyList<string> Indicators { get => _indicators; private set => Set(ref _indicators, value); }

    /// <summary>Last error message (unplayable file, lost device), cleared on the next track.</summary>
    public string? Status { get => _status; private set => Set(ref _status, value); }

    /// <summary>True while a track is loaded (playing, paused or buffering).</summary>
    public bool HasTrack => _state != PlaybackState.Stopped && _track is not null;

    public int Volume
    {
        get => _volume;
        set
        {
            var v = Math.Clamp(value, 0, 100);
            if (Set(ref _volume, v))
            {
                _player.Volume = v;
            }
        }
    }

    public bool Muted
    {
        get => _muted;
        set
        {
            if (Set(ref _muted, value))
            {
                _player.IsMuted = value;
            }
        }
    }

    /// <summary>CO-06: time display shows remaining time (saved).</summary>
    public bool ShowRemaining
    {
        get => _showRemaining;
        set
        {
            if (Set(ref _showRemaining, value))
            {
                _settings.Update(SettingsGroup.Interface, s => s.Interface.ShowRemainingTime = value);
                OnPropertyChanged(nameof(TimeText));
            }
        }
    }

    public string TimeText => DisplayText.TimeDisplay(_position, _duration, _showRemaining);

    /// <summary>Shows a message in the title area until the next track starts.</summary>
    public void ShowMessage(string message) => Status = message;

    public void Dispose()
    {
        _player.StateChanged -= OnStateChanged;
        _player.TrackChanged -= OnTrackChanged;
        _player.PositionChanged -= OnPositionChanged;
        _player.PlayModeChanged -= OnPlayModeChanged;
        _player.PlaybackError -= OnPlaybackError;
        _player.ReplayGainChanged -= OnReplayGainChanged;
        _player.SpeedChanged -= OnBadgesChanged;
        _player.OutputChanged -= OnBadgesChanged;
        if (_analysis is not null)
        {
            _analysis.Changed -= OnAnalysisChanged;
        }
        if (_sleep is not null)
        {
            _sleep.Changed -= OnBadgesChanged;
        }
    }

    private void OnReplayGainChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(UpdateIndicators);

    private void OnBadgesChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(UpdateIndicators);

    private void UpdateIndicators()
    {
        AbLoop = _player.AbLoop;
        var output = _player.OutputStatus;
        var sleepMinutes = _sleep?.Mode switch
        {
            null => (int?)null,
            SleepMode.AfterTime => (int)Math.Ceiling(_sleep.Remaining?.TotalMinutes ?? 0),
            _ => 0,
        };
        var badges = new PlayerBadges(_player.Speed, _player.AbLoop?.B is not null, _player.StopAfterCurrent, sleepMinutes,
            _track is not null && output is { } o && o.DeviceRate > 0 && o.DeviceRate != _track.Stream.SampleRate);
        var indicators = DisplayText.Indicators(_track, _player.ReplayGain, badges);
        if (!indicators.SequenceEqual(Indicators))
        {
            Indicators = indicators;
        }
    }

    private void OnAnalysisChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        Waveform = _analysis!.Waveform;
        WaveformProgress = _analysis.Progress;
        IsLive = _analysis.IsLive;
    });

    private void OnStateChanged(object? sender, PlaybackStateEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        State = e.State;
        OnPropertyChanged(nameof(HasTrack));
        if (e.State == PlaybackState.Stopped)
        {
            Position = TimeSpan.Zero;
            OnPropertyChanged(nameof(TimeText));
        }
    });

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        ApplyTrack(e.Track);
        Status = null;
        OnPropertyChanged(nameof(HasTrack));
    });

    private void OnPositionChanged(object? sender, PlaybackPositionEventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        Position = e.Position;
        Duration = e.Duration;
        OnPropertyChanged(nameof(TimeText));
    });

    private void OnPlayModeChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        Shuffle = _player.Shuffle;
        Repeat = _player.Repeat;
        UpdateIndicators(); // PB-08 A-B, PB-09 stop after current
    });

    private void OnPlaybackError(object? sender, PlaybackErrorEventArgs e) => Dispatcher.UIThread.Post(() =>
        Status = $"Cannot play {System.IO.Path.GetFileName(e.Path)}: {e.Message}");

    private void ApplyTrack(TrackInfo? track)
    {
        Track = track;
        Duration = track?.Duration;
        Title = DisplayText.Title(track);
        UpdateIndicators();
        OnPropertyChanged(nameof(TimeText));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
