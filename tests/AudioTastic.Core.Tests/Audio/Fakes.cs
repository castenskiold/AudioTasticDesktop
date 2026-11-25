using AudioTastic.Core.Audio;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.Core.Tests.Audio;

/// <summary>Deterministic test signal: sample (frame f, channel c) = <see cref="ValueAt"/>(f, c). Seekable.</summary>
internal sealed class SyntheticSource(int sampleRate, int channels, long lengthFrames, string title = "Synthetic") : IAudioSource
{
    private long _position;

    public static float ValueAt(long frame, int channel) => (((frame * 7) + (channel * 3)) % 2001 - 1000) / 1000f;

    public AudioStreamInfo Info { get; } = new("synthetic", sampleRate, channels, 32, 0, true);
    public AudioFormat Format { get; } = new(sampleRate, channels);
    public long? LengthFrames => lengthFrames;
    public long PositionFrames => _position;
    public bool CanSeek => true;
    public IReadOnlyDictionary<string, string> Tags { get; } = new Dictionary<string, string> { ["title"] = title };
    public bool Disposed { get; private set; }
    public long FailAfterFrames { get; init; } = long.MaxValue;

    public int Read(Span<float> buffer)
    {
        if (_position >= FailAfterFrames)
        {
            throw new AudioSourceException("Synthetic decode error.");
        }
        var frames = (int)Math.Min(buffer.Length / channels, lengthFrames - _position);
        for (var f = 0; f < frames; f++)
        {
            for (var c = 0; c < channels; c++)
            {
                buffer[(f * channels) + c] = ValueAt(_position + f, c);
            }
        }
        _position += frames;
        return frames;
    }

    public void Seek(long frame) => _position = Math.Clamp(frame, 0, lengthFrames);

    public void Dispose() => Disposed = true;
}

/// <summary>Opens "name.syn?rate=44100&amp;seconds=1&amp;channels=2" style paths as synthetic sources. "bad.syn" fails.</summary>
internal sealed class SyntheticFactory : IAudioSourceFactory
{
    public string Name => "Synthetic";
    public IReadOnlyCollection<string> Extensions { get; } = [".syn"];
    public List<SyntheticSource> Opened { get; } = [];

    public static string PathFor(string name, int rate = 44_100, double seconds = 1, int channels = 2) =>
        Path.Combine(TempFiles.Folder, $"{name}_{rate}_{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}_{channels}.syn");

    public bool CanOpen(string path) => path.EndsWith(".syn", StringComparison.OrdinalIgnoreCase);

    public IAudioSource Open(string path)
    {
        var parts = Path.GetFileNameWithoutExtension(path).Split('_');
        if (parts[0] == "bad")
        {
            throw new AudioSourceException("Synthetic open failure.");
        }
        var rate = int.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        var seconds = double.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
        var channels = int.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
        var source = new SyntheticSource(rate, channels, (long)(rate * seconds), parts[0]);
        Opened.Add(source);
        return source;
    }
}

/// <summary>Creates the (empty) files synthetic paths point at, so the engine's existence check passes.</summary>
internal static class TempFiles
{
    public static string Folder { get; } = Directory.CreateTempSubdirectory("audiotastic-tests-").FullName;

    public static string Touch(string path)
    {
        File.WriteAllBytes(path, []);
        return path;
    }
}

/// <summary>
/// An output device that pulls audio on its own thread (like a real device, roughly 5–10× faster than real time)
/// and records what it rendered.
/// </summary>
internal sealed class FakeAudioOutput : IAudioOutput
{
    private const int BlockFrames = 256;
    private readonly Lock _captureLock = new();
    private readonly Lock _threadLock = new();
    private readonly List<float> _captured = [];
    private Thread? _thread;
    private volatile bool _running;
    private IAudioRenderer? _renderer;

    public string BackendName => "Fake";
    public bool IsOpen { get; private set; }
    public bool IsRunning => _running;
    public string? DeviceName { get; private set; }
    public AudioFormat? Format { get; private set; }
    public List<(string? Device, AudioFormat Format)> Opens { get; } = [];
    public List<OutputOptions> OpenOptions { get; } = [];
    public bool IsExclusive { get; private set; }
    public OutputSampleFormat SampleFormat { get; private set; }

