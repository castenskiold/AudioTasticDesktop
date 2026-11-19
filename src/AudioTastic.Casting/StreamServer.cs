using System.Collections.Concurrent;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using AudioTastic.Sdk.Audio;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Casting;

/// <summary>CN-04: how a file is served.</summary>
public enum StreamFormat
{
    /// <summary>The file itself, for devices that play its format.</summary>
    Original,

    /// <summary>Decoded to 16-bit PCM WAV (stereo or mono): plays everywhere, no encoder, seekable.</summary>
    Wav,
}

/// <summary>A published file: what the device gets at its URL.</summary>
public sealed record StreamItem(string Path, StreamFormat Format, string ContentType);

/// <summary>
/// CN-04: the built-in HTTP server cast devices pull audio from. Each published file gets a random, unguessable URL
/// (128-bit token) that stops working when it is revoked; only devices on the local network (private, link-local and
/// loopback addresses) get an answer. Originals and WAV both support range requests, so devices can seek.
/// Kestrel (ASP.NET Core, MIT), embedded.
/// </summary>
public sealed class StreamServer : IAsyncDisposable
{
    private readonly IEnumerable<IAudioSourceFactory> _factories;
    private readonly ILogger<StreamServer> _logger;
    private readonly ConcurrentDictionary<string, StreamItem> _items = new(StringComparer.Ordinal);
    private WebApplication? _app;

    public StreamServer(IEnumerable<IAudioSourceFactory> factories, ILogger<StreamServer> logger)
    {
        _factories = factories;
        _logger = logger;
    }

    /// <summary>The port the server listens on (after <see cref="StartAsync"/>).</summary>
    public int Port { get; private set; }

    /// <summary>The address devices use, e.g. http://192.168.1.20:51234 (after <see cref="StartAsync"/>).</summary>
    public Uri? BaseAddress { get; private set; }

    public bool IsRunning => _app is not null;

    /// <summary>
    /// Starts listening on all interfaces at <paramref name="port"/> (0 = a free port chosen by the system).
    /// <paramref name="advertised"/> is the address put into URLs (default: this computer's address on the local network).
    /// </summary>
    public async Task StartAsync(int port = 0, IPAddress? advertised = null, CancellationToken cancellationToken = default)
    {
        if (_app is not null)
        {
            return;
        }
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseKestrelCore().ConfigureKestrel(k => k.ListenAnyIP(port));
        builder.Services.AddRoutingCore();
        var app = builder.Build();
        app.UseRouting();
        app.Use(async (context, next) =>
        {
            if (!IsLocalNetwork(context.Connection.RemoteIpAddress))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden; // only the local network
                return;
            }
            await next(context).ConfigureAwait(false);
        });
        app.MapMethods("/media/{token}/{name?}", ["GET", "HEAD"], (string token) => Serve(token));
        await app.StartAsync(cancellationToken).ConfigureAwait(false);
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Port = new Uri(address.Replace("[::]", "localhost", StringComparison.Ordinal).Replace("0.0.0.0", "localhost", StringComparison.Ordinal)).Port;
        BaseAddress = new Uri($"http://{FormatHost(advertised ?? LocalNetworkAddress() ?? IPAddress.Loopback)}:{Port}");
        _app = app;
        _logger.LogInformation("Streaming server listening on port {Port} ({Address})", Port, BaseAddress);
    }

    /// <summary>Publishes <paramref name="path"/> and returns its URL. The URL ends in a readable file name for devices that show it.</summary>
    public Uri Publish(string path, StreamFormat format)
    {
        if (BaseAddress is null)
        {
            throw new InvalidOperationException("The streaming server is not running.");
        }
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var contentType = format == StreamFormat.Wav ? "audio/wav" : ContentTypeOf(path);
        _items[token] = new StreamItem(path, format, contentType);
        var name = format == StreamFormat.Wav ? Path.GetFileNameWithoutExtension(path) + ".wav" : Path.GetFileName(path);
        return new Uri(BaseAddress, $"media/{token}/{Uri.EscapeDataString(name)}");
    }

    /// <summary>Stops answering <paramref name="url"/> (the token in it).</summary>
    public void Revoke(Uri url)
    {
        var parts = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            _items.TryRemove(parts[1], out _);
        }
    }

    /// <summary>Stops answering every published URL.</summary>
    public void RevokeAll() => _items.Clear();

    private IResult Serve(string token)
    {
        if (!_items.TryGetValue(token, out var item))
        {
            return Results.NotFound();
        }
        if (item.Format == StreamFormat.Original)
        {
            return File.Exists(item.Path) ? Results.File(item.Path, item.ContentType, enableRangeProcessing: true) : Results.NotFound();
        }
        var source = OpenSource(item.Path);
        if (source is null)
        {
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType);
        }
        if (source.LengthFrames is null || !source.CanSeek)
        {
            source.Dispose();
            return Results.StatusCode(StatusCodes.Status415UnsupportedMediaType); // live streams are passed on another way (later)
        }
        return Results.Stream(new WavStream(source), item.ContentType, enableRangeProcessing: true);
    }

    /// <summary>Opens <paramref name="path"/> with the first decoder plugin that can (as the player does).</summary>
    private IAudioSource? OpenSource(string path)
    {
        foreach (var factory in _factories.Where(f => f.CanOpen(path)))
        {
            try
            {
                return factory.Open(path);
            }
            catch (AudioSourceException ex)
            {
                _logger.LogDebug(ex, "{Factory} could not open {Path} for streaming", factory.Name, path);
            }
        }
        return null;
    }

    /// <summary>The MIME type cast devices expect for an original file.</summary>
    public static string ContentTypeOf(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".m4a" or ".mp4" or ".m4b" or ".alac" => "audio/mp4",
        ".aac" => "audio/aac",
        ".ogg" or ".oga" => "audio/ogg",
        ".opus" => "audio/ogg; codecs=opus",
        ".aiff" or ".aif" or ".aifc" => "audio/aiff",
        ".wma" => "audio/x-ms-wma",
        _ => "application/octet-stream",
    };

    /// <summary>True for loopback, private (10/8, 172.16/12, 192.168/16), link-local and IPv6 unique-local addresses.</summary>
    public static bool IsLocalNetwork(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return b[0] == 10 || (b[0] == 172 && b[1] is >= 16 and <= 31) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
        return address.IsIPv6LinkLocal || address.IsIPv6UniqueLocal || address.IsIPv6SiteLocal;
    }

    /// <summary>This computer's IPv4 address on the local network (the interface with a default gateway), or null.</summary>
    public static IPAddress? LocalNetworkAddress()
    {
        var candidates = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Select(n => n.GetIPProperties())
            .Select(p => (Gateway: p.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetwork),
                Address: p.UnicastAddresses.Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && IsLocalNetwork(a))))
            .Where(c => c.Address is not null)
            .OrderByDescending(c => c.Gateway)
            .ToList();
        return candidates.FirstOrDefault().Address;
    }

    private static string FormatHost(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync().ConfigureAwait(false);
            await _app.DisposeAsync().ConfigureAwait(false);
            _app = null;
        }
    }
}
