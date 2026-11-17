namespace AudioTastic.Core.Audio;

/// <summary>
/// CO-05: a gain applied in the mixer graph (master volume, and per deck for crossfades later).
/// The target is set from any thread (lock-free); the audio thread ramps towards it so changes never click.
/// </summary>
public sealed class GainStage
{
    /// <summary>A full 0 → 1 change takes this long.</summary>
    public const double DefaultRampSeconds = 0.010;

    private float _target;
    private float _current;
    private float _stepPerFrame;

    public GainStage(float initialGain = 1f, int sampleRate = 48_000)
    {
        _target = initialGain;
        _current = initialGain;
        Configure(sampleRate);
    }

    /// <summary>Sets the ramp speed for <paramref name="sampleRate"/>. Call while the graph is not rendering.</summary>
    public void Configure(int sampleRate, double rampSeconds = DefaultRampSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        _stepPerFrame = (float)(1.0 / Math.Max(1.0, sampleRate * rampSeconds));
    }

    /// <summary>Linear gain the stage moves towards (0 = silence, 1 = unity).</summary>
    public float TargetGain
    {
        get => Volatile.Read(ref _target);
        set => Volatile.Write(ref _target, Math.Clamp(value, 0f, 4f));
    }

    /// <summary>Gain currently applied (audio thread view).</summary>
    public float CurrentGain => _current;

    /// <summary>Jumps to the target without a ramp (e.g. before playback starts).</summary>
    public void SnapToTarget() => _current = TargetGain;

    /// <summary>Audio thread: applies the gain to interleaved <paramref name="samples"/> in place.</summary>
    public void Process(Span<float> samples, int channels)
    {
        var target = Volatile.Read(ref _target);
        var current = _current;
        if (current == target)
        {
            if (target != 1f)
            {
                for (var i = 0; i < samples.Length; i++)
                {
                    samples[i] *= target;
                }
            }
            return;
        }

        var step = _stepPerFrame;
        for (var i = 0; i < samples.Length; i += channels)
        {
            if (current < target)
            {
                current = Math.Min(target, current + step);
            }
            else if (current > target)
            {
                current = Math.Max(target, current - step);
            }
            for (var c = 0; c < channels && i + c < samples.Length; c++)
            {
                samples[i + c] *= current;
            }
        }
        _current = current;
    }

    /// <summary>
    /// CO-05 perceptual volume curve: 0–100 → linear gain using a cubic taper (as PulseAudio does), which tracks
    /// perceived loudness closely: 50 % ≈ −18 dB, 10 % ≈ −60 dB, 0 = silence.
    /// </summary>
    public static float VolumeToGain(int volume)
    {
        var v = Math.Clamp(volume, 0, 100) / 100f;
        return v * v * v;
    }
}