    /// <summary>Native formats reported for exclusive-mode negotiation (PB-05).</summary>
    public List<DeviceFormat> DeviceFormats { get; } = [];

    /// <summary>Simulates a device that refuses exclusive mode.</summary>
    public bool RefuseExclusive { get; set; }

    public IReadOnlyList<DeviceFormat> GetDeviceFormats(AudioDeviceInfo? device) => DeviceFormats;
    public bool Capture { get; set; }
    public long Renders;

    public IReadOnlyList<AudioDeviceInfo> Devices { get; set; } =
    [
        new("Speakers", true, new byte[] { 1 }),
        new("Headphones", false, new byte[] { 2 }),
    ];

    public event EventHandler? DevicesChanged;
    public event EventHandler<AudioOutputLostEventArgs>? OutputLost;

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => NoDevice ? [] : Devices;

    /// <summary>Simulates a computer with no audio device at all (opening fails, the list is empty).</summary>
    public bool NoDevice { get; set; }

    public float[] Captured
    {
        get
        {
            lock (_captureLock)
            {
                return [.. _captured];
            }
        }
    }

    public void Open(AudioDeviceInfo? device, AudioFormat format, OutputOptions options, IAudioRenderer renderer)
    {
        Close(); // like MiniaudioOutput: the old device is closed before the new one is opened
        if (NoDevice)
        {
            throw new InvalidOperationException("Could not open 'default device' (test: no device).");
        }
        if (options.Exclusive && RefuseExclusive)
        {
            throw new InvalidOperationException("Exclusive mode refused (test).");
        }
        OpenOptions.Add(options);
        IsExclusive = options.Exclusive;
        SampleFormat = options.SampleFormat;
        _renderer = renderer;
        Format = format;
        DeviceName = device?.Name ?? "Speakers";
        IsOpen = true;
        Opens.Add((device?.Name, format));
    }

    public void StartOutput()
    {
        // Start and stop can be called from different threads (engine commands, Dispose): a stop must never see a
        // thread that exists but has not been started yet (seen on a CI runner).
        lock (_threadLock)
        {
            if (_running)
            {
                return;
            }
            _running = true;
            var thread = new Thread(RenderLoop) { IsBackground = true, Name = "Fake audio device" };
            thread.Start();
            _thread = thread;
        }
    }

    public void StopOutput()
    {
        Thread? thread;
        lock (_threadLock)
        {
            _running = false;
            thread = _thread;
            _thread = null;
        }
        if (thread is not null && thread != Thread.CurrentThread)
        {
            thread.Join();
        }
    }

    public void Close()
    {
        StopOutput();
        IsOpen = false;
        Format = null;
    }

    /// <summary>Simulates the device disappearing.</summary>
    public void Lose()
    {
        _running = false;
        OutputLost?.Invoke(this, new AudioOutputLostEventArgs("test"));
    }

    public void RaiseDevicesChanged() => DevicesChanged?.Invoke(this, EventArgs.Empty);

    public void Dispose() => Close();

    private void RenderLoop()
    {
        var buffer = new float[BlockFrames * Format!.Value.Channels];
        while (_running)
        {
            // This device pulls several times faster than real time. On a slow or busy machine (CI runners) the decoder
            // threads fall behind and the mixer would render underrun silence that a real device, pulling in real time,
            // never sees; so wait (up to 500 ms) until the decks can deliver, like the offline renderers do.
            if (_renderer is MixerGraph graph)
            {
                var deadline = Environment.TickCount64 + 500;
                while (_running && (!graph.DeckA.CanRender(BlockFrames) || !graph.DeckB.CanRender(BlockFrames)) && Environment.TickCount64 < deadline)
                {
                    Thread.Sleep(1);
                }
            }
            _renderer!.Render(buffer);
            Interlocked.Increment(ref Renders);
            if (Capture)
            {
                lock (_captureLock)
                {
                    _captured.AddRange(buffer);
                }
            }
            Thread.Sleep(1);
        }
    }
}

internal static class Wait
{
    public static async Task UntilAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException("Condition not met in time.");
            }
            await Task.Delay(5);
        }
    }
}
