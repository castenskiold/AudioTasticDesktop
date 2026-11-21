using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Casting.Chromecast;

/// <summary>CN-01: what the Chromecast reports about the media it plays.</summary>
/// <param name="PlayerState">PLAYING, PAUSED, BUFFERING or IDLE.</param>
/// <param name="IdleReason">For IDLE: FINISHED (the track ended), CANCELLED, INTERRUPTED or ERROR.</param>
public sealed record CastMediaStatus(string PlayerState, string? IdleReason, TimeSpan CurrentTime, TimeSpan? Duration, double? Volume, bool? Muted);

/// <summary>The track shown on the TV and in the Google Home app while casting.</summary>
public sealed record CastTrack(Uri Url, string ContentType, string Title, string? Artist, string? Album, Uri? Image, TimeSpan? Duration);

/// <summary>
/// CN-01: plays on a Chromecast through Google's Default Media Receiver (app CC1AD845), which pulls the audio from
/// the streaming server (CN-04). Launch → connect to the receiver app → LOAD the URL with title/artist/album/cover →
/// PLAY / PAUSE / SEEK / STOP; volume belongs to the device. Media status updates arrive as <see cref="StatusChanged"/>.
/// </summary>
public sealed class ChromecastSession : IAsyncDisposable
{
    public const string DefaultMediaReceiver = "CC1AD845";
    public const string ReceiverNamespace = "urn:x-cast:com.google.cast.receiver";
    public const string MediaNamespace = "urn:x-cast:com.google.cast.media";

    private readonly CastChannel _channel;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonObject>> _pending = new();
    private int _requestId;
    private string? _transportId;
    private string? _sessionId;
    private long? _mediaSessionId;

    private ChromecastSession(CastChannel channel, ILogger logger)
    {
        _channel = channel;
        _logger = logger;
        channel.MessageReceived += OnMessage;
        channel.Closed += (_, _) =>
        {
            foreach (var pending in _pending.Values)
            {
                pending.TrySetException(new IOException("The Cast device closed the connection."));
            }
            Closed?.Invoke(this, EventArgs.Empty);
        };
    }

    /// <summary>The latest media status (null before the first LOAD answered).</summary>
    public CastMediaStatus? Status { get; private set; }

    /// <summary>A new media status (position, state, end of track); raised on the connection's thread.</summary>
    public event EventHandler<CastMediaStatus>? StatusChanged;

    /// <summary>The device went away or the receiver app was closed (e.g. someone cast something else).</summary>
    public event EventHandler? Closed;

