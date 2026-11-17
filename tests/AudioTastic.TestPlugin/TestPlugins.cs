using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;

namespace AudioTastic.TestPlugin;

/// <summary>
/// Test input plugin. mode.txt in the plugin folder selects the behaviour:
/// "good" (default), "init-throws", "read-throws", "open-throws".
/// It reports what happened by writing files into its data folder, because the test cannot share types with it.
/// </summary>
public sealed class TestInputPlugin : IInputPlugin, IPluginSettingsPage
{
    private IPluginContext? _context;
    private string _mode = "good";

    public IReadOnlyList<IAudioSourceFactory> SourceFactories => [new ToneFactory(_mode)];

    public void Initialize(IPluginContext context)
    {
        _context = context;
        var modeFile = Path.Combine(context.PluginFolder, "mode.txt");
        _mode = File.Exists(modeFile) ? File.ReadAllText(modeFile).Trim() : "good";
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

internal sealed class ToneFactory(string mode) : IAudioSourceFactory
{
    public string Name => "Test tone";
    public IReadOnlyCollection<string> Extensions => [".tst"];

    public bool CanOpen(string path) => string.Equals(Path.GetExtension(path), ".tst", StringComparison.OrdinalIgnoreCase);

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
