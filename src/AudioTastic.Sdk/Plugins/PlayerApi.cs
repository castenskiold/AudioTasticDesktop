using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Metadata;
using AudioTastic.Sdk.Playlists;

namespace AudioTastic.Sdk.Plugins;

/// <summary>Playback state as plugins see it.</summary>
public enum PlayerState
{
    Stopped,
    Playing,
    Paused,
    Buffering,
}

/// <summary>The current track.</summary>
/// <param name="Path">File path or stream URL.</param>
/// <param name="Tags">Title, artist, album, ... (for streams the title follows ICY metadata).</param>
/// <param name="Stream">Source format.</param>
/// <param name="Duration">Length, or null for streams.</param>
public sealed record NowPlaying(string Path, TrackTags Tags, AudioStreamInfo Stream, TimeSpan? Duration);

public sealed class NowPlayingEventArgs(NowPlaying? nowPlaying) : EventArgs
{
    public NowPlaying? NowPlaying { get; } = nowPlaying;
}

public sealed class PlayerStateEventArgs(PlayerState state) : EventArgs
{
    public PlayerState State { get; } = state;
}

/// <summary>
/// PG-01: controlled access to the player for plugins. Events are raised on background threads; plugins with UI must
/// marshal to the UI thread themselves. Commands are queued and safe to call from any thread.
/// </summary>
public interface IPlayerApi
{
    PlayerState State { get; }

    NowPlaying? NowPlaying { get; }

    TimeSpan Position { get; }

    /// <summary>0–100.</summary>
    int Volume { get; set; }

    /// <summary>A new track started (or playback ended: null), or a stream's title changed.</summary>
    event EventHandler<NowPlayingEventArgs>? TrackChanged;

    event EventHandler<PlayerStateEventArgs>? StateChanged;

    Task PlayAsync();
    Task PauseAsync();
    Task StopAsync();
    Task NextAsync();
    Task PreviousAsync();
    Task SeekAsync(TimeSpan position);

    /// <summary>The entries of the playlist tab shown in the playlist window.</summary>
    IReadOnlyList<PlaylistItem> GetActivePlaylist();

    /// <summary>Adds files, folders, playlists or URLs to the active tab; with <paramref name="play"/> the first one starts.</summary>
    Task AddToPlaylistAsync(IEnumerable<string> paths, bool play);

    /// <summary>Reads a file's tags (cached). Slow for uncached files: call from a background thread.</summary>
    TrackTags? ReadTags(string path);
}
