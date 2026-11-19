#if WINDOWS
using System.Runtime.Versioning;
using Windows.Media;
using Windows.Storage.Streams;

namespace AudioTastic.Platform.Windows;

/// <summary>
/// SI-02 on Windows: System Media Transport Controls for a desktop window
/// (<c>SystemMediaTransportControlsInterop.GetForWindow</c> on the main window's handle). They drive the media
/// overlay (volume flyout / quick settings), the lock screen, the keyboard media keys and Bluetooth headset buttons.
/// Calls before <see cref="Attach"/> are remembered and applied when it happens.
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsMediaControls : IMediaControls
{
    private readonly Lock _gate = new();
    private SystemMediaTransportControls? _smtc;
    private bool _enabled = true;
    private MediaPlaybackState _state;
    private NowPlaying? _item;
    private (TimeSpan Position, TimeSpan? Length) _timeline;

    public bool IsSupported => true;

    public event EventHandler<MediaButton>? ButtonPressed;

    public event EventHandler<TimeSpan>? SeekRequested;

    public void Attach(nint windowHandle)
    {
        lock (_gate)
        {
            if (_smtc is not null || windowHandle == 0)
            {
                return;
            }
            var smtc = SystemMediaTransportControlsInterop.GetForWindow(windowHandle);
            smtc.IsPlayEnabled = true;
            smtc.IsPauseEnabled = true;
            smtc.IsStopEnabled = true;
            smtc.IsNextEnabled = true;
            smtc.IsPreviousEnabled = true;
            smtc.ButtonPressed += OnButtonPressed;
            smtc.PlaybackPositionChangeRequested += OnPositionChangeRequested;
            _smtc = smtc;
            ApplyAll();
        }
    }

    public void SetEnabled(bool enabled)
    {
        lock (_gate)
        {
            _enabled = enabled;
            if (_smtc is not null)
            {
                _smtc.IsEnabled = enabled;
            }
        }
    }

    public void SetState(MediaPlaybackState state)
    {
        lock (_gate)
        {
            _state = state;
            ApplyState();
        }
    }

    public void SetNowPlaying(NowPlaying? item)
    {
        lock (_gate)
        {
            _item = item;
            ApplyItem();
        }
    }

    public void SetTimeline(TimeSpan position, TimeSpan? length)
    {
        lock (_gate)
        {
            _timeline = (position, length);
            ApplyTimeline();
        }
    }

    private void ApplyAll()
    {
        if (_smtc is null)
        {
            return;
        }
        _smtc.IsEnabled = _enabled;
        ApplyState();
        ApplyItem();
        ApplyTimeline();
    }

    private void ApplyState()
    {
        if (_smtc is not null)
        {
            _smtc.PlaybackStatus = _state switch
            {
                MediaPlaybackState.Playing => MediaPlaybackStatus.Playing,
                MediaPlaybackState.Paused => MediaPlaybackStatus.Paused,
                _ => MediaPlaybackStatus.Stopped,
            };
        }
    }

    private void ApplyItem()
    {
        if (_smtc is null)
        {
            return;
        }
        var updater = _smtc.DisplayUpdater;
        updater.ClearAll();
        if (_item is { } item)
        {
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = item.Title;
            updater.MusicProperties.Artist = item.Artist ?? string.Empty;
            updater.MusicProperties.AlbumTitle = item.Album ?? string.Empty;
            if (item.Art is { Length: > 0 } art)
            {
                updater.Thumbnail = RandomAccessStreamReference.CreateFromStream(NativeStream(art));
            }
        }
        updater.Update();
    }

    /// <summary>
    /// The cover in a Windows in-memory stream. The media overlay reads the thumbnail from another process, which a
    /// wrapped .NET stream does not support (the cover stayed empty).
    /// </summary>
    private static InMemoryRandomAccessStream NativeStream(byte[] data)
    {
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(data);
            writer.StoreAsync().AsTask().GetAwaiter().GetResult();
            writer.FlushAsync().AsTask().GetAwaiter().GetResult();
            writer.DetachStream();
        }
        stream.Seek(0);
        return stream;
    }

    private void ApplyTimeline()
    {
        if (_smtc is null)
        {
            return;
        }
        var (position, length) = _timeline;
        var end = length ?? TimeSpan.Zero;
        _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
        {
            StartTime = TimeSpan.Zero,
            MinSeekTime = TimeSpan.Zero,
            EndTime = end,
            MaxSeekTime = end,
            Position = length is null ? TimeSpan.Zero : position < end ? position : end,
        });
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        MediaButton? button = args.Button switch
        {
            SystemMediaTransportControlsButton.Play => MediaButton.Play,
            SystemMediaTransportControlsButton.Pause => MediaButton.Pause,
            SystemMediaTransportControlsButton.Stop => MediaButton.Stop,
            SystemMediaTransportControlsButton.Next => MediaButton.Next,
            SystemMediaTransportControlsButton.Previous => MediaButton.Previous,
            _ => null,
        };
        if (button is { } b)
        {
            ButtonPressed?.Invoke(this, b);
        }
    }

    private void OnPositionChangeRequested(SystemMediaTransportControls sender, PlaybackPositionChangeRequestedEventArgs args) =>
        SeekRequested?.Invoke(this, args.RequestedPlaybackPosition);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_smtc is null)
            {
                return;
            }
            _smtc.ButtonPressed -= OnButtonPressed;
            _smtc.PlaybackPositionChangeRequested -= OnPositionChangeRequested;
            _smtc.DisplayUpdater.ClearAll();
            _smtc.DisplayUpdater.Update();
            _smtc.IsEnabled = false;
            _smtc = null;
        }
    }
}
#endif
