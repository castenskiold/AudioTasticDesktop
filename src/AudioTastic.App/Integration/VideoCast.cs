using System.Diagnostics;
using AudioTastic.Casting;
using AudioTastic.Casting.Chromecast;
using AudioTastic.Casting.Discovery;
using AudioTastic.Casting.Dlna;
using AudioTastic.Core.Audio;
using AudioTastic.Core.Audio.Beats;
using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Core.Playlists;
using AudioTastic.Plugins.Host;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;
using AudioTastic.Sdk.Video;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Integration;

/// <summary>CN-09: the visualisation for one cast stream, and the plugin it came from (null for the built-in one).</summary>
public sealed record CastVisual(IVisualisation Visualisation, PluginRecord? Plugin);

/// <summary>
/// CN-09: where the visuals' audio comes from: the playing file read ahead by <paramref name="Delay"/> (through
/// <paramref name="Decoders"/>), or the live post-DSP <paramref name="Feed"/> for streams.
/// </summary>
/// <param name="Calibrating">True while Picture delay… is open: the TV shows the kick flash instead.</param>
/// <param name="Beats">Beat grids (AN-02): the calibration flash follows them when a track has been analysed.</param>
public sealed record VisualAudio(VisualisationFeed Feed, Func<PlayingPoint> Playing, Func<TimeSpan> Delay, IEnumerable<IAudioSourceFactory> Decoders,
    Func<bool> Calibrating, Func<Core.Audio.Beats.IBeatInfoProvider?> Beats);

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
/// picture seconds late, so the visuals are drawn from the playing file read ahead by that delay (LookAheadAudio;
/// set per TV with Picture delay…); live streams cannot be read ahead and use the live feed.
/// </summary>
public sealed class VideoCast : IAsyncDisposable
{
    private readonly StreamServer _server;
    private readonly IVideoEncoderFactory _encoders;
    private readonly VisualisationFeed _feed;
    private readonly Func<PlayingPoint> _playing;
    private readonly Func<TimeSpan> _delay;
    private readonly IEnumerable<IAudioSourceFactory> _decoders;
    private readonly Func<bool> _calibrating;
    private readonly Func<Core.Audio.Beats.IBeatInfoProvider?> _beats;
    private readonly Func<CastVisual> _createVisual;
    private readonly PluginHost _plugins;
    private readonly VideoCastFormat _format;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private ChromecastSession? _chromecast;
    private UpnpRenderer? _dlna;
    private Uri? _url;
    private bool _lost;

