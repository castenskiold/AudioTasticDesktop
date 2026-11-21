using AudioTastic.Casting;
using AudioTastic.Casting.Chromecast;
using AudioTastic.Casting.Dlna;
using AudioTastic.Casting.Discovery;
using AudioTastic.Core.Playback;
using AudioTastic.Data.Settings;
using AudioTastic.Metadata;
using AudioTastic.Plugins.Host;
using AudioTastic.Sdk.Plugins;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Integration;

/// <summary>
/// CN-01 / CN-02 / CN-05: casting in the player. Finds devices (DeviceDiscovery), starts the streaming server on first use
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
    private IAsyncDisposable? _remote;
    private VideoCast? _video;
    private readonly OutputRouter? _router;

    public CastService(PlaybackEngine engine, PluginHost plugins, TagService tags, ArtService art, SettingsStore settings, ILoggerFactory loggers,
        Sdk.Audio.IAudioOutput output)
    {
        _router = output as OutputRouter;
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

    /// <summary>CN-09: the TV showing the visuals while the music plays here ("visuals only"), or null.</summary>
    public CastDevice? VideoDevice => _video is { WithAudio: false } video ? video.Device : null;

    /// <summary>CN-09: the TV playing the player's mix with the visualisation ("audio and video"), or null.</summary>
    public CastDevice? AudioVideoDevice => _video is { WithAudio: true } video ? video.Device : null;

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

    /// <summary>True for the device kinds this version can cast to (Chromecast and DLNA; AirPlay follows).</summary>
    public static bool CanCastTo(CastDevice device) => device.Kind is CastDeviceKind.Chromecast or CastDeviceKind.Dlna;

    /// <summary>CN-09: true when <paramref name="device"/> has a screen and this machine can encode the video it plays.</summary>
    public bool CanCastVisuals(CastDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return device.HasScreen == true && CanCastTo(device) && VideoEncoder(device) is not null;
    }

    private Sdk.Video.IVideoEncoderFactory? VideoEncoder(CastDevice device) =>
        _plugins.VideoEncoderFactories.FirstOrDefault(e => e.Containers.Contains(VideoCast.ContainerFor(device)));

    /// <summary>
    /// CN-09 "visuals only": the visualisation goes to <paramref name="device"/> while the music keeps playing on this
    /// computer (audio casting stops first: the visuals follow what plays here).
    /// </summary>
    public Task CastVisualsAsync(CastDevice device, CancellationToken cancellationToken = default) => StartVideoAsync(device, withAudio: false, cancellationToken);

    /// <summary>
    /// CN-09 "audio and video": the player's own mix (gapless, crossfades, EQ and volume included) and the
    /// visualisation drawn from it go to <paramref name="device"/> in one stream; the speakers here go quiet. The
    /// player keeps the transport; the TV plays about two seconds behind it.
    /// </summary>
    public Task CastAudioVideoAsync(CastDevice device, CancellationToken cancellationToken = default) => StartVideoAsync(device, withAudio: true, cancellationToken);

    private async Task StartVideoAsync(CastDevice device, bool withAudio, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        var encoder = VideoEncoder(device) ?? throw new NotSupportedException("This computer cannot encode video for that device.");
        if (withAudio && _router is null)
        {
            throw new NotSupportedException("The output cannot be sent to a TV here.");
        }
        await _switching.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await StopAudioCoreAsync().ConfigureAwait(false);
            await StopVideoCoreAsync().ConfigureAwait(false);
            var server = await EnsureServerAsync(cancellationToken).ConfigureAwait(false);
            var settings = _settings.Current.CastingNetwork;
            var height = settings.VideoHeight >= 1080 ? 1080 : 720;
            var format = new VideoCastFormat(height * 16 / 9, height, Math.Clamp(settings.VideoFps, 15, 60));
            var audio = new VisualAudio(_engine.Graph.Visualisation, () => Core.Audio.PlayingPoint.Of(_engine), () => VisualDelay(device), _plugins.LiveSourceFactories,
                () => Volatile.Read(ref _calibrating), () => _engine.BeatProvider);
            NetworkAudioOutput? mix = null;
            if (withAudio)
            {
                mix = new NetworkAudioOutput(device.Name);
                _router!.Divert(mix);
                await _engine.ReopenOutputAsync().ConfigureAwait(false);
            }
            try
            {
                var video = await VideoCast.StartAsync(device, server, encoder, audio, CreateVisual, _plugins, format, mix, _engine.Volume,
                    _loggers.CreateLogger<VideoCast>(), cancellationToken).ConfigureAwait(false);
                video.Lost += OnVideoLost;
                _video = video;
                if (withAudio)
                {
                    // The mix goes at full level; the slider moves the TV's own volume.
                    _engine.VolumeOnDevice = true;
                    _engine.VolumeChanged += OnVolumeChanged;
                }
            }
            catch
            {
                if (mix is not null)
                {
                    await UndivertAsync().ConfigureAwait(false);
                }
                throw;
            }
        }
        finally
        {
            _switching.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool _calibrating;

    /// <summary>CN-09: while true (Picture delay… is open) the TV shows the kick flash, which makes the delay easy to judge.</summary>
    public bool Calibrating
    {
        get => Volatile.Read(ref _calibrating);
        set => Volatile.Write(ref _calibrating, value);
    }

    // Read every frame, so kept here: SettingsStore.Current copies all settings through JSON on every call.
    private System.Collections.Concurrent.ConcurrentDictionary<string, int>? _delays;

    /// <summary>CN-09: how late <paramref name="device"/> shows the picture (Picture delay…, by device id).</summary>
    public TimeSpan VisualDelay(CastDevice device)
    {
        ArgumentNullException.ThrowIfNull(device);
        _delays ??= new(_settings.Current.CastingNetwork.VisualDelays);
        return TimeSpan.FromMilliseconds(_delays.TryGetValue(device.Id, out var ms) ? ms : CastingNetworkSettings.DefaultVisualDelayMs);
    }

    /// <summary>Stores the picture delay for <paramref name="device"/>; the running stream uses it from the next frame.</summary>
    public void SetVisualDelay(CastDevice device, int milliseconds)
    {
        ArgumentNullException.ThrowIfNull(device);
        var value = Math.Clamp(milliseconds, 0, 15_000);
        _delays ??= new(_settings.Current.CastingNetwork.VisualDelays);
        _delays[device.Id] = value;
        _settings.Update(SettingsGroup.CastingNetwork, s => s.CastingNetwork.VisualDelays[device.Id] = value);
    }

    /// <summary>The visualisation chosen in Settings › Casting &amp; network: built-in, or a plugin that draws off-screen.</summary>
    private CastVisual CreateVisual()
    {
        var choice = _settings.Current.CastingNetwork.VideoVisualisation;
        if (choice is not "spectrum" and not "oscilloscope")
        {
            var record = _plugins.Plugins.FirstOrDefault(p => string.Equals(p.Id, choice, StringComparison.OrdinalIgnoreCase) && p.Status == PluginStatus.Enabled);
            IVisualisation? visualisation = null;
            if (record?.Instances.OfType<IVisualisationPlugin>().FirstOrDefault() is { } plugin)
            {
                // Plugin objects are created on the UI thread, like their windows.
                Dispatcher.UIThread.Invoke(() => _plugins.Guard(record, () => visualisation = plugin.CanRenderOffscreen ? plugin.Create() : null));
            }
            if (visualisation is IOffscreenVisualisation)
            {
                return new CastVisual(visualisation, record);
            }
            visualisation?.Dispose();
            _logger.LogWarning("The visualisation {Choice} cannot be cast; using the built-in spectrum", choice);
        }
        var type = choice == "oscilloscope" ? VisualisationType.Oscilloscope : VisualisationType.Spectrum;
        return new CastVisual(new CastVisualisation(type, 48, _settings.Current.Interface.PeakFallSpeed), null);
    }

    private void OnVideoLost(object? sender, EventArgs e) => _ = Task.Run(async () =>
    {
        await _switching.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(sender, _video))
            {
                _logger.LogInformation("The visuals' TV went away or was taken over");
                await StopVideoCoreAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _switching.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    });

    private async Task StopVideoCoreAsync()
    {
        if (_video is { } video)
        {
            _video = null;
            video.Lost -= OnVideoLost;
            if (video.WithAudio)
            {
                _engine.VolumeChanged -= OnVolumeChanged;
                _engine.VolumeOnDevice = false;
                await UndivertAsync().ConfigureAwait(false); // the speakers here take over again
            }
            await video.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void OnVolumeChanged(object? sender, EventArgs e)
    {
        if (_video is { WithAudio: true } video)
        {
            _ = video.SetVolumeAsync(_engine.Volume);
        }
    }

    private async Task UndivertAsync()
    {
        _router?.Divert(null);
        await _engine.ReopenOutputAsync().ConfigureAwait(false);
    }

    private async Task StopAudioCoreAsync()
    {
        var remote = _remote;
        _remote = null;
        Current = null;
        if (_engine.Remote is not null)
        {
            await _engine.SetRemoteAsync(null).ConfigureAwait(false);
        }
        if (remote is not null)
        {
            await remote.DisposeAsync().ConfigureAwait(false);
        }
    }

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
            await StopVideoCoreAsync().ConfigureAwait(false); // the visuals follow local playback, which moves to the device
            var server = await EnsureServerAsync(cancellationToken).ConfigureAwait(false);
            IRemotePlayer remote = device.Kind == CastDeviceKind.Dlna
                ? await DlnaRemote.ConnectAsync(device, server, Extras, _engine.Volume, _loggers.CreateLogger<DlnaRemote>(), cancellationToken)
                    .ConfigureAwait(false)
                : await ChromecastRemote.ConnectAsync(device, server, Extras, _loggers.CreateLogger<ChromecastRemote>(), cancellationToken)
                    .ConfigureAwait(false);
            var previous = _remote;
            _remote = (IAsyncDisposable)remote;
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

    /// <summary>Moves playback back to this computer at the device's position, and stops casting visuals.</summary>
    public async Task StopCastingAsync()
    {
        await _switching.WaitAsync().ConfigureAwait(false);
        try
        {
            await StopAudioCoreAsync().ConfigureAwait(false);
            await StopVideoCoreAsync().ConfigureAwait(false);
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
            _video?.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(4));
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
