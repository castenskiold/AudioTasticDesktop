using AudioTastic.Data.Settings;

namespace AudioTastic.Core.Audio.Dsp;

/// <summary>EQ-05: feed-forward compressor, stereo-linked peak detector, gain smoothing in dB.</summary>
public sealed class CompressorStage() : DspStage(DspChainEntry.Compressor, "Compressor")
{
    private sealed record Parameters(double Threshold, double Slope, double Attack, double Release, double Makeup)
    {
        public float ThresholdLinear { get; } = (float)Math.Pow(10, Threshold / 20);
        public float MakeupLinear { get; } = (float)Math.Pow(10, Makeup / 20);
    }

    private CompressorSettings _settings = new();
    private Parameters? _p;
    private int _sampleRate = 44_100;
    private double _envelope; // gain reduction in dB (>= 0)

    public void Apply(CompressorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _settings, settings);
        Volatile.Write(ref _p, Build(settings, Volatile.Read(ref _sampleRate)));
    }

    public override void Configure(int sampleRate, int channels)
    {
        Volatile.Write(ref _sampleRate, sampleRate);
        Volatile.Write(ref _p, Build(Volatile.Read(ref _settings), sampleRate));
        _envelope = 0;
    }

    public override void Reset() => _envelope = 0;

    private static Parameters Build(CompressorSettings s, int rate) => new(
        Math.Clamp(s.ThresholdDb, -60, 0),
        1 - (1 / Math.Clamp(s.Ratio, 1, 20)),
        Coefficient(s.AttackMs, rate),
        Coefficient(s.ReleaseMs, rate),
        Math.Clamp(s.MakeupDb, 0, 24));

    internal static double Coefficient(double ms, int rate) => Math.Exp(-1.0 / (Math.Max(0.1, ms) * 0.001 * rate));

    public override void Process(Span<float> samples, int channels)
    {
        if (Volatile.Read(ref _p) is not { } p)
        {
            return;
        }
        var env = _envelope;
        for (var i = 0; i + channels <= samples.Length; i += channels)
        {
            var peak = 0f;
            for (var c = 0; c < channels; c++)
            {
                peak = Math.Max(peak, Math.Abs(samples[i + c]));
            }
            // Below the threshold the target reduction is 0: no logarithm needed.
            var target = peak > p.ThresholdLinear ? ((20 * MathF.Log10(peak)) - p.Threshold) * p.Slope : 0;
            var k = target > env ? p.Attack : p.Release;
            env = target + (k * (env - target));
            var gain = env < 1e-4 ? p.MakeupLinear : MathF.Pow(10, (float)((p.Makeup - env) / 20));
            for (var c = 0; c < channels; c++)
            {
                samples[i + c] *= gain;
            }
        }
        _envelope = env < 1e-12 ? 0 : env;
    }
}

/// <summary>
/// EQ-05: look-ahead brick-wall limiter. The gain at each output frame is the smallest gain any frame in the next
/// 5 ms needs (sliding minimum), so no sample exceeds the ceiling; the gain recovers with the release time.
/// Adds 5 ms of latency.
/// </summary>
public sealed class LimiterStage() : DspStage(DspChainEntry.Limiter, "Limiter")
{
    private sealed record Parameters(float Ceiling, double Release);

    private LimiterSettings _settings = new();
    private Parameters? _p;
    private int _sampleRate = 44_100;
    private int _channels = 2;
    private int _lookahead;
    private float[] _delay = [];      // delayed samples, ring of _lookahead + 1 frames
    private float[] _required = [];   // required gain per frame in the ring
    private long[] _minQueue = [];    // monotonic queue of absolute frame numbers (sliding minimum)
    private int _qStart;
    private int _qCount;
    private long _frame;
    private float _gain = 1f;

