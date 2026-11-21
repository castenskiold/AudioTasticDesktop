using AudioTastic.Casting;
using AudioTastic.Casting.Chromecast;
using AudioTastic.Casting.Discovery;
using AudioTastic.Core.Playback;
using AudioTastic.Data.Settings;
using AudioTastic.Metadata;
using AudioTastic.Plugins.Host;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Integration;

/// <summary>
/// CN-01 / CN-05: casting in the player. Finds devices (DeviceDiscovery), starts the streaming server on first use
/// (CN-04), connects to the chosen device and hands playback to it (PlaybackEngine.SetRemoteAsync), and back to this
/// computer. When the device goes away the engine continues here, paused, and this service lets go of the device.
/// </summary>
public sealed class CastService : IDisposable
{
    private readonly PlaybackEngine _engine;
    private readonly PluginHost _plugins;
    private readonly TagService _tags;
    private readonly ArtService _art;
    private readonly SettingsStore _settings;
    private readonly ILoggerFactory _loggers;
    private readonly ILogger<CastService> _logger;
    private readonly DeviceDiscovery _discovery;
    private readonly SemaphoreSlim _switching = new(1, 1);
    private StreamServer? _server;
    private ChromecastRemote? _remote;

    public CastService(PlaybackEngine engine, PluginHost plugins, TagService tags, ArtService art, SettingsStore settings, ILoggerFactory loggers)
    {
        _engine = engine;
        _plugins = plugins;
        _tags = tags;
        _art = art;
        _settings = settings;
        _loggers = loggers;
        _logger = loggers.CreateLogger<CastService>();
        _discovery = new DeviceDiscovery(loggers.CreateLogger<DeviceDiscovery>());
        _engine.RemoteChanged += OnRemoteChanged;
    }

    /// <summary>The device playing now, or null (this computer).</summary>
    public CastDevice? Current { get; private set; }

    /// <summary>The devices found by the latest scan.</summary>
    public IReadOnlyList<CastDevice> Devices => _discovery.Devices;

    /// <summary>Casting started or stopped, or the device list changed (any thread).</summary>
    public event EventHandler? Changed;

    /// <summary>Looks for devices on the local network (about 3 seconds).</summary>
    public async Task<IReadOnlyList<CastDevice>> ScanAsync(CancellationToken cancellationToken = default)
    {
        var devices = await _discovery.ScanAsync(null, cancellationToken).ConfigureAwait(false);
        Changed?.Invoke(this, EventArgs.Empty);
        return devices;
    }

    /// <summary>True for the device kinds this version can cast to (Chromecast now; DLNA and AirPlay follow).</summary>
    public static bool CanCastTo(CastDevice device) => device.Kind == CastDeviceKind.Chromecast;

    /// <summary>Moves playback to <paramref name="device"/> at the current track and position.</summary>
    public async Task CastToAsync(CastDevice device, CancellationToken cancellationToken = default)
    {
        if (!CanCastTo(device))
        {
            throw new NotSupportedException($"Casting to {device.Kind} devices comes in a later step.");
        }
        await _switching.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var server = await EnsureServerAsync(cancellationToken).ConfigureAwait(false);
            var remote = await ChromecastRemote.ConnectAsync(device, server, Extras, _loggers.CreateLogger<ChromecastRemote>(), cancellationToken)
                .ConfigureAwait(false);
            var previous = _remote;
            _remote = remote;
            Current = device;
            await _engine.SetRemoteAsync(remote).ConfigureAwait(false);
            if (previous is not null)
            {
                await previous.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _switching.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Moves playback back to this computer at the device's position.</summary>
    public async Task StopCastingAsync()
    {
        await _switching.WaitAsync().ConfigureAwait(false);
        try
        {
            var remote = _remote;
            _remote = null;
            Current = null;
            await _engine.SetRemoteAsync(null).ConfigureAwait(false);
            if (remote is not null)
            {
                await remote.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _switching.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<StreamServer> EnsureServerAsync(CancellationToken cancellationToken)
    {
        if (_server is { IsRunning: true } running)
        {
            return running;
        }
        var server = new StreamServer(_plugins.LiveSourceFactories, _plugins.LiveEncoderFactories, _loggers.CreateLogger<StreamServer>());
        await server.StartAsync(_settings.Current.CastingNetwork.ServerPort, null, cancellationToken).ConfigureAwait(false);
        _server = server;
        return server;
    }

    /// <summary>Album and cover for the TV (the cover only when album art is switched on).</summary>
    private CastExtras? Extras(string path)
    {
        try
        {
            var album = _tags.Read(path)?.Album;
            var cover = _settings.Current.Metadata.AlbumArt ? _art.Find(path)?.Picture : null;
            return new CastExtras(album, cover?.Data, cover?.MimeType);
        }
#pragma warning disable CA1031 // Album and cover are extras: never stop casting over them.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "No album or cover for {Path}", path);
            return null;
        }
    }

    /// <summary>The engine let go of the device (it went away): release it here too.</summary>
    private void OnRemoteChanged(object? sender, EventArgs e)
    {
        if (_engine.Remote is null && _remote is { } lost)
        {
            _remote = null;
            Current = null;
            _ = lost.DisposeAsync().AsTask();
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>On exit: the TV returns to its idle screen; playback does not move back to this computer.</summary>
    public void Dispose()
    {
        _engine.RemoteChanged -= OnRemoteChanged;
        try
        {
            _remote?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(4));
            _server?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex)
        {
            _logger.LogDebug(ex, "Casting did not close cleanly");
        }
        _discovery.Dispose();
        _switching.Dispose();
    }
}
