using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using AudioTastic.Casting.Discovery;

namespace AudioTastic.Casting.Dlna;

/// <summary>A UPnP action failed: the renderer answered with a SOAP fault (UPnP error code) or not at all.</summary>
public sealed class UpnpException : IOException
{
    public UpnpException()
    {
    }

    public UpnpException(string message) : base(message)
    {
    }

    public UpnpException(string message, Exception inner) : base(message, inner)
    {
    }

    public UpnpException(string action, int code, string? description)
        : base($"The device refused {action} (UPnP error {code}{(description is null ? "" : ", " + description)}).")
    {
        Code = code;
    }

    /// <summary>The UPnP error code (e.g. 701 transition not available, 714 illegal MIME type), 0 when none.</summary>
    public int Code { get; }
}

/// <summary>What the renderer's AVTransport reports.</summary>
/// <param name="State">PLAYING, PAUSED_PLAYBACK, STOPPED, TRANSITIONING or NO_MEDIA_PRESENT.</param>
public sealed record UpnpTransportInfo(string State, string? Status);

/// <summary>The renderer's position: track time, track length (when it knows it) and the URI it plays.</summary>
public sealed record UpnpPositionInfo(TimeSpan RelTime, TimeSpan? Duration, string? TrackUri);

/// <summary>
/// CN-02: SOAP calls to a DLNA/UPnP media renderer's AVTransport, RenderingControl and ConnectionManager services
/// (own code, no library). Instance 0, channel Master; service types and control URLs come from discovery (CN-05).
/// </summary>
public sealed class UpnpRenderer : IDisposable
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Control = "urn:schemas-upnp-org:control-1-0";

    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly (Uri Url, string Type) _avTransport;
    private readonly (Uri Url, string Type)? _rendering;
    private readonly (Uri Url, string Type)? _connection;

    public UpnpRenderer(CastDevice device, HttpClient? http = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        _avTransport = Service(device, "avTransport", "urn:schemas-upnp-org:service:AVTransport:1")
            ?? throw new ArgumentException("The device has no AVTransport service.", nameof(device));
        _rendering = Service(device, "renderingControl", "urn:schemas-upnp-org:service:RenderingControl:1");
        _connection = Service(device, "connectionManager", "urn:schemas-upnp-org:service:ConnectionManager:1");
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
    }

    private static (Uri, string)? Service(CastDevice device, string key, string fallbackType) =>
        device.Details.TryGetValue(key, out var url) && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? (uri, device.Details.TryGetValue(key + "Type", out var type) && type.Length > 0 ? type : fallbackType)
            : null;

    public bool HasVolume => _rendering is not null;

    public Task SetUriAsync(Uri url, string metadata, CancellationToken cancellationToken) =>
        CallAsync(_avTransport, "SetAVTransportURI", [("InstanceID", "0"), ("CurrentURI", url.AbsoluteUri), ("CurrentURIMetaData", metadata)], cancellationToken);

    public Task PlayAsync(CancellationToken cancellationToken) =>
        CallAsync(_avTransport, "Play", [("InstanceID", "0"), ("Speed", "1")], cancellationToken);

    public Task PauseAsync(CancellationToken cancellationToken) => CallAsync(_avTransport, "Pause", [("InstanceID", "0")], cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => CallAsync(_avTransport, "Stop", [("InstanceID", "0")], cancellationToken);

    public Task SeekAsync(TimeSpan position, CancellationToken cancellationToken) =>
        CallAsync(_avTransport, "Seek", [("InstanceID", "0"), ("Unit", "REL_TIME"), ("Target", FormatTime(position))], cancellationToken);

    public async Task<UpnpTransportInfo> GetTransportInfoAsync(CancellationToken cancellationToken)
    {
        var answer = await CallAsync(_avTransport, "GetTransportInfo", [("InstanceID", "0")], cancellationToken).ConfigureAwait(false);
        return new UpnpTransportInfo(Value(answer, "CurrentTransportState") ?? "STOPPED", Value(answer, "CurrentTransportStatus"));
    }

    public async Task<UpnpPositionInfo> GetPositionInfoAsync(CancellationToken cancellationToken)
    {
        var answer = await CallAsync(_avTransport, "GetPositionInfo", [("InstanceID", "0")], cancellationToken).ConfigureAwait(false);
        return new UpnpPositionInfo(ParseTime(Value(answer, "RelTime")) ?? TimeSpan.Zero, ParseTime(Value(answer, "TrackDuration")), Value(answer, "TrackURI"));
    }

    /// <summary>The device volume 0–100, or null when it has no RenderingControl.</summary>
    public async Task<int?> GetVolumeAsync(CancellationToken cancellationToken)
    {
        if (_rendering is not { } rendering)
        {
            return null;
        }
        var answer = await CallAsync(rendering, "GetVolume", [("InstanceID", "0"), ("Channel", "Master")], cancellationToken).ConfigureAwait(false);
        return int.TryParse(Value(answer, "CurrentVolume"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var volume) ? volume : null;
    }

    public async Task SetVolumeAsync(int volume, CancellationToken cancellationToken)
    {
        if (_rendering is { } rendering)
        {
            await CallAsync(rendering, "SetVolume", [("InstanceID", "0"), ("Channel", "Master"),
                ("DesiredVolume", Math.Clamp(volume, 0, 100).ToString(CultureInfo.InvariantCulture))], cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task SetMuteAsync(bool muted, CancellationToken cancellationToken)
    {
        if (_rendering is { } rendering)
        {
            await CallAsync(rendering, "SetMute", [("InstanceID", "0"), ("Channel", "Master"), ("DesiredMute", muted ? "1" : "0")], cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>The MIME types the renderer accepts over HTTP (ConnectionManager GetProtocolInfo Sink); empty when unknown.</summary>
    public async Task<IReadOnlySet<string>> GetSinkTypesAsync(CancellationToken cancellationToken)
    {
        if (_connection is not { } connection)
        {
            return new HashSet<string>();
        }
        var answer = await CallAsync(connection, "GetProtocolInfo", [], cancellationToken).ConfigureAwait(false);
        return ParseSinkTypes(Value(answer, "Sink"));
    }

    /// <summary>The MIME types (lower case) of the http-get entries in a protocolInfo list (public for tests).</summary>
    public static IReadOnlySet<string> ParseSinkTypes(string? sink)
    {
        var types = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in (sink ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = entry.Split(':');
            if (parts.Length >= 3 && parts[0].Equals("http-get", StringComparison.OrdinalIgnoreCase))
            {
                types.Add(parts[2].Split(';')[0].Trim().ToLowerInvariant());
            }
        }
        return types;
    }

    /// <summary>The SOAP envelope for <paramref name="action"/> (public for tests).</summary>
    public static string Envelope(string serviceType, string action, IEnumerable<(string Name, string Value)> arguments)
    {
        XNamespace u = serviceType;
        var body = new XElement(u + action, new XAttribute(XNamespace.Xmlns + "u", serviceType),
            arguments.Select(a => new XElement(a.Name, a.Value)));
        var envelope = new XElement(Soap + "Envelope",
            new XAttribute(XNamespace.Xmlns + "s", Soap),
            new XAttribute(Soap + "encodingStyle", "http://schemas.xmlsoap.org/soap/encoding/"),
            new XElement(Soap + "Body", body));
        return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" + envelope.ToString(SaveOptions.DisableFormatting);
    }

    private async Task<XElement> CallAsync((Uri Url, string Type) service, string action, IEnumerable<(string, string)> arguments,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, service.Url)
        {
            Content = new StringContent(Envelope(service.Type, action, arguments), Encoding.UTF8),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse("text/xml; charset=\"utf-8\"");
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service.Type}#{action}\"");
        string text;
        System.Net.HttpStatusCode status;
        try
        {
            using var response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = response.StatusCode;
            text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new UpnpException($"The device did not answer {action}: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new UpnpException($"The device did not answer {action} in time.", ex);
        }
        XElement? body;
        try
        {
            body = XDocument.Parse(text).Root?.Element(Soap + "Body");
        }
        catch (System.Xml.XmlException ex)
        {
            throw new UpnpException($"The device sent an unreadable answer to {action} (HTTP {(int)status}).", ex);
        }
        if (body?.Element(Soap + "Fault") is { } fault)
        {
            var error = fault.Descendants(Control + "UPnPError").FirstOrDefault();
            var code = int.TryParse(error?.Element(Control + "errorCode")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 0;
            throw new UpnpException(action, code, error?.Element(Control + "errorDescription")?.Value);
        }
        if ((int)status >= 300)
        {
            throw new UpnpException($"The device refused {action} (HTTP {(int)status}).");
        }
        return body?.Elements().FirstOrDefault() ?? new XElement("empty");
    }

    private static string? Value(XElement answer, string name) =>
        answer.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value;

    /// <summary>H:MM:SS as UPnP REL_TIME targets use it.</summary>
    public static string FormatTime(TimeSpan time) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}");

    /// <summary>Parses H+:MM:SS[.fff] (also with fractions as F0/F1); null for NOT_IMPLEMENTED or empty.</summary>
    public static TimeSpan? ParseTime(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var parts = text.Trim().Split(':');
        if (parts.Length != 3
            || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes))
        {
            return null;
        }
        var seconds = parts[2];
        var slash = seconds.IndexOf('/', StringComparison.Ordinal);
        double value;
        if (slash > 0)
        {
            // S+.F0/F1 form, rare
            var dot = seconds.IndexOf('.', StringComparison.Ordinal);
            if (!int.TryParse(seconds[..(dot > 0 ? dot : slash)], NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
            {
                return null;
            }
            value = whole;
        }
        else if (!double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
        {
            return null;
        }
        return TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(value);
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
