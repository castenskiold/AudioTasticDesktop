using System.Diagnostics;
using AudioTastic.Casting;
using AudioTastic.Casting.Chromecast;
using AudioTastic.Casting.Discovery;
using AudioTastic.Casting.Dlna;
using AudioTastic.Core.Audio;
using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Plugins.Host;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;
using AudioTastic.Sdk.Video;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Integration;

/// <summary>CN-09: the visualisation for one cast stream, and the plugin it came from (null for the built-in one).</summary>
public sealed record CastVisual(IVisualisation Visualisation, PluginRecord? Plugin);

/// <summary>CN-09: size and rate of the video sent to a TV.</summary>
public sealed record VideoCastFormat(int Width, int Height, int FramesPerSecond)
{
    /// <summary>About 6 Mbit/s at 1080p30 (visualisations are mostly black and compress well).</summary>
    public int Bitrate => (int)Math.Clamp((long)Width * Height * FramesPerSecond / 10, 1_000_000, 12_000_000);
}

/// <summary>
/// CN-09, "visuals only": the visualisation goes to a TV while the music plays on this computer. The streaming server
/// publishes a live stream; each request from the TV runs its own pipeline on a dedicated thread: every 1/fps
/// seconds it takes the newest post-DSP audio (VisualisationFeed), lets the visualisation draw a frame and encodes it
/// as H.264 (fragmented MP4 for Chromecast, MPEG-TS for DLNA TVs; tested live on Anders's devices). The TV shows the
/// picture a few seconds late; making it match the speakers (a look-ahead feed and a visual offset) is the next step.
/// </summary>
public sealed class VideoCast : IAsyncDisposable
{
    private readonly StreamServer _server;
    private readonly IVideoEncoderFactory _encoders;
    private readonly VisualisationFeed _feed;
    private readonly Func<CastVisual> _createVisual;
    private readonly PluginHost _plugins;
    private readonly VideoCastFormat _format;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private ChromecastSession? _chromecast;
    private UpnpRenderer? _dlna;
    private Uri? _url;
    private bool _lost;

    private VideoCast(CastDevice device, StreamServer server, IVideoEncoderFactory encoders, VisualisationFeed feed, Func<CastVisual> createVisual,
        PluginHost plugins, VideoCastFormat format, ILogger logger)
    {
        Device = device;
        _server = server;
        _encoders = encoders;
        _feed = feed;
        _createVisual = createVisual;
        _plugins = plugins;
        _format = format;
        _logger = logger;
    }

    public CastDevice Device { get; }

    /// <summary>The TV went away or someone else cast to it.</summary>
    public event EventHandler? Lost;

    /// <summary>The container each kind of device plays live (21 Nov 2026 tests): Chromecast fragmented MP4, DLNA MPEG-TS.</summary>
    public static VideoContainer ContainerFor(CastDevice device) =>
        (device ?? throw new ArgumentNullException(nameof(device))).Kind == CastDeviceKind.Chromecast ? VideoContainer.FragmentedMp4 : VideoContainer.MpegTs;

