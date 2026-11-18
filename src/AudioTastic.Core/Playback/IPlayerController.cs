using AudioTastic.Core.Audio;
using AudioTastic.Core.Playlists;
using AudioTastic.Data.Settings;
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

/// <summary>PB-04 / PB-05: output mode, sample format, the rate the graph renders at and the hardware's rate.</summary>
public sealed record OutputStatus(bool Exclusive, OutputSampleFormat SampleFormat, int RenderRate, int DeviceRate);

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

public sealed class TrackChangedEventArgs(TrackInfo? track, int index, PlaylistEntry? entry) : EventArgs
{
    /// <summary>The playing track, or null when only the selection moved (stopped) or playback ended.</summary>
    public TrackInfo? Track { get; } = track;

    /// <summary>Position of <see cref="Entry"/> in the playing playlist.</summary>
    public int Index { get; } = index;

    public PlaylistEntry? Entry { get; } = entry;
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
/// <para>Plays entries of a <see cref="Playlist"/> (PL-01): the playing playlist can differ from the tab shown (PL-02).</para>
/// </summary>
public interface IPlayerController
{
    PlaybackState State { get; }

    /// <summary>The loaded track, or null when nothing has been played yet.</summary>
    TrackInfo? CurrentTrack { get; }

    /// <summary>Index of the current entry in <see cref="PlayingPlaylist"/>; -1 when there is none.</summary>
    int CurrentIndex { get; }

    /// <summary>PL-02: the playlist Next/Previous follow, even when another tab is shown.</summary>
    Playlist PlayingPlaylist { get; }

    /// <summary>The current (or last played) entry.</summary>
    PlaylistEntry? CurrentEntry { get; }

    TimeSpan Position { get; }
    TimeSpan? Duration { get; }

    /// <summary>CO-05: 0–100, perceptual curve; saved across restarts.</summary>
    int Volume { get; set; }

    /// <summary>CO-05: silences output without changing <see cref="Volume"/>.</summary>
    bool IsMuted { get; set; }

    /// <summary>CO-03: shuffle on/off (saved).</summary>
    bool Shuffle { get; set; }

    /// <summary>CO-04: repeat mode (saved).</summary>
    RepeatMode Repeat { get; set; }

    /// <summary>Name of the device audio is going to, or null when no output is open.</summary>
    string? OutputDeviceName { get; }

    event EventHandler<PlaybackStateEventArgs>? StateChanged;
    event EventHandler<TrackChangedEventArgs>? TrackChanged;
    event EventHandler<PlaybackPositionEventArgs>? PositionChanged;
    event EventHandler<PlaybackErrorEventArgs>? PlaybackError;

    /// <summary>Raised when <see cref="Shuffle"/> or <see cref="Repeat"/> changes.</summary>
    event EventHandler<EventArgs>? PlayModeChanged;

    /// <summary>PB-03: the ReplayGain applied to the current track, or null.</summary>
    ReplayGainDecision? ReplayGain { get; }

    /// <summary>PB-03: raised when <see cref="ReplayGain"/> changes.</summary>
    event EventHandler<EventArgs>? ReplayGainChanged;

    /// <summary>CO-07: recent peaks of the mixed signal (live waveform for streams).</summary>
    PeakTap Peaks { get; }

    /// <summary>PB-04 / PB-05: how the output is running; null when closed.</summary>
    OutputStatus? OutputStatus { get; }

    /// <summary>PB-04 / PB-05: raised when the output is (re)opened.</summary>
    event EventHandler<EventArgs>? OutputChanged;

    /// <summary>Plays <paramref name="entry"/> of <paramref name="playlist"/>, which becomes the playing playlist.</summary>
    Task PlayAsync(Playlist playlist, PlaylistEntry entry);

    /// <summary>Plays <paramref name="paths"/> as an ad-hoc playlist (tests, tools). The app adds to a playlist tab instead.</summary>
    Task OpenAsync(IEnumerable<string> paths, bool play = true);

    /// <summary>Resumes when paused, restarts the current track when playing, starts it when stopped (X key).</summary>
    Task PlayAsync();

    /// <summary>Plays the entry at <paramref name="index"/> of the playing playlist.</summary>
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