    public void Apply(LimiterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _settings, settings);
        Volatile.Write(ref _p, Build(settings, Volatile.Read(ref _sampleRate)));
    }

    public override void Configure(int sampleRate, int channels)
    {
        _sampleRate = sampleRate;
        _channels = channels;
        _lookahead = Math.Max(1, sampleRate * 5 / 1000);
        var size = _lookahead + 1;
        _delay = new float[size * channels];
        _required = new float[size];
        _minQueue = new long[size];
        Volatile.Write(ref _p, Build(Volatile.Read(ref _settings), sampleRate));
        Reset();
    }

    public override void Reset()
    {
        Array.Clear(_delay);
        Array.Fill(_required, 1f);
        _qStart = 0;
        _qCount = 0;
        _frame = 0;
        _gain = 1f;
    }

    private static Parameters Build(LimiterSettings s, int rate) =>
        new((float)Math.Pow(10, Math.Clamp(s.CeilingDb, -24, 0) / 20), CompressorStage.Coefficient(s.ReleaseMs, rate));

    /// <summary>Latency in frames.</summary>
    public int LatencyFrames => _lookahead;

    public override void Process(Span<float> samples, int channels)
    {
        if (Volatile.Read(ref _p) is not { } p || channels != _channels || _required.Length == 0)
        {
            return;
        }
        var size = _required.Length; // lookahead + 1 frames
        for (var i = 0; i + channels <= samples.Length; i += channels)
        {
            var t = _frame;
            // The output frame is t - lookahead; the window is frames [t - lookahead, t]. Drop older queue entries
            // first: the ring slot of frame t - lookahead - 1 is overwritten next.
            while (_qCount > 0 && _minQueue[_qStart] < t - _lookahead)
            {
                _qStart = (_qStart + 1) % size;
                _qCount--;
            }
            var pos = (int)(t % size);
            var peak = 0f;
            for (var c = 0; c < channels; c++)
            {
                var x = samples[i + c];
                peak = Math.Max(peak, Math.Abs(x));
                _delay[(pos * channels) + c] = x;
            }
            var need = peak > p.Ceiling ? p.Ceiling / peak : 1f;
            _required[pos] = need;
            while (_qCount > 0 && _required[(int)(_minQueue[(_qStart + _qCount - 1) % size] % size)] >= need)
            {
                _qCount--;
            }
            _minQueue[(_qStart + _qCount) % size] = t;
            _qCount++;
            var target = _required[(int)(_minQueue[_qStart] % size)];
            _gain = target < _gain ? target : target + ((float)p.Release * (_gain - target));
            var outPos = (int)((t + 1) % size); // = (t - lookahead) mod size
            for (var c = 0; c < channels; c++)
            {
                samples[i + c] = _delay[(outPos * channels) + c] * _gain;
            }
            _frame = t + 1;
        }
    }
}

/// <summary>EQ-05: mid/side stereo widening (stereo only).</summary>
public sealed class WidenerStage() : DspStage(DspChainEntry.Widener, "Stereo widening")
{
    private float _width = 1.5f;

    public void Apply(WidenerSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _width, (float)Math.Clamp(settings.Width, 0, 2));
    }

    public override void Configure(int sampleRate, int channels)
    {
    }

    public override void Process(Span<float> samples, int channels)
    {
        if (channels != 2)
        {
            return;
        }
        var width = Volatile.Read(ref _width);
        if (width == 1f)
        {
            return; // unchanged, bit-exact
        }
        for (var i = 0; i + 1 < samples.Length; i += 2)
        {
            var mid = (samples[i] + samples[i + 1]) * 0.5f;
            var side = (samples[i] - samples[i + 1]) * 0.5f * width;
            samples[i] = mid + side;
            samples[i + 1] = mid - side;
        }
    }
}

/// <summary>
/// EQ-05: reverb after Jezar's public-domain Freeverb design: eight damped comb filters and four all-passes per
/// channel, tunings scaled to the sample rate. The dry signal stays at full level; Mix sets the reverb level.
/// </summary>
public sealed class ReverbStage() : DspStage(DspChainEntry.Reverb, "Reverb")
{
    private static readonly int[] CombTuning = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllpassTuning = [556, 441, 341, 225];
    private const int StereoSpread = 23;
    private const float FixedGain = 0.015f;
    private const int MaxBlock = 1024; // frames per inner pass

    private sealed record Parameters(float Feedback, float Damp1, float Damp2, float Wet1, float Wet2);

    private ReverbSettings _settings = new();
    private Parameters? _p;
    private float[][] _comb = [];
    private int[] _combIndex = [];
    private float[] _combStore = [];
    private float[][] _allpass = [];
    private int[] _allpassIndex = [];
    private float[] _input = [];
    private float[] _outL = [];
    private float[] _outR = [];

