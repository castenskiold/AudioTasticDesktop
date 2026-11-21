using AudioTastic.Casting.Discovery;
using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Casting.Chromecast;

/// <summary>
/// CN-01: the engine's remote player for a Chromecast. Each track is published on the streaming server (CN-04) as
/// the file itself when the Chromecast plays its codec (MP3, AAC, FLAC, Opus, Vorbis, 16-bit WAV), otherwise
/// transcoded (FLAC, else WAV); internet radio URLs go to the device directly. Original files and WAV start and seek
/// through byte ranges; transcoded streams are restarted at the new time (?t=), which is faster than the device
/// reading them from the start. CUE tracks play their range of the file. The position is the device's, moved on
/// between its reports while playing.
/// </summary>
public sealed class ChromecastRemote : IRemotePlayer, IAsyncDisposable
{
    private static readonly HashSet<string> DirectCodecs = new(StringComparer.OrdinalIgnoreCase) { "mp3", "aac", "flac", "opus", "vorbis", "pcm_s16le" };
    private static readonly HashSet<string> DirectExtensions = new(StringComparer.OrdinalIgnoreCase) { ".mp3", ".m4a", ".mp4", ".aac", ".flac", ".ogg", ".oga", ".opus", ".wav" };

    private readonly ChromecastSession _session;
    private readonly StreamServer _server;
    private readonly Func<string, CastExtras?> _extras;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private Uri? _url;
    private Uri? _cover;
    private CastTrack? _track;
    private StreamFormat _format;
    private bool _live;
    private TimeSpan _fileStart;   // CUE: where the track starts in the file
    private TimeSpan? _fileEnd;    // CUE: where it ends
    private TimeSpan _offset;      // transcoded stream restarted at this file time
    private CastMediaStatus? _status;
    private long _statusTicks;
    private bool _playingSeen;
    private bool _ended;

    private ChromecastRemote(CastDevice device, ChromecastSession session, StreamServer server, Func<string, CastExtras?> extras, ILogger logger)
    {
        Name = device.Name;
        _session = session;
        _server = server;
        _extras = extras;
        _logger = logger;
        session.StatusChanged += OnStatus;
        session.Closed += (_, _) => Lost?.Invoke(this, EventArgs.Empty);
    }

    public string Name { get; }

    public event EventHandler? Lost;

    /// <summary>Connects to <paramref name="device"/> and starts its media receiver.</summary>
    public static async Task<ChromecastRemote> ConnectAsync(CastDevice device, StreamServer server, Func<string, CastExtras?> extras, ILogger logger,
        CancellationToken cancellationToken)
    {
        var session = await ChromecastSession.StartAsync(device.Address, device.Port, logger, cancellationToken).ConfigureAwait(false);
        return new ChromecastRemote(device, session, server, extras, logger);
    }