    private VideoCast(CastDevice device, StreamServer server, IVideoEncoderFactory encoders, VisualAudio audio, Func<CastVisual> createVisual,
        PluginHost plugins, VideoCastFormat format, ILogger logger)
    {
        Device = device;
        _server = server;
        _encoders = encoders;
        _feed = audio.Feed;
        _playing = audio.Playing;
        _delay = audio.Delay;
        _decoders = audio.Decoders;
        _calibrating = audio.Calibrating;
        _beats = audio.Beats;
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
    public static async Task<VideoCast> StartAsync(CastDevice device, StreamServer server, IVideoEncoderFactory encoders, VisualAudio audio,
        Func<CastVisual> createVisual, PluginHost plugins, VideoCastFormat format, ILogger logger, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(server);
        ArgumentNullException.ThrowIfNull(audio);
        var cast = new VideoCast(device, server, encoders, audio, createVisual, plugins, format, logger);
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
        using var lookAhead = new LookAheadAudio(_playing, _decoders, _logger);
        VisualFrameRenderer? kick = null; // the calibration picture, made when first needed
        var flash = new KickFlashVisualisation();
        try
        {
            using var encoder = _encoders.Create(new VideoEncodeSettings(ContainerFor(Device), _format.Width, _format.Height, _format.FramesPerSecond, _format.Bitrate),
                output);
            _logger.LogInformation("Visuals stream to {Device} started ({Encoder})", Device.Name, encoder.EncoderName);
            var stereo = new float[SpectrumAnalyzer.Size * 2];
            var lastWritten = -1L;
            var clock = Stopwatch.StartNew();
            var late = 0;
            var frameLength = TimeSpan.FromSeconds(1.0 / _format.FramesPerSecond);
            for (long n = 0; !cancellationToken.IsCancellationRequested; n++)
            {
                var due = frameLength * n;
                var wait = due - clock.Elapsed;
                if (wait > TimeSpan.Zero)
                {
                    cancellationToken.WaitHandle.WaitOne(wait);
                }
                else if (wait < -frameLength)
                {
                    // Behind real time (a slow frame or a busy computer): skip to the current frame, so the TV gets a
                    // live picture at a lower frame rate instead of running out of video.
                    var skipped = (long)(-wait / frameLength);
                    n += skipped;
                    due = frameLength * n;
                    if (skipped >= 3 && late++ % 100 == 0)
                    {
                        _logger.LogWarning("The visuals for {Device} skipped {Frames} frames to stay live (the computer is too slow for this frame rate)",
                            Device.Name, skipped);
                    }
                }
                // The picture reaches the screen a delay late, so it is drawn from the audio the speakers will play then.
                var frames = 0;
                var rate = _feed.SampleRate;
                var now = _playing();
                if (now.Playing && now.Entry is { } entry && LookAheadAudio.CanReadAhead(entry) && !lookAhead.Failed)
                {
                    frames = lookAhead.Read(stereo, _delay());
                    rate = lookAhead.SampleRate;
                }
                else
                {
                    // Live streams cannot be read ahead: the picture follows the speakers' audio, a delay late.
                    var written = _feed.WrittenFrames;
                    if (written != lastWritten)
                    {
                        frames = _feed.ReadLatest(stereo); // nothing new while paused or stopped: the bars fall
                        lastWritten = written;
                    }
                }
                var time = clock.Elapsed;
                if (_calibrating())
                {
                    kick ??= new VisualFrameRenderer(flash, _format.Width, _format.Height);
                    flash.SinceBeat = SinceBeat(now);
                    kick.Render(stereo.AsSpan(0, frames * 2), new AudioFormat(rate, 2), time);
                    encoder.WriteFrame(kick.Pixels, due);
                    continue;
                }
                if (visual.Plugin is { } plugin)
                {
                    var count = frames;
                    var sampleRate = rate;
                    if (!_plugins.Guard(plugin, () => renderer.Render(stereo.AsSpan(0, count * 2), new AudioFormat(sampleRate, 2), time)))
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
                    renderer.Render(stereo.AsSpan(0, frames * 2), new AudioFormat(rate, 2), time);
                }
                encoder.WriteFrame(renderer.Pixels, due);
            }
        }
        catch (AudioSourceException ex)
        {
            _logger.LogWarning(ex, "Could not encode the visuals for {Device}", Device.Name);
        }
        finally
        {
            renderer.Dispose();
            kick?.Dispose();
        }
    }

    /// <summary>
    /// Seconds from the last kick of the track's beat grid (with its local corrections) to where the speakers will be
    /// when this frame is on screen; null when the track has no grid (the flash then detects kicks in the audio).
    /// </summary>
    private double? SinceBeat(PlayingPoint now)
    {
        if (!now.Playing || now.Entry is not { } entry || Grid(entry) is not { Confidence: >= 0.5 } grid)
        {
            return null;
        }
        var t = (now.Position + (_delay() * Math.Max(0.25, now.Speed))).TotalSeconds;
        var index = grid.BeatAtOrAfter(t);
        double Kick(long i) => grid.BeatTime(i) + grid.LocalOffset(grid.BeatTime(i));
        if (Kick(index) > t)
        {
            index--;
        }
        return t - Kick(index);
    }

    private PlaylistEntry? _gridEntry;
    private BeatInfo? _grid;
    private long _gridChecked;

    /// <summary>The entry's beat grid, read from the cache once (and again every 2 s while it is not analysed yet).</summary>
    private BeatInfo? Grid(PlaylistEntry entry)
    {
        if (!ReferenceEquals(entry, _gridEntry) || (_grid is null && Environment.TickCount64 - _gridChecked > 2000))
        {
            _gridEntry = entry;
            _gridChecked = Environment.TickCount64;
            _grid = _beats()?.TryGet(entry);
        }
        return _grid;
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
