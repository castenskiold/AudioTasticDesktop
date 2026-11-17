using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;

namespace GainDsp;

/// <summary>A DSP plugin: creates effect instances for the DSP chain.</summary>
public sealed class GainDspPlugin : IDspPlugin
{
    private IPluginSettings? _settings;

    public string Name => "Gain";

    public void Initialize(IPluginContext context) => _settings = context.Settings;

    public IDspEffect CreateEffect(AudioFormat format) => new GainEffect(_settings!);

    public void Dispose()
    {
    }
}

/// <summary>
/// Process runs on the audio thread: no allocation, no locks, no exceptions. The gain is read from settings when
/// they change (on another thread) and handed over through a volatile field.
/// </summary>
internal sealed class GainEffect : IDspEffect
{
    private readonly IPluginSettings _settings;
    private volatile float _gain;

    public GainEffect(IPluginSettings settings)
    {
        _settings = settings;
        _settings.Changed += OnChanged;
        OnChanged(this, "gainDb");
    }

    public void Process(Span<float> samples, int channels)
    {
        var gain = _gain;
        for (var i = 0; i < samples.Length; i++)
        {
            samples[i] *= gain;
        }
    }

    public void Reset()
    {
    }

    public void Dispose() => _settings.Changed -= OnChanged;

    private void OnChanged(object? sender, string key)
    {
        if (key == "gainDb")
        {
            _gain = MathF.Pow(10, _settings.GetValue("gainDb", -6f) / 20);
        }
    }
}
