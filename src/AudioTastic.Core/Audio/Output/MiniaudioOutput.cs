using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Interop;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Core.Audio.Output;

/// <summary>
/// PB-04 part 1: audio output through miniaudio in shared mode (WASAPI on Windows, Core Audio on macOS,
/// PulseAudio/PipeWire/ALSA on Linux). 32-bit float, stereo in P1.
/// <list type="bullet">
/// <item>The audio callback is an unmanaged function pointer into <see cref="OnData"/>: no delegates, no allocations.</item>
/// <item>Device list changes are detected by polling every 2 s (miniaudio has no device-change events).</item>
/// <item>The system default device follows default-device changes (miniaudio reroutes); an explicitly chosen device
/// that disappears raises <see cref="OutputLost"/> so the engine can fall back to the default.</item>
/// </list>
/// </summary>
public sealed unsafe class MiniaudioOutput : IAudioOutput
{
    private static readonly TimeSpan DevicePollInterval = TimeSpan.FromSeconds(2);

    private readonly ILogger _logger;
    private readonly Lock _lock = new(); // guards context/device calls from control and poll threads
    private readonly Timer _devicePoll;
    private nint _context;
    private nint _device;
    private GCHandle _self;
    private IAudioRenderer? _renderer;
    private int _channels;
    private volatile bool _stopRequested = true;
    private bool _explicitDevice;
    private string _deviceSignature = string.Empty;
    private bool _disposed;

    static MiniaudioOutput() => NativeLibraries.RegisterResolver(typeof(MiniaudioOutput).Assembly);

    /// <param name="useNullBackend">True for miniaudio's null backend (no sound card needed; tests and CI).</param>
    public MiniaudioOutput(ILogger<MiniaudioOutput>? logger = null, bool useNullBackend = false)
    {
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        int version;
        try
        {
            version = MiniaudioNative.ApiVersion();
        }
        catch (DllNotFoundException ex)
        {
            throw new InvalidOperationException(
                "The miniaudio native library was not found. Build it with the CMake commands in native/README.md. " +
                $"Searched: {string.Join(", ", NativeLibraries.SearchDirectories)}", ex);
        }
        if (version != MiniaudioNative.ApiVersionExpected)
        {
            throw new InvalidOperationException(
                $"miniaudio shim API version {version}, expected {MiniaudioNative.ApiVersionExpected}. Rebuild native/ (CMake).");
        }
        Check(MiniaudioNative.ContextCreate(useNullBackend ? 1 : 0, out _context), "initialise audio");
        BackendName = Marshal.PtrToStringUTF8(MiniaudioNative.ContextBackendName(_context)) ?? "unknown";
        _self = GCHandle.Alloc(this);
        _deviceSignature = Signature(GetDevices());
        _devicePoll = new Timer(_ => PollDevices(), null, DevicePollInterval, DevicePollInterval);
        _logger.LogInformation("Audio output backend {Backend}", BackendName);
    }

    public string BackendName { get; }
    public bool IsOpen => _device != 0;
    public bool IsRunning => IsOpen && !_stopRequested;
    public string? DeviceName { get; private set; }
    public AudioFormat? Format { get; private set; }

    /// <summary>Sample rate the hardware runs at (differs from <see cref="Format"/> when the OS resamples).</summary>
    public int DeviceSampleRate => IsOpen ? (int)MiniaudioNative.DeviceInternalSampleRate(_device) : 0;

    /// <summary>Frames per device period (callback size).</summary>
    public int PeriodFrames => IsOpen ? (int)MiniaudioNative.DevicePeriodFrames(_device) : 0;

