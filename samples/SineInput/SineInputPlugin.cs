using System.Globalization;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;

namespace SineInput;

/// <summary>An input plugin: offers one decoder factory for the .sine extension.</summary>
public sealed class SineInputPlugin : IInputPlugin
{
    public IReadOnlyList<IAudioSourceFactory> SourceFactories { get; } = [new SineFactory()];

    public void Initialize(IPluginContext context)
    {
    }

    public void Dispose()
    {
    }
}

internal sealed class SineFactory : IAudioSourceFactory
{
    public string Name => "Sine tone";
    public IReadOnlyCollection<string> Extensions { get; } = [".sine"];

    public bool CanOpen(string path) => string.Equals(Path.GetExtension(path), ".sine", StringComparison.OrdinalIgnoreCase);

    public IAudioSource Open(string path)
    {
        // A decoder reports "cannot play this file" with AudioSourceException; anything else counts as a plugin fault.
        if (!double.TryParse(File.ReadAllText(path).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var hz) || hz is <= 0 or > 20000)
        {
            throw new AudioSourceException("A .sine file must contain a frequency between 0 and 20000 Hz.");
        }
        return new SineSource(hz);
    }
}

/// <summary>A decoded stream: interleaved float stereo, seekable, with a known length.</summary>
internal sealed class SineSource(double hz) : IAudioSource
{
    private const int Rate = 44100;

    public AudioStreamInfo Info { get; } = new("sine", Rate, 2, 32, 0, IsLossless: true);
    public AudioFormat Format => Info.OutputFormat;
    public long? LengthFrames => Rate * 10L;
    public long PositionFrames { get; private set; }
    public bool CanSeek => true;
    public IReadOnlyDictionary<string, string> Tags { get; } = new Dictionary<string, string> { ["title"] = $"{hz:0.#} Hz sine" };

    public int Read(Span<float> buffer)
    {
        var frames = (int)Math.Min(buffer.Length / 2, LengthFrames!.Value - PositionFrames);
        for (var i = 0; i < frames; i++)
        {
            var sample = (float)(0.2 * Math.Sin(2 * Math.PI * hz * (PositionFrames + i) / Rate));
            buffer[2 * i] = sample;
            buffer[2 * i + 1] = sample;
        }
        PositionFrames += frames;
        return frames;
    }

    public void Seek(long frame) => PositionFrames = Math.Clamp(frame, 0, LengthFrames!.Value);

    public void Dispose()
    {
    }
}
