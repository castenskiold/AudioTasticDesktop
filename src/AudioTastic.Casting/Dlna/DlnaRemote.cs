using System.Xml.Linq;
using AudioTastic.Casting.Discovery;
using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Casting.Dlna;

/// <summary>
/// CN-02: the engine's remote player for a DLNA/UPnP media renderer (smart TVs, network receivers). Each track is
/// published on the streaming server (CN-04): the file itself when the renderer lists its MIME type, otherwise WAV
/// (exact length, so the renderer can seek by byte ranges), else FLAC or MP3 made on the fly. The renderer is told the
/// URL with DIDL-Lite metadata (title, artist, album, cover), then Play; the position, state and end of track come
/// from polling GetTransportInfo/GetPositionInfo once a second. The TV keeps its own volume when casting starts; the
/// player's volume changes it in proportion.
/// </summary>
public sealed class DlnaRemote : IRemotePlayer, IAsyncDisposable
{
    private static readonly HashSet<string> DirectCodecs = new(StringComparer.OrdinalIgnoreCase) { "mp3", "aac", "flac", "vorbis", "pcm_s16le", "pcm_s24le" };

    /// <summary>MIME types and the other spellings renderers list for them.</summary>
    private static readonly Dictionary<string, string[]> Spellings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio/mpeg"] = ["audio/mpeg", "audio/mp3", "audio/x-mp3", "audio/mpeg3"],
        ["audio/flac"] = ["audio/flac", "audio/x-flac"],
        ["audio/wav"] = ["audio/wav", "audio/x-wav", "audio/wave", "audio/vnd.wave"],
        ["audio/mp4"] = ["audio/mp4", "audio/x-m4a", "audio/m4a", "audio/x-mp4", "audio/aac"],
        ["audio/aac"] = ["audio/aac", "audio/x-aac", "audio/vnd.dlna.adts", "audio/aacp"],
        ["audio/ogg"] = ["audio/ogg", "application/ogg", "audio/x-ogg", "audio/vorbis"],
    };

    private readonly UpnpRenderer _renderer;
    private readonly StreamServer _server;
    private readonly Func<string, CastExtras?> _extras;
    private readonly ILogger _logger;
    private readonly IReadOnlySet<string> _sink;
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _closing = new();
    private readonly SemaphoreSlim _commands = new(1, 1);
    private readonly Task _poller;
    private readonly int? _deviceVolume;
    private readonly int _playerVolume;
    private bool? _muted;

    private Uri? _url;
    private Uri? _cover;
    private string? _token;
    private string _contentType = "audio/wav";
    private StreamFormat _format;
    private bool _live;
    private string _title = "";
    private string? _artist;
    private string? _album;
    private TimeSpan? _duration;
    private TimeSpan _fileStart;
    private TimeSpan? _fileEnd;
    private TimeSpan _offset;
    private string _state = "STOPPED";
    private TimeSpan _relTime;
    private long _statusTicks;
    private bool _busy;
    private bool _playingSeen;
    private bool _ended;
    private bool _stopped;
    private int _failures;
    private int _foreign;
    private bool _lost;

    private DlnaRemote(CastDevice device, UpnpRenderer renderer, StreamServer server, Func<string, CastExtras?> extras, IReadOnlySet<string> sink,
        int? deviceVolume, int playerVolume, ILogger logger)
    {
        Name = device.Name;
        _renderer = renderer;
        _server = server;
        _extras = extras;
        _sink = sink;
        _deviceVolume = deviceVolume;
        _playerVolume = playerVolume;
        _logger = logger;
        _poller = Task.Run(PollAsync);
    }

    public string Name { get; }

    public event EventHandler? Lost;

    /// <summary>
    /// Connects to <paramref name="device"/>: reads the formats it accepts and its volume (the starting point for the
    /// player's volume, <paramref name="playerVolume"/>).
    /// </summary>
    public static async Task<DlnaRemote> ConnectAsync(CastDevice device, StreamServer server, Func<string, CastExtras?> extras, int playerVolume,
        ILogger logger, CancellationToken cancellationToken, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var renderer = new UpnpRenderer(device, http);
        try
        {
            IReadOnlySet<string> sink;
            try
            {
                sink = await renderer.GetSinkTypesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (UpnpException ex)
            {
                logger.LogInformation("{Device} did not list its formats ({Message}); assuming MP3, FLAC and WAV", device.Name, ex.Message);
                sink = new HashSet<string>();
            }
            // The renderer must answer AVTransport before we take over playback.
            var state = await renderer.GetTransportInfoAsync(cancellationToken).ConfigureAwait(false);
            int? volume = null;
            try
            {
                volume = await renderer.GetVolumeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (UpnpException ex)
            {
                logger.LogDebug("{Device} did not tell its volume: {Message}", device.Name, ex.Message);
            }
            logger.LogInformation("DLNA renderer {Device}: {State}, volume {Volume}, {Count} formats ({Formats})", device.Name, state.State, volume,
                sink.Count, string.Join(' ', sink.Where(t => t.StartsWith("audio/", StringComparison.Ordinal))));
            return new DlnaRemote(device, renderer, server, extras, sink, volume, playerVolume, logger);
        }
        catch
        {
            renderer.Dispose();
            throw;
        }
    }

    /// <summary>
    /// How a track reaches the renderer (public for tests): the original file when the renderer lists its type, else
    /// WAV, else FLAC or MP3 when they can be made; with the MIME spelling the renderer listed. An empty
    /// <paramref name="sink"/> (not reported) counts as MP3, FLAC and WAV.
    /// </summary>
    public static (StreamFormat Format, string ContentType) FormatFor(string path, string codec, IReadOnlySet<string> sink,
        IReadOnlyCollection<StreamFormat> available)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(available);
        var original = StreamServer.ContentTypeOf(path).Split(';')[0];
        if (DirectCodecs.Contains(codec) && Accepted(original, sink) is { } type)
        {
            return (StreamFormat.Original, type);
        }
        if (Accepted("audio/wav", sink) is { } wav)
        {
            return (StreamFormat.Wav, wav);
        }
        if (available.Contains(StreamFormat.Flac) && Accepted("audio/flac", sink) is { } flac)
        {
            return (StreamFormat.Flac, flac);
        }
        if (available.Contains(StreamFormat.Mp3) && Accepted("audio/mpeg", sink) is { } mp3)
        {
            return (StreamFormat.Mp3, mp3);
        }
        return (StreamFormat.Wav, "audio/wav");
    }

    /// <summary>The spelling of <paramref name="type"/> the renderer listed, or null when it does not accept it.</summary>
    private static string? Accepted(string type, IReadOnlySet<string> sink)
    {
        if (sink.Count == 0)
        {
            return type is "audio/mpeg" or "audio/flac" or "audio/wav" ? type : null;
        }
        var spellings = Spellings.TryGetValue(type, out var list) ? list : [type];
        return spellings.FirstOrDefault(sink.Contains);
    }

    /// <summary>The DIDL-Lite description of a track for SetAVTransportURI (public for tests).</summary>
    public static string Didl(Uri url, string protocolInfo, string title, string? artist, string? album, Uri? cover, TimeSpan? duration)
    {
        ArgumentNullException.ThrowIfNull(url);
        XNamespace didl = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
        XNamespace dc = "http://purl.org/dc/elements/1.1/";
        XNamespace upnp = "urn:schemas-upnp-org:metadata-1-0/upnp/";
        XNamespace dlna = "urn:schemas-dlna-org:metadata-1-0/";
        var res = new XElement(didl + "res", new XAttribute("protocolInfo", protocolInfo), url.AbsoluteUri); // AbsoluteUri keeps %20 etc.; ToString() unescapes them
        if (duration is { } length)
        {
            res.Add(new XAttribute("duration", $"{UpnpRenderer.FormatTime(length)}.000"));
        }
        var item = new XElement(didl + "item", new XAttribute("id", "1"), new XAttribute("parentID", "0"), new XAttribute("restricted", "1"),
            new XElement(dc + "title", title),
            artist is null ? null : new XElement(dc + "creator", artist),
            artist is null ? null : new XElement(upnp + "artist", artist),
            album is null ? null : new XElement(upnp + "album", album),
            cover is null ? null : new XElement(upnp + "albumArtURI", new XAttribute(dlna + "profileID", "JPEG_TN"), cover.AbsoluteUri),
            new XElement(upnp + "class", "object.item.audioItem.musicTrack"),
            res);
        var root = new XElement(didl + "DIDL-Lite",
            new XAttribute("xmlns", didl.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dc", dc.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "upnp", upnp.NamespaceName),
            new XAttribute(XNamespace.Xmlns + "dlna", dlna.NamespaceName),
            item);
        return root.ToString(SaveOptions.DisableFormatting);
    }

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                var time = _relTime;
                if (_state == "PLAYING" && !_busy)
                {
                    time += TimeSpan.FromMilliseconds(Environment.TickCount64 - _statusTicks);
                }
                var position = _offset + time - _fileStart;
                if (_duration is { } length && _fileEnd is null && position > length)
                {
                    position = length;
                }
                return position < TimeSpan.Zero ? TimeSpan.Zero : position;
            }
        }
    }

    public bool HasEnded
    {
        get
        {
            lock (_gate)
            {
                if (_ended)
                {
                    return true;
                }
            }
            return _fileEnd is { } end && _playingSeen && Position >= end - _fileStart;
        }
    }

    public async Task LoadAsync(PlaylistEntry entry, TrackInfo track, TimeSpan start, bool paused)
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(track);
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            RevokeUrls();
            var extras = entry.IsUrl ? null : _extras(entry.Path);
            lock (_gate)
            {
                _busy = true;
                _ended = false;
                _playingSeen = false;
                _live = entry.IsUrl;
                _fileStart = entry.Start ?? TimeSpan.Zero;
                _fileEnd = entry.End;
                _offset = TimeSpan.Zero;
                _relTime = TimeSpan.Zero;
                _foreign = 0;
            }
            _title = track.Title;
            _artist = track.Artist;
            _album = extras?.Album;
            _duration = entry.IsUrl ? null : track.Duration;
            if (extras?.Cover is { Length: > 0 } cover)
            {
                _cover = _server.PublishData(cover, extras.CoverType ?? "image/jpeg", "cover" + (extras.CoverType == "image/png" ? ".png" : ".jpg"));
            }
            if (entry.IsUrl)
            {
                _format = StreamFormat.Original;
                _contentType = "audio/mpeg";
                _url = new Uri(entry.Path);
                _token = null;
                _logger.LogInformation("Casting {Url} to {Device}", entry.Path, Name);
                await StartAsync(_url, null, TimeSpan.Zero, paused).ConfigureAwait(false);
                return;
            }
            (_format, _contentType) = FormatFor(entry.Path, track.Stream.Codec, _sink, _server.Formats);
            _url = _server.Publish(entry.Path, _format, _contentType);
            _token = _url.Segments.Length > 2 ? _url.Segments[2].TrimEnd('/') : null;
            _logger.LogInformation("Casting {Path} to {Device} as {Format} ({Type})", entry.Path, Name, _format, _contentType);
            await StartAtAsync(_fileStart + start, paused).ConfigureAwait(false);
        }
        finally
        {
            lock (_gate)
            {
                _busy = false;
                _statusTicks = Environment.TickCount64;
            }
            _commands.Release();
        }
    }

    /// <summary>Plays the current file from <paramref name="fileTime"/>: made-on-the-fly streams start there, others seek there.</summary>
    private async Task StartAtAsync(TimeSpan fileTime, bool paused)
    {
        if (_format is StreamFormat.Flac or StreamFormat.Mp3)
        {
            lock (_gate)
            {
                _offset = fileTime;
            }
            var url = fileTime > TimeSpan.Zero ? StreamServer.StartingAt(_url!, fileTime) : _url!;
            await StartAsync(url, fileTime > TimeSpan.Zero ? null : _duration, TimeSpan.Zero, paused).ConfigureAwait(false);
        }
        else
        {
            await StartAsync(_url!, _duration, fileTime, paused).ConfigureAwait(false);
        }
    }

    /// <summary>Stop → SetAVTransportURI → Play, then the seek to <paramref name="seekTo"/> once it plays, then Pause if asked.</summary>
    private async Task StartAsync(Uri url, TimeSpan? duration, TimeSpan seekTo, bool paused)
    {
        var ct = _closing.Token;
        lock (_gate)
        {
            _stopped = true; // the Stop below is ours, not the end of a track
        }
        try
        {
            await _renderer.StopAsync(ct).ConfigureAwait(false);
        }
        catch (UpnpException ex)
        {
            _logger.LogDebug("Stop before loading: {Message}", ex.Message); // many TVs refuse Stop when nothing plays
        }
        var features = _live ? "*" : StreamServer.ContentFeatures(_format);
        var metadata = Didl(url, $"http-get:*:{_contentType}:{features}", _title, _artist, _album, _cover, duration);
        await _renderer.SetUriAsync(url, metadata, ct).ConfigureAwait(false);
        await _renderer.PlayAsync(ct).ConfigureAwait(false);
        lock (_gate)
        {
            _stopped = false;
        }
        if (seekTo > TimeSpan.FromSeconds(1) || paused)
        {
            // Renderers seek and pause only once the media is open.
            var deadline = Environment.TickCount64 + 10_000;
            while (Environment.TickCount64 < deadline)
            {
                var info = await _renderer.GetTransportInfoAsync(ct).ConfigureAwait(false);
                if (info.State is "PLAYING" or "PAUSED_PLAYBACK")
                {
                    break;
                }
                await Task.Delay(250, ct).ConfigureAwait(false);
            }
            if (seekTo > TimeSpan.FromSeconds(1))
            {
                await _renderer.SeekAsync(seekTo, ct).ConfigureAwait(false);
                lock (_gate)
                {
                    _relTime = seekTo;
                }
            }
            if (paused)
            {
                await _renderer.PauseAsync(ct).ConfigureAwait(false);
            }
        }
    }

    public async Task PauseAsync()
    {
        await RunAsync(ct => _renderer.PauseAsync(ct)).ConfigureAwait(false);
        var position = Position;
        lock (_gate)
        {
            _relTime = position + _fileStart - _offset;
            _state = "PAUSED_PLAYBACK";
            _statusTicks = Environment.TickCount64;
        }
    }

    public async Task ResumeAsync()
    {
        await RunAsync(ct => _renderer.PlayAsync(ct)).ConfigureAwait(false);
        lock (_gate)
        {
            _state = "PLAYING";
            _statusTicks = Environment.TickCount64;
        }
    }

    public async Task StopAsync()
    {
        lock (_gate)
        {
            _stopped = true;
        }
        await RunAsync(ct => _renderer.StopAsync(ct)).ConfigureAwait(false);
        RevokeUrls();
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_live || _url is null)
        {
            return;
        }
        var fileTime = _fileStart + position;
        if (_format is StreamFormat.Flac or StreamFormat.Mp3)
        {
            bool paused;
            lock (_gate)
            {
                paused = _state == "PAUSED_PLAYBACK";
                _busy = true;
            }
            await _commands.WaitAsync().ConfigureAwait(false);
            try
            {
                await StartAtAsync(fileTime, paused).ConfigureAwait(false); // restart the stream there
            }
            finally
            {
                lock (_gate)
                {
                    _relTime = TimeSpan.Zero;
                    _busy = false;
                    _statusTicks = Environment.TickCount64;
                }
                _commands.Release();
            }
            return;
        }
        await RunAsync(ct => _renderer.SeekAsync(fileTime, ct)).ConfigureAwait(false);
        lock (_gate)
        {
            _relTime = fileTime - _offset;
            _statusTicks = Environment.TickCount64;
        }
    }

    /// <summary>The player's volume moves the device's in proportion to where both were when casting started.</summary>
    public async Task SetVolumeAsync(int volume, bool muted)
    {
        if (_muted != muted)
        {
            _muted = muted;
            await RunAsync(ct => _renderer.SetMuteAsync(muted, ct)).ConfigureAwait(false);
        }
        if (DeviceVolume(_deviceVolume, _playerVolume, volume) is { } level)
        {
            await RunAsync(ct => _renderer.SetVolumeAsync(level, ct)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The device volume for player volume <paramref name="volume"/>, scaled from the device's volume
    /// <paramref name="startDevice"/> at player volume <paramref name="startPlayer"/> (public for tests); null when the
    /// device's volume is unknown.
    /// </summary>
    public static int? DeviceVolume(int? startDevice, int startPlayer, int volume)
    {
        if (startDevice is not { } device)
        {
            return null;
        }
        if (startPlayer <= 0 || device <= 0)
        {
            return Math.Clamp((int)Math.Round(volume * 0.3), 0, 100); // no reference: a third of the scale at most
        }
        return Math.Clamp((int)Math.Round(device * (double)volume / startPlayer), 0, 100);
    }

    private async Task RunAsync(Func<CancellationToken, Task> command)
    {
        await _commands.WaitAsync().ConfigureAwait(false);
        try
        {
            await command(_closing.Token).ConfigureAwait(false);
        }
        finally
        {
            _commands.Release();
        }
    }

    /// <summary>Once a second: state, position and the URI the renderer plays; end of track, takeover and loss.</summary>
    private async Task PollAsync()
    {
        var ct = _closing.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                if (_url is null || Volatile.Read(ref _busy))
                {
                    continue;
                }
                var info = await _renderer.GetTransportInfoAsync(ct).ConfigureAwait(false);
                var position = await _renderer.GetPositionInfoAsync(ct).ConfigureAwait(false);
                Update(info, position);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (UpnpException ex)
            {
                if (++_failures == 5)
                {
                    _logger.LogWarning("{Device} stopped answering ({Message}); playing on this computer again", Name, ex.Message);
                    RaiseLost();
                    return;
                }
            }
        }
    }

    private void Update(UpnpTransportInfo info, UpnpPositionInfo position)
    {
        _failures = 0;
        var foreign = false;
        lock (_gate)
        {
            if (_busy)
            {
                return;
            }
            _state = info.State;
            _relTime = position.RelTime;
            _statusTicks = Environment.TickCount64;
            if (info.State == "PLAYING")
            {
                _playingSeen = true;
            }
            if (_playingSeen && !_stopped && info.State is "STOPPED" or "NO_MEDIA_PRESENT")
            {
                _ended = true;
            }
            // Someone else cast to the TV: it plays a URI that is not ours.
            if (_token is not null && !string.IsNullOrEmpty(position.TrackUri) && _playingSeen
                && !position.TrackUri.Contains(_token, StringComparison.OrdinalIgnoreCase))
            {
                foreign = ++_foreign >= 3;
            }
            else
            {
                _foreign = 0;
            }
        }
        if (foreign)
        {
            _logger.LogInformation("{Device} is playing something else; playing on this computer again", Name);
            RaiseLost();
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

    private void RevokeUrls()
    {
        if (_url is { } url && !_live)
        {
            _server.Revoke(url);
        }
        if (_cover is { } cover)
        {
            _server.Revoke(cover);
        }
        _url = null;
        _cover = null;
        _token = null;
    }

    /// <summary>Stops the renderer (unless someone else took it over) and the polling.</summary>
    public async ValueTask DisposeAsync()
    {
        var stop = !_lost && _url is not null;
        await _closing.CancelAsync().ConfigureAwait(false);
        try
        {
            await _poller.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        if (stop)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _renderer.StopAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is UpnpException or OperationCanceledException)
            {
                _logger.LogDebug("Stop on close: {Message}", ex.Message);
            }
        }
        RevokeUrls();
        _renderer.Dispose();
        _closing.Dispose();
        _commands.Dispose();
    }
}