    public event EventHandler? DevicesChanged;
    public event EventHandler<AudioOutputLostEventArgs>? OutputLost;

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            Check(MiniaudioNative.EnumeratePlayback(_context, null, 0, out var total), "list devices");
            var infos = new MiniaudioNative.DeviceInfo[total + 4]; // room for devices added in between
            fixed (MiniaudioNative.DeviceInfo* p = infos)
            {
                Check(MiniaudioNative.EnumeratePlayback(_context, p, (uint)infos.Length, out total), "list devices");
                var count = (int)Math.Min(total, (uint)infos.Length);
                var result = new List<AudioDeviceInfo>(count);
                for (var i = 0; i < count; i++)
                {
                    var name = Marshal.PtrToStringUTF8((nint)p[i].Name) ?? $"Device {i + 1}";
                    var id = new ReadOnlySpan<byte>(p[i].Id, MiniaudioNative.IdBytes).ToArray();
                    result.Add(new AudioDeviceInfo(name, p[i].IsDefault != 0, id));
                }
                return result;
            }
        }
    }

    public void Open(AudioDeviceInfo? device, AudioFormat format, int bufferMilliseconds, IAudioRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(renderer);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            CloseCore();
            _renderer = renderer;
            _channels = format.Channels;
            nint handle;
            var id = device?.Id.ToArray();
            fixed (byte* idPtr = id)
            {
                Check(MiniaudioNative.DeviceOpen(_context, idPtr, (uint)format.SampleRate, (uint)format.Channels,
                    (uint)Math.Clamp(bufferMilliseconds, 4, 1000), &OnData, &OnNotify, GCHandle.ToIntPtr(_self), out handle),
                    $"open '{device?.Name ?? "default device"}'");
            }
            _device = handle;
            _explicitDevice = device is not null;
            Format = format;
            var name = stackalloc byte[MiniaudioNative.NameBytes];
            DeviceName = MiniaudioNative.DeviceName(_device, name, MiniaudioNative.NameBytes) == 0
                ? Marshal.PtrToStringUTF8((nint)name)
                : device?.Name;
            _logger.LogInformation(
                "Opened {Device} ({Backend}) at {Rate} Hz, {Channels} ch, device rate {DeviceRate} Hz, period {Period} frames",
                DeviceName, BackendName, format.SampleRate, format.Channels, DeviceSampleRate, PeriodFrames);
        }
    }

    public void StartOutput()
    {
        lock (_lock)
        {
            if (_device == 0)
            {
                throw new InvalidOperationException("Open the output first.");
            }
            _stopRequested = false;
            Check(MiniaudioNative.DeviceStart(_device), "start playback");
        }
    }

    public void StopOutput()
    {
        lock (_lock)
        {
            _stopRequested = true;
            if (_device != 0)
            {
                LogIfFailed(MiniaudioNative.DeviceStop(_device), "stop playback");
            }
        }
    }

    public void Close()
    {
        lock (_lock)
        {
            CloseCore();
        }
    }

    public void Dispose()
    {
        _devicePoll.Dispose();
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            CloseCore();
            MiniaudioNative.ContextDestroy(_context);
            _context = 0;
        }
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private void CloseCore()
    {
        if (_device == 0)
        {
            return;
        }
        _stopRequested = true;
        LogIfFailed(MiniaudioNative.DeviceStop(_device), "stop playback");
        MiniaudioNative.DeviceClose(_device);
        _device = 0;
        Format = null;
        DeviceName = null;
    }

    /// <summary>Audio thread entry point (called by the shim's miniaudio data callback).</summary>
    [UnmanagedCallersOnly]
    private static void OnData(nint user, float* output, uint frameCount)
    {
        var self = Unsafe.As<MiniaudioOutput>(GCHandle.FromIntPtr(user).Target!);
        var buffer = new Span<float>(output, (int)frameCount * self._channels);
        try
        {
            if (self._renderer is { } renderer)
            {
                renderer.Render(buffer);
            }
            else
            {
                buffer.Clear();
            }
        }
#pragma warning disable CA1031 // An exception must never cross into native code: output silence instead.
        catch (Exception)
#pragma warning restore CA1031
        {
            buffer.Clear();
        }
    }

    [UnmanagedCallersOnly]
    private static void OnNotify(nint user, int notification)
    {
        var self = Unsafe.As<MiniaudioOutput>(GCHandle.FromIntPtr(user).Target!);
        switch (notification)
        {
            case MiniaudioNative.NotifyStopped when !self._stopRequested:
                self._stopRequested = true;
                ThreadPool.QueueUserWorkItem(static s => s.RaiseLost("the device stopped (unplugged or disabled)"), self, false);
                break;
            case MiniaudioNative.NotifyRerouted:
                ThreadPool.QueueUserWorkItem(static s => s._logger.LogInformation("Output rerouted to the new default device"), self, false);
                break;
        }
    }

    private void RaiseLost(string reason)
    {
        _logger.LogWarning("Audio output lost: {Reason}", reason);
        OutputLost?.Invoke(this, new AudioOutputLostEventArgs(reason));
    }

    private void PollDevices()
    {
        try
        {
            if (_disposed)
            {
                return;
            }
            var devices = GetDevices();
            var signature = Signature(devices);
            if (signature == _deviceSignature)
            {
                return;
            }
            _deviceSignature = signature;
            _logger.LogInformation("Audio devices changed: {Devices}", string.Join(", ", devices));
            DevicesChanged?.Invoke(this, EventArgs.Empty);

            var openName = DeviceName;
            if (IsRunning && _explicitDevice && openName is not null && devices.All(d => d.Name != openName))
            {
                _stopRequested = true;
                RaiseLost($"'{openName}' was removed");
            }
        }
#pragma warning disable CA1031 // Polling runs on a timer thread; log and try again next time.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogDebug(ex, "Device poll failed");
        }
    }

    private static string Signature(IReadOnlyList<AudioDeviceInfo> devices) =>
        string.Join('\n', devices.Select(d => (d.IsDefault ? "*" : string.Empty) + d.Name));

    private void LogIfFailed(int result, string action)
    {
        if (result != 0)
        {
            _logger.LogWarning("Could not {Action} (miniaudio error {Result})", action, result);
        }
    }

    private static void Check(int result, string action)
    {
        if (result != 0)
        {
            throw new InvalidOperationException($"Could not {action} (miniaudio error {result}).");
        }
    }
}
