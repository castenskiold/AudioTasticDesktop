namespace AudioTastic.Platform;

/// <summary>SI-02: a button pressed in the OS media controls (overlay, lock screen, keyboard media keys, headset).</summary>
public enum MediaButton
{
    Play,
    Pause,
    Stop,
    Next,
    Previous,
}

/// <summary>SI-02: what the OS media controls show as the playback state.</summary>
public enum MediaPlaybackState
{
    Stopped,
    Playing,
    Paused,
}

/// <summary>SI-02: the now-playing information shown by the OS.</summary>
/// <param name="Art">Encoded cover image (JPEG/PNG), or null.</param>
public sealed record NowPlaying(string Title, string? Artist, string? Album, byte[]? Art);

/// <summary>
/// SI-02: the operating system's media controls: Windows System Media Transport Controls (media overlay, lock screen,
/// media keys, Bluetooth headsets), macOS Now Playing, Linux MPRIS. Events may arrive on any thread.
/// </summary>
public interface IMediaControls : IDisposable
{
    /// <summary>True when this OS build has media controls (Windows so far).</summary>
    bool IsSupported { get; }

    /// <summary>A transport button was pressed.</summary>
    event EventHandler<MediaButton>? ButtonPressed;

    /// <summary>The user moved the position in the OS controls (where the OS offers that).</summary>
    event EventHandler<TimeSpan>? SeekRequested;

    /// <summary>Connects to the OS; on Windows the controls belong to <paramref name="windowHandle"/> (the main window).</summary>
    void Attach(nint windowHandle);

    /// <summary>Shows (true) or removes (false) AudioTastic from the OS media controls.</summary>
    void SetEnabled(bool enabled);

    void SetState(MediaPlaybackState state);

    /// <summary>The track shown; null clears it.</summary>
    void SetNowPlaying(NowPlaying? item);

    /// <summary>The position and length shown (length null: a live stream without one).</summary>
    void SetTimeline(TimeSpan position, TimeSpan? length);
}

/// <summary>Media controls for systems that have none yet (macOS and Linux until they get their own).</summary>
public sealed class NoMediaControls : IMediaControls
{
    public static NoMediaControls Instance { get; } = new();

    public bool IsSupported => false;

    public event EventHandler<MediaButton>? ButtonPressed
    {
        add { }
        remove { }
    }

    public event EventHandler<TimeSpan>? SeekRequested
    {
        add { }
        remove { }
    }

    public void Attach(nint windowHandle)
    {
    }

    public void SetEnabled(bool enabled)
    {
    }

    public void SetState(MediaPlaybackState state)
    {
    }

    public void SetNowPlaying(NowPlaying? item)
    {
    }

    public void SetTimeline(TimeSpan position, TimeSpan? length)
    {
    }

    public void Dispose()
    {
    }
}
