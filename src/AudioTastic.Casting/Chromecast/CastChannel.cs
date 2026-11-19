using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Casting.Chromecast;

/// <summary>
/// CN-01: the TLS connection to a Cast device (port 8009). Sends and receives framed <see cref="CastMessage"/>s,
/// answers the device's heartbeat PINGs and sends its own every 5 seconds; when nothing arrives for 20 seconds the
/// connection counts as lost. Cast devices use self-signed certificates per device, so the certificate is not
/// checked (the device was found on the local network, CN-05).
/// </summary>
public sealed class CastChannel : IAsyncDisposable
{
    public const string ConnectionNamespace = "urn:x-cast:com.google.cast.tp.connection";
    public const string HeartbeatNamespace = "urn:x-cast:com.google.cast.tp.heartbeat";
    public const string SenderId = "sender-0";
    public const string ReceiverId = "receiver-0";

    private readonly TcpClient _tcp;
    private readonly SslStream _ssl;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private long _lastReceived = Environment.TickCount64;
    private Task? _readLoop;
    private Task? _heartbeat;
    private int _closed;

    private CastChannel(TcpClient tcp, SslStream ssl, ILogger logger)
    {
        _tcp = tcp;
        _ssl = ssl;
        _logger = logger;
    }

    /// <summary>Every message from the device except heartbeats; raised on the read loop's thread.</summary>
    public event EventHandler<CastMessage>? MessageReceived;

    /// <summary>The connection ended (device gone, network lost, or closed).</summary>
    public event EventHandler? Closed;

    public bool IsOpen => Volatile.Read(ref _closed) == 0;

    public static async Task<CastChannel> ConnectAsync(IPAddress address, int port, ILogger logger, CancellationToken cancellationToken)
    {
        var tcp = new TcpClient();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            await tcp.ConnectAsync(address, port, timeout.Token).ConfigureAwait(false);
#pragma warning disable CA5359 // Cast devices present self-signed certificates; the device was found on the LAN.
            var ssl = new SslStream(tcp.GetStream(), false, (_, _, _, _) => true);
#pragma warning restore CA5359
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = address.ToString() }, timeout.Token).ConfigureAwait(false);
            var channel = new CastChannel(tcp, ssl, logger);
            channel._readLoop = Task.Run(channel.ReadLoopAsync, CancellationToken.None);
            await channel.SendAsync(ConnectionNamespace, ReceiverId, new JsonObject { ["type"] = "CONNECT" }, cancellationToken).ConfigureAwait(false);
            channel._heartbeat = Task.Run(channel.HeartbeatAsync, CancellationToken.None);
            return channel;
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    public Task SendAsync(string ns, string destination, JsonObject payload, CancellationToken cancellationToken) =>
        SendAsync(new CastMessage(SenderId, destination, ns, payload.ToJsonString()), cancellationToken);

    public async Task SendAsync(CastMessage message, CancellationToken cancellationToken)
    {
        if (!IsOpen)
        {
            throw new IOException("The connection to the Cast device is closed.");
        }
        var frame = message.Frame();
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _ssl.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await _ssl.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            Close();
            throw new IOException("The connection to the Cast device was lost.", ex);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var header = new byte[4];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await _ssl.ReadExactlyAsync(header, _stop.Token).ConfigureAwait(false);
                var length = BinaryPrimitives.ReadInt32BigEndian(header);
                if (length is < 0 or > 1024 * 1024)
                {
                    throw new InvalidDataException($"Cast message of {length} bytes.");
                }
                var body = new byte[length];
                await _ssl.ReadExactlyAsync(body, _stop.Token).ConfigureAwait(false);
                Volatile.Write(ref _lastReceived, Environment.TickCount64);
                var message = CastMessage.Decode(body);
                if (message.Namespace == HeartbeatNamespace)
                {
                    if (message.Payload.Contains("\"PING\"", StringComparison.Ordinal))
                    {
                        await SendAsync(HeartbeatNamespace, message.SourceId, new JsonObject { ["type"] = "PONG" }, _stop.Token).ConfigureAwait(false);
                    }
                    continue;
                }
                if (message.Namespace == ConnectionNamespace && message.Payload.Contains("\"CLOSE\"", StringComparison.Ordinal))
                {
                    _logger.LogDebug("Cast device closed the virtual connection {Source}", message.SourceId);
                }
                MessageReceived?.Invoke(this, message);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidDataException or EndOfStreamException or SocketException)
        {
            if (!_stop.IsCancellationRequested)
            {
                _logger.LogInformation("Cast connection ended: {Message}", ex.Message);
            }
        }
        Close();
    }

    private async Task HeartbeatAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), _stop.Token).ConfigureAwait(false);
                if (Environment.TickCount64 - Volatile.Read(ref _lastReceived) > 20_000)
                {
                    _logger.LogInformation("Cast device stopped answering");
                    Close();
                    return;
                }
                await SendAsync(HeartbeatNamespace, ReceiverId, new JsonObject { ["type"] = "PING" }, _stop.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
        }
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }
        _stop.Cancel();
        _ssl.Dispose();
        _tcp.Dispose();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync()
    {
        if (IsOpen)
        {
            try
            {
                await SendAsync(ConnectionNamespace, ReceiverId, new JsonObject { ["type"] = "CLOSE" }, CancellationToken.None).ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }
        Close();
        foreach (var task in new[] { _readLoop, _heartbeat })
        {
            if (task is not null)
            {
                await task.ConfigureAwait(false);
            }
        }
        _writeLock.Dispose();
        _stop.Dispose();
    }
}
