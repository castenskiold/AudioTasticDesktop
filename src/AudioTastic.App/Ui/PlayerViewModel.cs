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

    public PlayerViewModel(IPlayerController player, SettingsStore settings)
    {
        _player = player;
        _settings = settings;
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
    }

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
    }

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
    });

    private void OnPlaybackError(object? sender, PlaybackErrorEventArgs e) => Dispatcher.UIThread.Post(() =>
        Status = $"Cannot play {System.IO.Path.GetFileName(e.Path)}: {e.Message}");

    private void ApplyTrack(TrackInfo? track)
    {
        Track = track;
        Duration = track?.Duration;
        Title = DisplayText.Title(track);
        Indicators = DisplayText.Indicators(track);
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