    public void Apply(ReverbSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _settings, settings);
        Volatile.Write(ref _p, Build(settings));
    }

    public override void Configure(int sampleRate, int channels)
    {
        var scale = sampleRate / 44100.0;
        _comb = new float[CombTuning.Length * 2][];
        _allpass = new float[AllpassTuning.Length * 2][];
        for (var side = 0; side < 2; side++)
        {
            var spread = side * StereoSpread;
            for (var i = 0; i < CombTuning.Length; i++)
            {
                _comb[(side * CombTuning.Length) + i] = new float[Math.Max(1, (int)((CombTuning[i] + spread) * scale))];
            }
            for (var i = 0; i < AllpassTuning.Length; i++)
            {
                _allpass[(side * AllpassTuning.Length) + i] = new float[Math.Max(1, (int)((AllpassTuning[i] + spread) * scale))];
            }
        }
        _combIndex = new int[_comb.Length];
        _combStore = new float[_comb.Length];
        _allpassIndex = new int[_allpass.Length];
        _input = new float[MaxBlock];
        _outL = new float[MaxBlock];
        _outR = new float[MaxBlock];
        Volatile.Write(ref _p, Build(Volatile.Read(ref _settings)));
    }

    public override void Reset()
    {
        foreach (var buffer in _comb)
        {
            Array.Clear(buffer);
        }
        foreach (var buffer in _allpass)
        {
            Array.Clear(buffer);
        }
        Array.Clear(_combStore);
    }

    private static Parameters Build(ReverbSettings s)
    {
        var room = (Math.Clamp(s.RoomSize, 0, 1) * 0.28) + 0.7;
        var damp = Math.Clamp(s.Damping, 0, 1) * 0.4;
        var wet = Math.Clamp(s.Mix, 0, 1) * 3;
        var width = Math.Clamp(s.Width, 0, 1);
        return new Parameters((float)room, (float)damp, (float)(1 - damp), (float)(wet * ((width / 2) + 0.5)), (float)(wet * ((1 - width) / 2)));
    }

    public override void Process(Span<float> samples, int channels)
    {
        if (Volatile.Read(ref _p) is not { } p || channels != 2 || _comb.Length == 0)
        {
            return;
        }
        var frames = samples.Length / 2;
        for (var start = 0; start < frames; start += MaxBlock)
        {
            var n = Math.Min(MaxBlock, frames - start);
            var block = samples.Slice(start * 2, n * 2);
            var input = _input.AsSpan(0, n);
            for (var f = 0; f < n; f++)
            {
                input[f] = (block[f * 2] + block[(f * 2) + 1]) * FixedGain;
            }
            // One filter at a time over the whole block (tight loops, buffers stay in cache).
            var outL = _outL.AsSpan(0, n);
            var outR = _outR.AsSpan(0, n);
            outL.Clear();
            outR.Clear();
            for (var c = 0; c < CombTuning.Length; c++)
            {
                Comb(c, input, outL, p);
                Comb(CombTuning.Length + c, input, outR, p);
            }
            for (var a = 0; a < AllpassTuning.Length; a++)
            {
                Allpass(a, outL);
                Allpass(AllpassTuning.Length + a, outR);
            }
            for (var f = 0; f < n; f++)
            {
                block[f * 2] += (outL[f] * p.Wet1) + (outR[f] * p.Wet2);
                block[(f * 2) + 1] += (outR[f] * p.Wet1) + (outL[f] * p.Wet2);
            }
        }
    }

    /// <summary>Damped comb: adds its output for each input sample to <paramref name="output"/>.</summary>
    private void Comb(int n, ReadOnlySpan<float> input, Span<float> output, Parameters p)
    {
        var buffer = _comb[n];
        var index = _combIndex[n];
        var store = _combStore[n];
        float damp1 = p.Damp1, damp2 = p.Damp2, feedback = p.Feedback;
        for (var f = 0; f < input.Length; f++)
        {
            var o = buffer[index];
            store = (o * damp2) + (store * damp1);
            buffer[index] = input[f] + (store * feedback);
            output[f] += o;
            if (++index == buffer.Length)
            {
                index = 0;
            }
        }
        _combStore[n] = Math.Abs(store) < 1e-25f ? 0 : store;
        _combIndex[n] = index;
    }

    /// <summary>All-pass, in place.</summary>
    private void Allpass(int n, Span<float> samples)
    {
        var buffer = _allpass[n];
        var index = _allpassIndex[n];
        for (var f = 0; f < samples.Length; f++)
        {
            var stored = buffer[index];
            var x = samples[f];
            buffer[index] = x + (stored * 0.5f);
            samples[f] = stored - x;
            if (++index == buffer.Length)
            {
                index = 0;
            }
        }
        _allpassIndex[n] = index;
    }
}
