using AudioTastic.App.Ui.Playlist;
using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using AudioTastic.Metadata;
using AudioTastic.Sdk.Metadata;
using AudioTastic.Sdk.Playlists;
using AudioTastic.Sdk.Plugins;
using Avalonia.Threading;

namespace AudioTastic.App.Plugins;

/// <summary>
/// PG-01: the player as plugins see it. Transport goes through <see cref="IPlayerController"/>; playlist edits are
/// marshalled to the UI thread (playlists are edited only there). Events are raised on background threads.
/// </summary>
internal sealed class PlayerApi : IPlayerApi, IDisposable
{
    private readonly PlaybackEngine _engine;
    private readonly PlaylistService _playlists;
    private readonly TagService _tags;
    private readonly Func<PlaylistController> _controller;

    public PlayerApi(PlaybackEngine engine, PlaylistService playlists, TagService tags, Func<PlaylistController> controller)
    {
        _engine = engine;
        _playlists = playlists;
        _tags = tags;
        _controller = controller;
        _engine.StateChanged += OnStateChanged;
        _engine.TrackChanged += OnTrackChanged;
    }

    public PlayerState State => Map(_engine.State);

    public NowPlaying? NowPlaying => Describe(_engine.CurrentTrack, _engine.CurrentEntry);

    public TimeSpan Position => _engine.Position;

    public int Volume
    {
        get => _engine.Volume;
        set => _engine.Volume = Math.Clamp(value, 0, 100);
    }

    public event EventHandler<NowPlayingEventArgs>? TrackChanged;
    public event EventHandler<PlayerStateEventArgs>? StateChanged;

    public Task PlayAsync() => _engine.PlayAsync();
    public Task PauseAsync() => _engine.PauseAsync();
    public Task StopAsync() => _engine.StopAsync();
    public Task NextAsync() => _engine.NextAsync();
    public Task PreviousAsync() => _engine.PreviousAsync();
    public Task SeekAsync(TimeSpan position) => _engine.SeekAsync(position);

    public IReadOnlyList<PlaylistItem> GetActivePlaylist() => [.. _playlists.Active.Entries.Select(e => e.ToItem())];

    public Task AddToPlaylistAsync(IEnumerable<string> paths, bool play)
    {
        var list = paths.ToArray();
        return Dispatcher.UIThread.InvokeAsync(() => _controller().AddAsync(list, play: play));
    }

    public TrackTags? ReadTags(string path) => _tags.Read(path);

    public void Dispose()
    {
        _engine.StateChanged -= OnStateChanged;
        _engine.TrackChanged -= OnTrackChanged;
    }

    private void OnStateChanged(object? sender, PlaybackStateEventArgs e) =>
        StateChanged?.Invoke(this, new PlayerStateEventArgs(Map(e.State)));

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e)
    {
        if (e.Track is not null || _engine.State == PlaybackState.Stopped)
        {
            TrackChanged?.Invoke(this, new NowPlayingEventArgs(Describe(e.Track, e.Entry)));
        }
    }

    private static NowPlaying? Describe(TrackInfo? track, PlaylistEntry? entry) => track is null ? null : new NowPlaying(
        track.Path,
        entry?.Tags ?? new TrackTags(track.Title, track.Artist, Duration: track.Duration),
        track.Stream,
        track.Duration);

    private static PlayerState Map(PlaybackState state) => state switch
    {
        PlaybackState.Playing => PlayerState.Playing,
        PlaybackState.Paused => PlayerState.Paused,
        PlaybackState.Buffering => PlayerState.Buffering,
        _ => PlayerState.Stopped,
    };
}
