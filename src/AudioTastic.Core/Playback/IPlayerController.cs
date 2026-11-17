using AudioTastic.Core.Audio;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.Core.Playback;

/// <summary>CO-02: transport state.</summary>
public enum PlaybackState
{
    Stopped,
    Playing,
    Paused,
    /// <summary>Opening a file or seeking; audio follows shortly.</summary>
    Buffering,
}

/// <summary>The track being played.</summary>
/// <param name="Path">File path.</param>
/// <param name="Title">Title tag, or the file name without extension.</param>
/// <param name="Artist">Artist tag, if any.</param>
/// <param name="Stream">Source format (codec, rate, bit depth) for MW5 and the file info dialog.</param>
/// <param name="Duration">Length, when known.</param>
public sealed record TrackInfo(string Path, string Title, string? Artist, AudioStreamInfo Stream, TimeSpan? Duration);

/// <summary>CO-06: playback position, raised at 10 Hz while playing or paused.</summary>
public sealed class PlaybackPositionEventArgs(TimeSpan position, TimeSpan? duration) : EventArgs
{
    public TimeSpan Position { get; } = position;
    public TimeSpan? Duration { get; } = duration;
}

public sealed class PlaybackStateEventArgs(PlaybackState state) : EventArgs
{
    public PlaybackState State { get; } = state;
}

public sealed class TrackChangedEventArgs(TrackInfo? track, int index) : EventArgs
{
    public TrackInfo? Track { get; } = track;
    public int Index { get; } = index;
}

public sealed class PlaybackErrorEventArgs(string path, string message) : EventArgs
{
    public string Path { get; } = path;
    public string Message { get; } = message;
}

/// <summary>
/// CO-02: the single entry point for transport commands, used by the UI, plugins, IPC, OS media controls and the
/// remote-control API. Commands are serialised; events are raised on background threads (UI code must marshal
/// to its own thread).
/// <para>P1 keeps a simple track list here; the playlist (PL-01, P3) replaces it.</para>
/// </summary>
public interface IPlayerController
{
    PlaybackState State { get; }

    /// <summary>The loaded track, or null when nothing has been played yet.</summary>
    TrackInfo? CurrentTrack { get; }

    /// <summary>Index of the current track in <see cref="Tracks"/>; -1 when the list is empty.</summary>
    int CurrentIndex { get; }

    IReadOnlyList<string> Tracks { get; }

    TimeSpan Position { get; }
    TimeSpan? Duration { get; }

    /// <summary>CO-05: 0–100, perceptual curve; saved across restarts.</summary>
    int Volume { get; set; }

    /// <summary>CO-05: silences output without changing <see cref="Volume"/>.</summary>
    bool IsMuted { get; set; }

    /// <summary>Name of the device audio is going to, or null when no output is open.</summary>
    string? OutputDeviceName { get; }

    event EventHandler<PlaybackStateEventArgs>? StateChanged;
    event EventHandler<TrackChangedEventArgs>? TrackChanged;
    event EventHandler<PlaybackPositionEventArgs>? PositionChanged;
    event EventHandler<PlaybackErrorEventArgs>? PlaybackError;

    /// <summary>Replaces the track list with <paramref name="paths"/> and plays the first (Open, L key).</summary>
    Task OpenAsync(IEnumerable<string> paths, bool play = true);

    /// <summary>Resumes when paused, restarts the current track when playing, starts it when stopped (X key).</summary>
    Task PlayAsync();

    /// <summary>Plays the track at <paramref name="index"/>.</summary>
    Task PlayIndexAsync(int index);

    /// <summary>Toggles pause (C key).</summary>
    Task PauseAsync();

    /// <summary>Stops and resets to the start of the track (V key).</summary>
    Task StopAsync();

    /// <summary>Restarts the track when more than 3 s has played, otherwise goes to the previous track (Z key).</summary>
    Task PreviousAsync();

    /// <summary>Goes to the next track (B key).</summary>
    Task NextAsync();

    /// <summary>Seeks the current track sample-accurately.</summary>
    Task SeekAsync(TimeSpan position);

    /// <summary>PB-04: playback devices.</summary>
    IReadOnlyList<AudioDeviceInfo> GetOutputDevices();

    /// <summary>PB-04: switches to <paramref name="deviceName"/> (null = system default) and saves the choice.</summary>
    Task SetOutputDeviceAsync(string? deviceName);
}
