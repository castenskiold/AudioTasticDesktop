namespace AudioTastic.Core.Audio;

/// <summary>
/// UI-07: the last <see cref="CapacityFrames"/> stereo frames after the DSP chain (before the master volume), for the
/// spectrum, the oscilloscope and visualisation plugins. The audio thread writes without locking; readers copy the
/// newest frames (a block may be torn while it is being written, which is invisible in a visualisation).
/// </summary>
public sealed class VisualisationFeed
{
    public const int CapacityFrames = 8192;

    private readonly float[] _ring = new float[CapacityFrames * 2];
    private long _written;
    private volatile int _sampleRate = 44_100;

    public int SampleRate => _sampleRate;

    /// <summary>Frames written so far (changes while audio plays).</summary>
    public long WrittenFrames => Interlocked.Read(ref _written);

    internal void Configure(int sampleRate) => _sampleRate = sampleRate;

    /// <summary>Audio thread: appends interleaved samples (mono is duplicated, more channels use the first two).</summary>
    public void Write(ReadOnlySpan<float> samples, int channels)
    {
        var frames = samples.Length / channels;
        var pos = Interlocked.Read(ref _written);
        for (var f = 0; f < frames; f++)
        {
            var i = (int)((pos + f) % CapacityFrames) * 2;
            _ring[i] = samples[f * channels];
            _ring[i + 1] = channels > 1 ? samples[(f * channels) + 1] : samples[f * channels];
        }
        Interlocked.Exchange(ref _written, pos + frames);
    }

    /// <summary>Copies the newest frames into <paramref name="stereo"/> (interleaved L/R); returns the frame count.</summary>
    public int ReadLatest(Span<float> stereo)
    {
        var frames = Math.Min(stereo.Length / 2, CapacityFrames);
        var end = Interlocked.Read(ref _written);
        var start = end - frames;
        for (var f = 0; f < frames; f++)
        {
            var frame = start + f;
            if (frame < 0)
            {
                stereo[f * 2] = 0;
                stereo[(f * 2) + 1] = 0;
                continue;
            }
            var i = (int)(frame % CapacityFrames) * 2;
            stereo[f * 2] = _ring[i];
            stereo[(f * 2) + 1] = _ring[i + 1];
        }
        return frames;
    }
}
