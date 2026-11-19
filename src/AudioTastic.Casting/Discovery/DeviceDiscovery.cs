using Microsoft.Extensions.Logging;

namespace AudioTastic.Casting.Discovery;

/// <summary>
/// CN-05: finds cast devices of every family at once (Chromecast through mDNS, DLNA renderers through SSDP; AirPlay
/// follows with CN-03) and keeps the latest list. A scan takes a few seconds; the device picker scans while it is open.
/// </summary>
public sealed class DeviceDiscovery : IDisposable
{
    private readonly MdnsBrowser _mdns;
    private readonly SsdpBrowser _ssdp;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly ILogger<DeviceDiscovery> _logger;
    private readonly SemaphoreSlim _scanning = new(1, 1);

    public DeviceDiscovery(ILogger<DeviceDiscovery> logger)
    {
        _logger = logger;
        _mdns = new MdnsBrowser(logger);
        _ssdp = new SsdpBrowser(_http, logger);
    }

    /// <summary>The devices found by the latest scan, by name.</summary>
    public IReadOnlyList<CastDevice> Devices { get; private set; } = [];

    /// <summary>Raised after a scan, on a worker thread.</summary>
    public event EventHandler? DevicesChanged;

    /// <summary>Looks for devices for <paramref name="duration"/> (default 3 s) and returns them by name.</summary>
    public async Task<IReadOnlyList<CastDevice>> ScanAsync(TimeSpan? duration = null, CancellationToken cancellationToken = default)
    {
        var time = duration ?? TimeSpan.FromSeconds(3);
        await _scanning.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var chromecasts = _mdns.FindChromecastsAsync(time, cancellationToken);
            var renderers = _ssdp.FindRenderersAsync(time, cancellationToken);
            await Task.WhenAll(chromecasts, renderers).ConfigureAwait(false);
            var devices = (await chromecasts.ConfigureAwait(false)).Concat(await renderers.ConfigureAwait(false))
                .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase)
                .ToArray();
            Devices = devices;
            _logger.LogInformation("Cast devices found: {Devices}",
                devices.Length == 0 ? "none" : string.Join(", ", devices.Select(d => $"{d.Name} ({d.Kind}, {d.Address}{(d.HasScreen == true ? ", screen" : "")})")));
            DevicesChanged?.Invoke(this, EventArgs.Empty);
            return devices;
        }
        finally
        {
            _scanning.Release();
        }
    }

    public void Dispose()
    {
        _http.Dispose();
        _scanning.Dispose();
    }
}
