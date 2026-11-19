using AudioTastic.Core.Playback;
using AudioTastic.Data.Settings;
using AudioTastic.Metadata;
using AudioTastic.Platform;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Integration;

/// <summary>
/// SI-07: a native notification with the title, artist and cover when the track changes (also a new radio title).
/// Optional (Settings › Interface, off by default) and never while a player window has the focus. The cover is written
/// to a small temporary file, because Windows toasts take images as files; without a cover the app logo is shown.
/// </summary>
public sealed class NotificationService : IDisposable
{
    private readonly INotifications _notifications;
    private readonly IPlayerController _player;
    private readonly ArtService _art;
    private readonly TagService _tags;
    private readonly SettingsStore _settings;
    private readonly ILogger<NotificationService> _logger;
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "AudioTastic");
    private Func<bool>? _playerFocused;
    private string? _lastShown;
    private int _version;

    public NotificationService(IPlatformServices platform, IPlayerController player, ArtService art, TagService tags, SettingsStore settings,
        ILogger<NotificationService> logger)
    {
        _notifications = platform.Notifications;
        _player = player;
        _art = art;
        _tags = tags;
        _settings = settings;
        _logger = logger;
    }

    public bool IsSupported => _notifications.IsSupported;

    /// <summary>Starts following track changes; <paramref name="playerFocused"/> is asked on the UI thread.</summary>
    public void Start(Func<bool> playerFocused)
    {
        if (!_notifications.IsSupported || _playerFocused is not null)
        {
            return;
        }
        _playerFocused = playerFocused;
        _player.TrackChanged += OnTrackChanged;
    }

    /// <summary>The notification's second line: "Artist — Album", either part, or null.</summary>
    public static string? SecondLine(string? artist, string? album) =>
        (string.IsNullOrWhiteSpace(artist), string.IsNullOrWhiteSpace(album)) switch
        {
            (false, false) => $"{artist} — {album}",
            (false, true) => artist,
            (true, false) => album,
            _ => null,
        };

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e)
    {
        if (e.Track is not { } track || !_settings.Current.Interface.NotifyOnTrackChange)
        {
            return;
        }
        var key = track.Path + "|" + track.Title;
        var version = Interlocked.Increment(ref _version);
        Dispatcher.UIThread.Post(() =>
        {
            if (_playerFocused?.Invoke() == true || key == _lastShown)
            {
                _logger.LogDebug("No track notification: {Reason}", key == _lastShown ? "same title" : "the player is in front");
                return; // the player is in front, or the same title again (gapless hand-over of the same entry)
            }
            _lastShown = key;
            _ = Task.Run(() => Show(track, version));
        });
    }

    private void Show(TrackInfo track, int version)
    {
        try
        {
            var stream = track.Path.Contains("://", StringComparison.Ordinal);
            var album = stream ? null : _tags.Read(track.Path)?.Album;
            var cover = stream || !_settings.Current.Metadata.AlbumArt ? null : _art.Find(track.Path)?.Picture;
            if (Volatile.Read(ref _version) != version)
            {
                return; // another track started meanwhile
            }
            var image = ImageFile(cover);
            _notifications.ShowNowPlaying(track.Title, SecondLine(track.Artist, album), image);
            _logger.LogDebug("Track notification shown: {Title} ({Image})", track.Title, image);
        }
#pragma warning disable CA1031 // Notifications are optional: never disturb playback.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Could not show the track notification");
        }
    }

    /// <summary>The cover as a temporary file (one per image type, overwritten each time), or the app logo.</summary>
    private string? ImageFile(TagPicture? cover)
    {
        if (cover is { Data.Length: > 0 })
        {
            Directory.CreateDirectory(_folder);
            var extension = cover.MimeType.Contains("png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            // A new name each time: Windows caches toast images by path.
            foreach (var old in Directory.EnumerateFiles(_folder, "cover-*"))
            {
                TryDelete(old);
            }
            var file = Path.Combine(_folder, $"cover-{Environment.TickCount64}{extension}");
            File.WriteAllBytes(file, cover.Data);
            return file;
        }
        var logo = Path.Combine(AppContext.BaseDirectory, "audiotastic.png");
        return File.Exists(logo) ? logo : null;
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // still shown by Windows; removed next time
        }
    }

    public void Dispose()
    {
        if (_playerFocused is not null)
        {
            _player.TrackChanged -= OnTrackChanged;
        }
    }
}
