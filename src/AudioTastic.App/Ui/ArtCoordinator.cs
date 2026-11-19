using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AudioTastic.Core.Playback;
using AudioTastic.Data.Settings;
using AudioTastic.Metadata;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Ui;

/// <summary>
/// MD-02: the playing track's album art for the skinned windows. Found (<see cref="ArtService"/>) and decoded off the
/// UI thread when the track changes, when its tags are written (a new cover) and when Settings › Metadata › Album art
/// changes; <see cref="Changed"/> is raised on the UI thread. <see cref="Image"/> is null when there is no art (the
/// skin then shows the logo).
/// </summary>
public sealed class ArtCoordinator : IDisposable
{
    /// <summary>Decoded width: enough for the Art &amp; Lyrics window at double size on a high-DPI screen.</summary>
    private const int DecodeWidth = 800;

    private readonly IPlayerController _player;
    private readonly ArtService _art;
    private readonly SettingsStore _settings;
    private readonly TagWriteService? _writes;
    private readonly ILogger<ArtCoordinator> _logger;
    private int _version;
    private string? _path;
    private bool _albumArtOn;

    public ArtCoordinator(IPlayerController player, ArtService art, SettingsStore settings, ILogger<ArtCoordinator> logger, TagWriteService? writes = null)
    {
        _player = player;
        _art = art;
        _settings = settings;
        _writes = writes;
        _logger = logger;
        _albumArtOn = settings.Current.Metadata.AlbumArt;
        player.TrackChanged += OnTrackChanged;
        settings.Changed += OnSettingsChanged;
        if (writes is not null)
        {
            writes.Written += OnTagsWritten;
        }
        Reload();
    }

    /// <summary>The decoded cover, or null.</summary>
    public Bitmap? Image { get; private set; }

    /// <summary>Where the cover came from (null file = embedded), or null.</summary>
    public AlbumArt? Source { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="Image"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Finds the current track's art again.</summary>
    public void Reload()
    {
        var version = Interlocked.Increment(ref _version);
        var path = _player.CurrentTrack?.Path;
        _path = path;
        if (path is null || !_settings.Current.Metadata.AlbumArt)
        {
            Dispatcher.UIThread.Post(() => Apply(version, null, null));
            return;
        }
        _ = Task.Run(() =>
        {
            AlbumArt? found = null;
            Bitmap? image = null;
            try
            {
                found = _art.Find(path);
                if (found is not null)
                {
                    using var stream = new MemoryStream(found.Picture.Data);
                    image = Bitmap.DecodeToWidth(stream, DecodeWidth, BitmapInterpolationMode.HighQuality);
                }
            }
#pragma warning disable CA1031 // A broken picture shows no art.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogDebug(ex, "Album art of {Path} could not be shown", path);
                image?.Dispose();
                image = null;
                found = null;
            }
            Dispatcher.UIThread.Post(() => Apply(version, found, image));
        });
    }

    public void Dispose()
    {
        _player.TrackChanged -= OnTrackChanged;
        _settings.Changed -= OnSettingsChanged;
        if (_writes is not null)
        {
            _writes.Written -= OnTagsWritten;
        }
    }

    private void Apply(int version, AlbumArt? source, Bitmap? image)
    {
        if (version != Volatile.Read(ref _version))
        {
            image?.Dispose(); // a newer request is on its way
            return;
        }
        var old = Image;
        Image = image;
        Source = source;
        Changed?.Invoke(this, EventArgs.Empty);
        // Controls drop the old bitmap on Changed; release it after this render.
        if (old is not null)
        {
            Dispatcher.UIThread.Post(old.Dispose, DispatcherPriority.Background);
        }
    }

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e)
    {
        if (!string.Equals(e.Track?.Path, _path, StringComparison.Ordinal))
        {
            Reload();
        }
    }

    private void OnTagsWritten(object? sender, TagWrittenEventArgs e)
    {
        if (string.Equals(e.Path, _path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            Reload();
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Group == SettingsGroup.Metadata && e.Settings.Metadata.AlbumArt != _albumArtOn)
        {
            _albumArtOn = e.Settings.Metadata.AlbumArt;
            Reload();
        }
    }
}
