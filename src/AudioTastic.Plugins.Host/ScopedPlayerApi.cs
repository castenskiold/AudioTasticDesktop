using AudioTastic.Sdk.Metadata;
using AudioTastic.Sdk.Playlists;
using AudioTastic.Sdk.Plugins;

namespace AudioTastic.Plugins.Host;

/// <summary>
/// PG-03: the player API as one plugin sees it. Event handlers of the plugin run inside the fault guard (an exception
/// counts as a plugin fault instead of reaching the player), and all of them are dropped when the plugin is unloaded,
/// so a plugin that forgets to unsubscribe can still be unloaded and collected.
/// </summary>
internal sealed class ScopedPlayerApi : IPlayerApi, IDisposable
{
    private readonly IPlayerApi _player;
    private readonly PluginRecord _plugin;
    private readonly PluginHost _host;
    private readonly Lock _lock = new();
    private EventHandler<NowPlayingEventArgs>? _trackChanged;
    private EventHandler<PlayerStateEventArgs>? _stateChanged;
    private bool _disposed;

    public ScopedPlayerApi(IPlayerApi player, PluginRecord plugin, PluginHost host)
    {
        _player = player;
        _plugin = plugin;
        _host = host;
        _player.TrackChanged += OnTrackChanged;
        _player.StateChanged += OnStateChanged;
    }

    public PlayerState State => _player.State;
    public NowPlaying? NowPlaying => _player.NowPlaying;
    public TimeSpan Position => _player.Position;

    public int Volume
    {
        get => _player.Volume;
        set => _player.Volume = value;
    }

    public event EventHandler<NowPlayingEventArgs>? TrackChanged
    {
        add
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _trackChanged += value;
                }
            }
        }
        remove
        {
            lock (_lock)
            {
                _trackChanged -= value;
            }
        }
    }

    public event EventHandler<PlayerStateEventArgs>? StateChanged
    {
        add
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _stateChanged += value;
                }
            }
        }
        remove
        {
            lock (_lock)
            {
                _stateChanged -= value;
            }
        }
    }

    public Task PlayAsync() => _player.PlayAsync();
    public Task PauseAsync() => _player.PauseAsync();
    public Task StopAsync() => _player.StopAsync();
    public Task NextAsync() => _player.NextAsync();
    public Task PreviousAsync() => _player.PreviousAsync();
    public Task SeekAsync(TimeSpan position) => _player.SeekAsync(position);
    public IReadOnlyList<PlaylistItem> GetActivePlaylist() => _player.GetActivePlaylist();
    public Task AddToPlaylistAsync(IEnumerable<string> paths, bool play) => _player.AddToPlaylistAsync(paths, play);
    public TrackTags? ReadTags(string path) => _player.ReadTags(path);

    /// <summary>Drops the plugin's handlers and stops listening to the player (plugin unloaded).</summary>
    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _trackChanged = null;
            _stateChanged = null;
        }
        _player.TrackChanged -= OnTrackChanged;
        _player.StateChanged -= OnStateChanged;
    }

    private void OnTrackChanged(object? sender, NowPlayingEventArgs e)
    {
        EventHandler<NowPlayingEventArgs>? handlers;
        lock (_lock)
        {
            handlers = _trackChanged;
        }
        if (handlers is not null && _plugin.Status == PluginStatus.Enabled)
        {
            _host.Guard(_plugin, () => handlers(this, e));
        }
    }

    private void OnStateChanged(object? sender, PlayerStateEventArgs e)
    {
        EventHandler<PlayerStateEventArgs>? handlers;
        lock (_lock)
        {
            handlers = _stateChanged;
        }
        if (handlers is not null && _plugin.Status == PluginStatus.Enabled)
        {
            _host.Guard(_plugin, () => handlers(this, e));
        }
    }
}