    /// <summary>How a track reaches the Chromecast: the file itself, or transcoded (public for tests).</summary>
    public static StreamFormat FormatFor(string path, string codec, IReadOnlyCollection<StreamFormat> available) =>
        DirectExtensions.Contains(Path.GetExtension(path)) && DirectCodecs.Contains(codec) ? StreamFormat.Original
        : available.Contains(StreamFormat.Flac) ? StreamFormat.Flac
        : StreamFormat.Wav;

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                if (_status is not { } status)
                {
                    return _offset - _fileStart;
                }
                var time = status.CurrentTime;
                if (status.PlayerState == "PLAYING")
                {
                    time += TimeSpan.FromMilliseconds(Environment.TickCount64 - _statusTicks);
                }
                var position = _offset + time - _fileStart;
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
        RevokeUrls();
        var extras = entry.IsUrl ? null : _extras(entry.Path);
        lock (_gate)
        {
            _status = null;
            _ended = false;
            _playingSeen = false;
            _live = entry.IsUrl;
            _fileStart = entry.Start ?? TimeSpan.Zero;
            _fileEnd = entry.End;
            _offset = TimeSpan.Zero;
        }
        if (extras?.Cover is { Length: > 0 } cover)
        {
            _cover = _server.PublishData(cover, extras.CoverType ?? "image/jpeg", "cover" + (extras.CoverType == "image/png" ? ".png" : ".jpg"));
        }
        if (entry.IsUrl)
        {
            _format = StreamFormat.Original;
            _url = new Uri(entry.Path);
            _track = new CastTrack(_url, "audio/mpeg", track.Title, track.Artist, null, null, null);
            await _session.LoadAsync(_track, TimeSpan.Zero, CancellationToken.None, autoplay: !paused, live: true).ConfigureAwait(false);
            return;
        }
        _format = FormatFor(entry.Path, track.Stream.Codec, _server.Formats);
        _url = _server.Publish(entry.Path, _format);
        var contentType = _format switch
        {
            StreamFormat.Flac => "audio/flac",
            StreamFormat.Mp3 => "audio/mpeg",
            StreamFormat.Wav => "audio/wav",
            _ => StreamServer.ContentTypeOf(entry.Path),
        };
        _track = new CastTrack(_url, contentType, track.Title, track.Artist, extras?.Album, _cover, track.Duration);
        _logger.LogInformation("Casting {Path} to {Device} as {Format}", entry.Path, Name, _format);
        await LoadAtAsync(_fileStart + start, !paused).ConfigureAwait(false);
    }

    /// <summary>Loads the current track at <paramref name="fileTime"/>: originals and WAV seek there, transcoded streams start there.</summary>
    private async Task LoadAtAsync(TimeSpan fileTime, bool autoplay)
    {
        var track = _track!;
        if (_format is StreamFormat.Flac or StreamFormat.Mp3)
        {
            lock (_gate)
            {
                _offset = fileTime;
                _status = null;
            }
            var url = fileTime > TimeSpan.Zero ? StreamServer.StartingAt(_url!, fileTime) : _url!;
            await _session.LoadAsync(track with { Url = url, Duration = null }, TimeSpan.Zero, CancellationToken.None, autoplay).ConfigureAwait(false);
        }
        else
        {
            await _session.LoadAsync(track, fileTime, CancellationToken.None, autoplay).ConfigureAwait(false);
        }
    }

    public Task PauseAsync() => _session.PauseAsync(CancellationToken.None);

    public Task ResumeAsync() => _session.PlayAsync(CancellationToken.None);

    public async Task StopAsync()
    {
        await _session.StopAsync(CancellationToken.None).ConfigureAwait(false);
        RevokeUrls();
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_live || _track is null)
        {
            return;
        }
        var fileTime = _fileStart + position;
        if (_format is StreamFormat.Flac or StreamFormat.Mp3)
        {
            var playing = _status?.PlayerState is "PLAYING" or "BUFFERING" or null;
            await LoadAtAsync(fileTime, playing).ConfigureAwait(false); // restart the transcoded stream there
        }
        else
        {
            await _session.SeekAsync(fileTime, CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>The player's volume (0–100, cubic like the local volume) as the device volume (0–1).</summary>
    public Task SetVolumeAsync(int volume, bool muted) => _session.SetVolumeAsync(volume / 100.0, muted, CancellationToken.None);

    private void OnStatus(object? sender, CastMediaStatus status)
    {
        lock (_gate)
        {
            _status = status;
            _statusTicks = Environment.TickCount64;
            if (status.PlayerState is "PLAYING" or "PAUSED")
            {
                _playingSeen = true;
            }
            if (status.PlayerState == "IDLE" && status.IdleReason == "FINISHED" && _playingSeen)
            {
                _ended = true;
            }
            if (status.PlayerState == "IDLE" && status.IdleReason == "ERROR")
            {
                _logger.LogWarning("The Chromecast could not play {Url}", _url);
            }
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
    }

    /// <summary>Closes the receiver app on the device and the connection.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            await _session.StopReceiverAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidOperationException)
        {
        }
        RevokeUrls();
        await _session.DisposeAsync().ConfigureAwait(false);
    }
}
