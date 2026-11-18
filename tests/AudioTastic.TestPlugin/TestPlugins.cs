using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;

namespace AudioTastic.TestPlugin;

/// <summary>
/// Test input plugin. mode.txt in the plugin folder selects the behaviour:
/// "good" (default), "init-throws", "read-throws", "open-throws"; an optional second line sets the extension (".tst").
/// It reports what happened by writing files into its data folder, because the test cannot share types with it.
/// </summary>
public sealed class TestInputPlugin : IInputPlugin, IPluginSettingsPage
{
    private IPluginContext? _context;
    private string _mode = "good";
    private string _extension = ".tst";

    public IReadOnlyList<IAudioSourceFactory> SourceFactories => [new ToneFactory(_mode, _extension)];

    public void Initialize(IPluginContext context)
    {
        _context = context;
        var modeFile = Path.Combine(context.PluginFolder, "mode.txt");
        var lines = File.Exists(modeFile) ? File.ReadAllLines(modeFile) : [];
        _mode = lines.Length > 0 ? lines[0].Trim() : "good";
        _extension = lines.Length > 1 ? lines[1].Trim() : ".tst";
        if (_mode == "init-throws")
        {
            throw new InvalidOperationException("init failed on purpose");
        }
        var starts = context.Settings.GetValue("starts", 0) + 1;
        context.Settings.SetValue("starts", starts);
        File.WriteAllText(Path.Combine(context.DataFolder, "initialized.txt"), $"{context.PluginId} {starts}");
    }

    public object CreateSettingsPage() => "settings page";

    public void Dispose()
    {
        if (_context is not null)
        {
            File.WriteAllText(Path.Combine(_context.DataFolder, "disposed.txt"), "yes");
        }
    }
}

/// <summary>A second plugin class in the same assembly (the host must create every IPlugin class).</summary>
public sealed class TestGeneralPlugin : IGeneralPlugin
{
    public void Initialize(IPluginContext context) =>
        File.WriteAllText(Path.Combine(context.DataFolder, "general.txt"), context.Player.State.ToString());

    public void Dispose()
    {
    }
}

/// <summary>
/// Test output plugin (PB-04 / PG-01): one device, "Null sink", that pulls audio on its own thread and discards it.
/// Mode "output-open-throws" makes Open fail with a non-refusal exception (a plugin fault).
/// </summary>
public sealed class TestOutputPlugin : IOutputPlugin
{
    private string _mode = "good";

    public string Name => "Test output";

    public void Initialize(IPluginContext context)
    {
        var modeFile = Path.Combine(context.PluginFolder, "mode.txt");
        _mode = File.Exists(modeFile) ? File.ReadAllLines(modeFile).FirstOrDefault()?.Trim() ?? "good" : "good";
    }

    public IAudioOutput CreateOutput() => new NullSinkOutput(_mode == "output-open-throws");

    public void Dispose()
    {
    }
}

internal sealed class NullSinkOutput(bool openThrows) : IAudioOutput
{
    private IAudioRenderer? _renderer;
    private Thread? _thread;
    private volatile bool _running;

    public string BackendName => "Test sink";
    public bool IsOpen { get; private set; }
    public bool IsRunning => _running;
    public string? DeviceName { get; private set; }
    public AudioFormat? Format { get; private set; }

    public event EventHandler? DevicesChanged;
    public event EventHandler<AudioOutputLostEventArgs>? OutputLost;

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => [new AudioDeviceInfo("Null sink", true, new byte[] { 9 })];

    public void Open(AudioDeviceInfo? device, AudioFormat format, OutputOptions options, IAudioRenderer renderer)
    {
        if (openThrows)
        {
            throw new NotSupportedException("open failed on purpose");
        }
        if (options.Exclusive)
        {
            throw new InvalidOperationException("The null sink has no exclusive mode."); // a refusal, not a fault
        }
        Close();
        _renderer = renderer;
        Format = format;
        DeviceName = device?.Name ?? "Null sink";
        IsOpen = true;
    }

    public void StartOutput()
    {
        if (_running || _renderer is null || Format is not { } format)
        {
            return;
        }
        _running = true;
        _thread = new Thread(() =>
        {
            var buffer = new float[format.SampleRate / 100 * format.Channels];
            while (_running)
            {
                _renderer.Render(buffer);
                Thread.Sleep(5); // about twice real time
            }
        }) { IsBackground = true, Name = "Test sink" };
        _thread.Start();
    }

    public void StopOutput()
    {
        _running = false;
        if (_thread is { } thread && thread != Thread.CurrentThread)
        {
            thread.Join();
        }
        _thread = null;
    }

    public void Close()
    {
        StopOutput();
        IsOpen = false;
        Format = null;
        DeviceName = null;
    }

    public void Dispose() => Close();

    internal void RaiseEvents()
    {
        DevicesChanged?.Invoke(this, EventArgs.Empty);
        OutputLost?.Invoke(this, new AudioOutputLostEventArgs("test"));
    }
}

internal sealed class ToneFactory(string mode, string extension) : IAudioSourceFactory
{
    public string Name => "Test tone";
    public IReadOnlyCollection<string> Extensions => [extension];

    public bool CanOpen(string path) => string.Equals(Path.GetExtension(path), extension, StringComparison.OrdinalIgnoreCase);

    public IAudioSource Open(string path)
    {
        if (mode == "open-throws")
        {
            throw new InvalidOperationException("open failed on purpose");
        }
        if (path.Contains("missing", StringComparison.Ordinal))
        {
            throw new AudioSourceException("not a test file");
        }
        return new ToneSource(mode == "read-throws");
    }
}

internal sealed class ToneSource(bool throwOnRead) : IAudioSource
{
    private const int Rate = 8000;

    public AudioStreamInfo Info { get; } = new("test", Rate, 1, 16, 0, true);
    public AudioFormat Format => Info.OutputFormat;
    public long? LengthFrames => Rate;
    public long PositionFrames { get; private set; }
    public bool CanSeek => true;
    public IReadOnlyDictionary<string, string> Tags { get; } = new Dictionary<string, string> { ["title"] = "Tone" };

    public int Read(Span<float> buffer)
    {
        if (throwOnRead)
        {
            throw new InvalidOperationException("read failed on purpose");
        }
        var frames = (int)Math.Min(buffer.Length, Rate - PositionFrames);
        buffer[..frames].Fill(0.25f);
        PositionFrames += frames;
        return frames;
    }

    public void Seek(long frame) => PositionFrames = Math.Clamp(frame, 0, Rate);

    public void Dispose()
    {
    }
}
