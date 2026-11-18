using AudioTastic.Data.Settings;

namespace AudioTastic.Core.Audio.Dsp;

/// <summary>
/// EQ-01 / EQ-04: the equalizer: up to ten biquads in series (transposed direct form II, double-precision state)
/// and the overall gain. A new design is published with <see cref="Apply"/>; the filter state is kept, so changes
/// while playing are smooth enough for slider moves.
/// </summary>
public sealed class EqualizerStage() : DspStage(DspChainEntry.Equalizer, "Equalizer")
{
    private const int MaxChannels = 8;
    private EqualizerSettings _settings = new();
    private EqualizerDesign? _design;
    private double[] _state = new double[EqualizerDesign.MaxBands * MaxChannels * 2];
    private int _sampleRate = 44_100;

    /// <summary>The design in use (for the drawn curve).</summary>
    public EqualizerDesign Design => Volatile.Read(ref _design) ?? EqualizerDesign.Create(Volatile.Read(ref _settings), _sampleRate);

    /// <summary>Control thread: new settings take effect at the next block.</summary>
    public void Apply(EqualizerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var copy = settings.Clone();
        Volatile.Write(ref _settings, copy);
        Volatile.Write(ref _design, EqualizerDesign.Create(copy, Volatile.Read(ref _sampleRate)));
    }

    public override void Configure(int sampleRate, int channels)
    {
        Volatile.Write(ref _sampleRate, sampleRate);
        if (_state.Length < EqualizerDesign.MaxBands * channels * 2)
        {
            _state = new double[EqualizerDesign.MaxBands * channels * 2];
        }
        Array.Clear(_state);
        Volatile.Write(ref _design, EqualizerDesign.Create(Volatile.Read(ref _settings), sampleRate));
    }

    public override void Reset() => Array.Clear(_state);

    public override void Process(Span<float> samples, int channels)
    {
        var design = Volatile.Read(ref _design);
        if (design is null)
        {
            return;
        }
        var bands = design.Bands;
        var gain = design.Gain;
        var state = _state;
        var frames = samples.Length / channels;
        for (var c = 0; c < channels; c++)
        {
            for (var b = 0; b < bands.Count; b++)
            {
                var q = bands[b];
                if (q == Biquad.Identity)
                {
                    continue;
                }
                var s = ((b * channels) + c) * 2;
                double z1 = state[s], z2 = state[s + 1];
                for (int f = 0, i = c; f < frames; f++, i += channels)
                {
                    double x = samples[i];
                    var y = (q.B0 * x) + z1;
                    z1 = (q.B1 * x) - (q.A1 * y) + z2;
                    z2 = (q.B2 * x) - (q.A2 * y);
                    samples[i] = (float)y;
                }
                // Flush denormals once per block (they make the maths very slow after a fade to silence).
                state[s] = Math.Abs(z1) < 1e-30 ? 0 : z1;
                state[s + 1] = Math.Abs(z2) < 1e-30 ? 0 : z2;
            }
        }
        if (gain != 1f)
        {
            for (var i = 0; i < samples.Length; i++)
            {
                samples[i] *= gain;
            }
        }
    }
}
