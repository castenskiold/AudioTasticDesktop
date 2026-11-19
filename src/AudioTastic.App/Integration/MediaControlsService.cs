using AudioTastic.Core.Playback;
using AudioTastic.Data.Settings;
using AudioTastic.Metadata;
using AudioTastic.Platform;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Integration;

/// <summary>
/// SI-02: keeps the OS media controls in line with the player (title, artist, album, cover, state, position) and turns
/// their buttons (overlay, lock screen, keyboard media keys, headset) into player commands. The cover and album are
/// read in the background; the position is sent at most once a second and on seeks. A track without a cover (and a
/// stream) shows the AudioTastic logo (Anders, 19 Nov 2026).
/// </summary>
public sealed class MediaControlsService : IDisposable
{
    private readonly IMediaControls _controls;
    private readonly IPlayerController _player;
    private readonly ArtService _art;
    private readonly TagService _tags;
    private readonly SettingsStore _settings;
    private readonly ILogger<MediaControlsService> _logger;
    private long _lastTimelineTicks;
    private TimeSpan _lastPosition;
    private int _trackVersion;
    private bool _started;
    private byte[]? _logo;

    public MediaControlsService(IPlatformServices platform, IPlayerController player, ArtService art, TagService tags, SettingsStore settings,
        ILogger<MediaControlsService> logger)
    {
        _controls = platform.MediaControls;
        _player = player;
        _art = art;
        _tags = tags;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>Connects to the OS (Windows: the main window's handle) and starts following the player.</summary>
    public void Start(nint mainWindowHandle)
    {
        if (_started || !_controls.IsSupported)
        {
            return;
        }
        _started = true;
        try
        {
            _controls.SetEnabled(_settings.Current.SystemIntegration.MediaControls);
            _controls.Attach(mainWindowHandle);
        }
#pragma warning disable CA1031 // The OS feature is optional: never stop the player over it.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "OS media controls are not available");
            return;
        }
        _controls.ButtonPressed += OnButtonPressed;
        _controls.SeekRequested += OnSeekRequested;
        _player.TrackChanged += OnTrackChanged;
        _player.StateChanged += OnStateChanged;
        _player.PositionChanged += OnPositionChanged;
        _settings.Changed += OnSettingsChanged;
        Publish(_player.CurrentTrack);
        _controls.SetState(StateOf(_player.State));
    }

    /// <summary>The state the OS shows: buffering counts as playing, so the overlay does not flicker.</summary>
    public static MediaPlaybackState StateOf(PlaybackState state) => state switch
    {
        PlaybackState.Playing or PlaybackState.Buffering => MediaPlaybackState.Playing,
        PlaybackState.Paused => MediaPlaybackState.Paused,
        _ => MediaPlaybackState.Stopped,
    };

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Group == SettingsGroup.SystemIntegration)
        {
            _controls.SetEnabled(e.Settings.SystemIntegration.MediaControls);
        }
    }

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e) => Publish(e.Track);

    private void OnStateChanged(object? sender, PlaybackStateEventArgs e) => _controls.SetState(StateOf(e.State));

    private void OnPositionChanged(object? sender, PlaybackPositionEventArgs e)
    {
        // Once a second, or at once after a jump (a seek), so the overlay's progress stays right.
        var now = Environment.TickCount64;
        var jumped = (e.Position - _lastPosition).Duration() > TimeSpan.FromSeconds(2);
        _lastPosition = e.Position;
        if (jumped || now - _lastTimelineTicks >= 1000)
        {
            _lastTimelineTicks = now;
            _controls.SetTimeline(e.Position, e.Duration);
        }
    }

    /// <summary>Shows the title and artist at once, then adds the album and the cover when read.</summary>
    private void Publish(TrackInfo? track)
    {
        var version = Interlocked.Increment(ref _trackVersion);
        if (track is null)
        {
            _controls.SetNowPlaying(null);
            return;
        }
        _controls.SetNowPlaying(new NowPlaying(track.Title, track.Artist, null, Logo()));
        _controls.SetTimeline(TimeSpan.Zero, track.Duration);
        if (track.Path.Contains("://", StringComparison.Ordinal))
        {
            return; // streams: no tags or cover to read; the logo stays
        }
        _ = Task.Run(() =>
        {
            try
            {
                var album = _tags.Read(track.Path)?.Album;
                var art = _settings.Current.Metadata.AlbumArt ? _art.Find(track.Path)?.Picture.Data : null;
                if (Volatile.Read(ref _trackVersion) == version)
                {
                    _controls.SetNowPlaying(new NowPlaying(track.Title, track.Artist, album, art ?? Logo()));
                }
            }
#pragma warning disable CA1031 // Background read for display only.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogDebug(ex, "No album or cover for the media controls: {Path}", track.Path);
            }
        });
    }

    /// <summary>The app logo (audiotastic.png next to the program), read once; null if it is missing.</summary>
    private byte[]? Logo()
    {
        if (_logo is null)
        {
            try
            {
                _logo = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "audiotastic.png"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logo = [];
            }
        }
        return _logo.Length > 0 ? _logo : null;
    }

    private void OnButtonPressed(object? sender, MediaButton button)
    {
        _logger.LogInformation("Media button {Button}", button);
        _ = Run(button switch
        {
            MediaButton.Play => _player.PlayAsync,
            MediaButton.Pause => _player.PauseAsync,
            MediaButton.Stop => _player.StopAsync,
            MediaButton.Next => _player.NextAsync,
            MediaButton.Previous => _player.PreviousAsync,
            _ => () => Task.CompletedTask,
        });
    }

    private void OnSeekRequested(object? sender, TimeSpan position) => _ = Run(() => _player.SeekAsync(position));

    private async Task Run(Func<Task> command)
    {
        try
        {
            await command().ConfigureAwait(false);
        }
#pragma warning disable CA1031 // A media button must never take the player down.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Media button command failed");
        }
    }

    public void Dispose()
    {
        if (!_started)
        {
            return;
        }
        _controls.ButtonPressed -= OnButtonPressed;
        _controls.SeekRequested -= OnSeekRequested;
        _player.TrackChanged -= OnTrackChanged;
        _player.StateChanged -= OnStateChanged;
        _player.PositionChanged -= OnPositionChanged;
        _settings.Changed -= OnSettingsChanged;
        _controls.Dispose();
    }
}