    /// <summary>Connects and starts the Default Media Receiver (or joins it when it already runs).</summary>
    public static async Task<ChromecastSession> StartAsync(IPAddress address, int port, ILogger logger, CancellationToken cancellationToken)
    {
        var channel = await CastChannel.ConnectAsync(address, port, logger, cancellationToken).ConfigureAwait(false);
        var session = new ChromecastSession(channel, logger);
        try
        {
            var status = await session.RequestAsync(ReceiverNamespace, CastChannel.ReceiverId,
                new JsonObject { ["type"] = "LAUNCH", ["appId"] = DefaultMediaReceiver }, cancellationToken).ConfigureAwait(false);
            var app = FindApp(status) ?? throw new IOException("The Chromecast did not start its media receiver.");
            session._transportId = app["transportId"]?.GetValue<string>();
            session._sessionId = app["sessionId"]?.GetValue<string>();
            await channel.SendAsync(CastChannel.ConnectionNamespace, session._transportId!, new JsonObject { ["type"] = "CONNECT" }, cancellationToken)
                .ConfigureAwait(false);
            return session;
        }
        catch
        {
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The LOAD request for a track (public for tests): Default Media Receiver, music metadata, autoplay.</summary>
    public static JsonObject LoadRequest(CastTrack track, TimeSpan start, bool autoplay = true, bool live = false)
    {
        var metadata = new JsonObject { ["metadataType"] = 3, ["title"] = track.Title }; // 3 = MusicTrackMediaMetadata
        if (track.Artist is not null)
        {
            metadata["artist"] = track.Artist;
        }
        if (track.Album is not null)
        {
            metadata["albumName"] = track.Album;
        }
        if (track.Image is not null)
        {
            metadata["images"] = new JsonArray(new JsonObject { ["url"] = track.Image.AbsoluteUri });
        }
        var media = new JsonObject
        {
            ["contentId"] = track.Url.AbsoluteUri, // escaped: ToString() would unescape spaces
            ["contentType"] = track.ContentType,
            ["streamType"] = live ? "LIVE" : "BUFFERED",
            ["metadata"] = metadata,
        };
        if (track.Duration is { } duration)
        {
            media["duration"] = duration.TotalSeconds;
        }
        return new JsonObject
        {
            ["type"] = "LOAD",
            ["media"] = media,
            ["autoplay"] = autoplay,
            ["currentTime"] = start.TotalSeconds,
        };
    }

    /// <summary>Loads and starts <paramref name="track"/> at <paramref name="start"/>; throws when the device refuses it.</summary>
    public async Task LoadAsync(CastTrack track, TimeSpan start, CancellationToken cancellationToken, bool autoplay = true, bool live = false)
    {
        var answer = await RequestAsync(MediaNamespace, Transport, LoadRequest(track, start, autoplay, live), cancellationToken).ConfigureAwait(false);
        var type = answer["type"]?.GetValue<string>();
        if (type is "LOAD_FAILED" or "LOAD_CANCELLED" or "INVALID_REQUEST")
        {
            throw new IOException($"The Chromecast could not play the track ({type}).");
        }
    }

    public Task PlayAsync(CancellationToken cancellationToken) => MediaCommandAsync("PLAY", null, cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken) => MediaCommandAsync("PAUSE", null, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => MediaCommandAsync("STOP", null, cancellationToken);

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken) =>
        MediaCommandAsync("SEEK", new JsonObject { ["currentTime"] = position.TotalSeconds, ["resumeState"] = "PLAYBACK_START" }, cancellationToken);

    /// <summary>The device volume, 0–1 (the device's own volume, not the player's).</summary>
    public Task SetVolumeAsync(double level, bool muted, CancellationToken cancellationToken) =>
        RequestAsync(ReceiverNamespace, CastChannel.ReceiverId, new JsonObject
        {
            ["type"] = "SET_VOLUME",
            ["volume"] = new JsonObject { ["level"] = Math.Clamp(level, 0, 1), ["muted"] = muted },
        }, cancellationToken);

    /// <summary>Stops the receiver app on the device (the TV returns to its idle screen).</summary>
    public async Task StopReceiverAsync(CancellationToken cancellationToken)
    {
        if (_sessionId is not null && _channel.IsOpen)
        {
            await RequestAsync(ReceiverNamespace, CastChannel.ReceiverId, new JsonObject { ["type"] = "STOP", ["sessionId"] = _sessionId }, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private string Transport => _transportId ?? throw new InvalidOperationException("The media receiver is not running.");

    private async Task MediaCommandAsync(string type, JsonObject? extra, CancellationToken cancellationToken)
    {
        if (_mediaSessionId is null)
        {
            return; // nothing loaded yet
        }
        var payload = new JsonObject { ["type"] = type, ["mediaSessionId"] = _mediaSessionId };
        foreach (var (key, value) in extra ?? [])
        {
            payload[key] = value?.DeepClone();
        }
        await RequestAsync(MediaNamespace, Transport, payload, cancellationToken).ConfigureAwait(false);
    }

    private async Task<JsonObject> RequestAsync(string ns, string destination, JsonObject payload, CancellationToken cancellationToken)
    {
        var id = Interlocked.Increment(ref _requestId);
        payload["requestId"] = id;
        var answer = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            await _channel.SendAsync(ns, destination, payload, cancellationToken).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            return await answer.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void OnMessage(object? sender, CastMessage message)
    {
        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(message.Payload) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return;
        }
        if (payload is null)
        {
            return;
        }
        var type = payload["type"]?.GetValue<string>();
        if (type == "MEDIA_STATUS" && ParseMediaStatus(payload) is { } status)
        {
            if (payload["status"] is JsonArray { Count: > 0 } list && list[0]?["mediaSessionId"] is { } sessionId)
            {
                _mediaSessionId = sessionId.GetValue<long>();
            }
            Status = status;
            StatusChanged?.Invoke(this, status);
        }
        if (type == "RECEIVER_STATUS" && _transportId is not null && FindApp(payload) is null)
        {
            _logger.LogInformation("The media receiver on the Chromecast was closed");
            Closed?.Invoke(this, EventArgs.Empty);
        }
        if (message.Namespace == CastChannel.ConnectionNamespace && message.SourceId == _transportId && type == "CLOSE")
        {
            Closed?.Invoke(this, EventArgs.Empty);
        }
        if (payload["requestId"] is { } requestId && requestId.GetValue<int>() is var id and > 0 && _pending.TryGetValue(id, out var waiting))
        {
            waiting.TrySetResult(payload);
        }
    }

    /// <summary>The first media status in a MEDIA_STATUS message (public for tests); null when it lists none.</summary>
    public static CastMediaStatus? ParseMediaStatus(JsonObject payload)
    {
        if (payload["status"] is not JsonArray { Count: > 0 } list || list[0] is not JsonObject status)
        {
            return null;
        }
        var time = status["currentTime"]?.GetValue<double>() ?? 0;
        double? duration = status["media"]?["duration"] is { } d ? d.GetValue<double>() : null;
        var volume = status["volume"];
        return new CastMediaStatus(
            status["playerState"]?.GetValue<string>() ?? "IDLE",
            status["idleReason"]?.GetValue<string>(),
            TimeSpan.FromSeconds(time),
            duration is { } seconds ? TimeSpan.FromSeconds(seconds) : null,
            volume?["level"]?.GetValue<double>(),
            volume?["muted"]?.GetValue<bool>());
    }

    private static JsonObject? FindApp(JsonObject status) =>
        (status["status"]?["applications"] as JsonArray)?.OfType<JsonObject>()
            .FirstOrDefault(a => string.Equals(a["appId"]?.GetValue<string>(), DefaultMediaReceiver, StringComparison.Ordinal));

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"Chromecast session {_sessionId}");

    public async ValueTask DisposeAsync() => await _channel.DisposeAsync().ConfigureAwait(false);
}
