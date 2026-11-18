namespace AudioTastic.Core.Audio;

/// <summary>
/// PB-02 (and PB-10's sleep fade): fades the whole output in or out. Ramps are linear in dB between −60 dB and
/// 0 dB (an exponential gain curve, which sounds even), then snap to silence or unity. The control thread requests a
/// fade; the audio thread applies it without allocation or locks.
/// </summary>
public sealed class FadeStage
{
    private const double FloorDb = -60.0;
    private static readonly float Floor = (float)Math.Pow(10, FloorDb / 20);

    private int _sampleRate = 48_000;
    private int _requestVersion;
    private int _appliedVersion;
    private volatile bool _requestIn;
    private volatile int _requestFrames;
    private volatile bool _requestSnap;
    private volatile bool _requestFromSilence;
    private float _gain = 1;
    private float _step = 1;
    private bool _fadingIn = true;

    /// <summary>The gain right now (1 = no fade, 0 = silent).</summary>
    public float CurrentGain => Volatile.Read(ref _gain);

    /// <summary>True when the output is fully faded out.</summary>
    public bool IsSilent => Volatile.Read(ref _gain) == 0 && !_requestIn;

    public void Configure(int sampleRate) => _sampleRate = Math.Max(1, sampleRate);

    /// <summary>Starts a fade out over <paramref name="milliseconds"/> (0 = cut now).</summary>
    public void FadeOut(int milliseconds) => Request(fadeIn: false, milliseconds, snap: milliseconds <= 0);

    /// <summary>
    /// Starts a fade in over <paramref name="milliseconds"/> (0 = full level now), from the current level or, with
    /// <paramref name="fromSilence"/>, from silence.
    /// </summary>
    public void FadeIn(int milliseconds, bool fromSilence = false) => Request(fadeIn: true, milliseconds, snap: milliseconds <= 0, fromSilence);

    /// <summary>Jumps to full level (or silence) without a ramp.</summary>
    public void Snap(bool on) => Request(fadeIn: on, 0, snap: true);

    /// <summary>Audio thread: applies the fade to interleaved samples.</summary>
    public void Process(Span<float> samples, int channels)
    {
        var version = Volatile.Read(ref _requestVersion);
        if (version != _appliedVersion)
        {
            _appliedVersion = version;
            _fadingIn = _requestIn;
            var frames = Math.Max(1, _requestFrames);
            if (_requestSnap)
            {
                _gain = _fadingIn ? 1f : 0f;
            }
            else
            {
                // 60 dB over the fade length: multiply by a constant factor per frame.
                _step = (float)Math.Pow(10, -FloorDb / 20 / frames);
                if (!_fadingIn)
                {
                    _step = 1 / _step;
                }
                if (_fadingIn && (_gain < Floor || _requestFromSilence))
                {
                    _gain = Floor;
                }
            }
        }
        var gain = _gain;
        if (gain == 1f && _fadingIn)
        {
            return;
        }
        var frameCount = samples.Length / channels;
        for (var f = 0; f < frameCount; f++)
        {
            if (_fadingIn ? gain is > 0f and < 1f : gain > 0f)
            {
                gain *= _step;
                if (_fadingIn && gain >= 1f)
                {
                    gain = 1f;
                }
                else if (!_fadingIn && gain <= Floor)
                {
                    gain = 0f;
                }
            }
            if (gain == 1f)
            {
                continue;
            }
            for (var c = 0; c < channels; c++)
            {
                samples[(f * channels) + c] *= gain;
            }
        }
        Volatile.Write(ref _gain, gain);
    }

    private void Request(bool fadeIn, int milliseconds, bool snap, bool fromSilence = false)
    {
        _requestFromSilence = fromSilence;
        _requestIn = fadeIn;
        _requestFrames = (int)((long)Math.Max(0, milliseconds) * _sampleRate / 1000);
        _requestSnap = snap;
        Interlocked.Increment(ref _requestVersion);
    }
}