    /// <summary>Publishes the live stream and starts it on <paramref name="device"/>.</summary>
    public static async Task<VideoCast> StartAsync(CastDevice device, StreamServer server, IVideoEncoderFactory encoders, VisualisationFeed feed,
        Func<CastVisual> createVisual, PluginHost plugins, VideoCastFormat format, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(server);
        var cast = new VideoCast(device, server, encoders, feed, createVisual, plugins, format, logger);
        try
        {
            await cast.StartAsync(cancellationToken).ConfigureAwait(false);
            return cast;
        }
        catch
        {
            await cast.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StartAsync(CancellationToken cancellationToken)
    {
        var container = ContainerFor(Device);
        var (name, type) = container == VideoContainer.FragmentedMp4 ? ("visuals.mp4", "video/mp4") : ("visuals.ts", "video/mpeg");
        _url = _server.PublishLive(name, type, ProduceAsync);
        _logger.LogInformation("Casting visuals to {Device} as {Container} {Width}x{Height} at {Fps} fps", Device.Name, container, _format.Width, _format.Height,
            _format.FramesPerSecond);
        if (Device.Kind == CastDeviceKind.Chromecast)
        {
            _chromecast = await ChromecastSession.StartAsync(Device.Address, Device.Port, _logger, cancellationToken).ConfigureAwait(false);
            _chromecast.Closed += (_, _) => RaiseLost();
            await _chromecast.LoadAsync(new CastTrack(_url, type, "AudioTastic", "Visualisation", null, null, null), TimeSpan.Zero, cancellationToken,
                live: true, video: true).ConfigureAwait(false);
        }
        else
        {
            _dlna = new UpnpRenderer(Device);
            try
            {
                await _dlna.StopAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (UpnpException)
            {
                // nothing was playing
            }
            var didl = DlnaRemote.Didl(_url, $"http-get:*:{type}:{StreamServer.ContentFeatures(StreamFormat.Live)}", "AudioTastic", "Visualisation", null, null,
                null, "object.item.videoItem");
            await _dlna.SetUriAsync(_url, didl, cancellationToken).ConfigureAwait(false);
            await _dlna.PlayAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>One request from the TV: a pipeline on its own thread until the TV disconnects or casting stops.</summary>
    private Task ProduceAsync(Stream output, CancellationToken requestAborted) =>
        Task.Factory.StartNew(() =>
        {
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(requestAborted, _stopping.Token);
            Produce(output, stop.Token);
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private void Produce(Stream output, CancellationToken cancellationToken)
    {
        var visual = _createVisual();
        var renderer = new VisualFrameRenderer(visual.Visualisation, _format.Width, _format.Height);
        try
        {
            using var encoder = _encoders.Create(new VideoEncodeSettings(ContainerFor(Device), _format.Width, _format.Height, _format.FramesPerSecond, _format.Bitrate),
                output);
            _logger.LogInformation("Visuals stream to {Device} started ({Encoder})", Device.Name, encoder.EncoderName);
            var stereo = new float[SpectrumAnalyzer.Size * 2];
            var lastWritten = -1L;
            var clock = Stopwatch.StartNew();
            var late = 0;
            for (long n = 0; !cancellationToken.IsCancellationRequested; n++)
            {
                var due = TimeSpan.FromSeconds((double)n / _format.FramesPerSecond);
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    cancellationToken.WaitHandle.WaitOne(wait);
                }
                else if (wait < TimeSpan.FromSeconds(-1) && late++ % 300 == 0)
                {
                    _logger.LogWarning("The visuals stream to {Device} is {Seconds:0.0} s behind real time", Device.Name, -wait.TotalSeconds);
                }
                var written = _feed.WrittenFrames;
                var frames = 0;
                if (written != lastWritten)
                {
                    frames = _feed.ReadLatest(stereo); // nothing new while paused or stopped: the bars fall
                    lastWritten = written;
                }
                var time = clock.Elapsed;
                if (visual.Plugin is { } plugin)
                {
                    var count = frames;
                    if (!_plugins.Guard(plugin, () => renderer.Render(stereo.AsSpan(0, count * 2), new AudioFormat(_feed.SampleRate, 2), time)))
                    {
                        _logger.LogWarning("The visualisation plugin {Plugin} failed while casting; using the built-in spectrum", plugin.Manifest.Name);
                        renderer.Dispose();
                        visual = new CastVisual(new CastVisualisation(Data.Settings.VisualisationType.Spectrum), null);
                        renderer = new VisualFrameRenderer(visual.Visualisation, _format.Width, _format.Height);
                        continue;
                    }
                }
                else
                {
                    renderer.Render(stereo.AsSpan(0, frames * 2), new AudioFormat(_feed.SampleRate, 2), time);
                }
                encoder.WriteFrame(renderer.Pixels);
            }
        }
        catch (AudioSourceException ex)
        {
            _logger.LogWarning(ex, "Could not encode the visuals for {Device}", Device.Name);
        }
        finally
        {
            renderer.Dispose();
        }
    }

    private void RaiseLost()
    {
        if (!_lost)
        {
            _lost = true;
            Lost?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Stops the TV (unless someone else took it over) and the streams.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stopping.CancelAsync().ConfigureAwait(false);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try
        {
            if (_chromecast is not null)
            {
                if (!_lost)
                {
                    await _chromecast.StopReceiverAsync(timeout.Token).ConfigureAwait(false);
                }
                await _chromecast.DisposeAsync().ConfigureAwait(false);
            }
            if (_dlna is not null && !_lost)
            {
                await _dlna.StopAsync(timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
            _logger.LogDebug("Stopping the visuals on {Device}: {Message}", Device.Name, ex.Message);
        }
        _dlna?.Dispose();
        if (_url is not null)
        {
            _server.Revoke(_url);
        }
        _stopping.Dispose();
    }
}
