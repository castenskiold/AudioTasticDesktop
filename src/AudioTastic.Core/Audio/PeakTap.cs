namespace AudioTastic.Core.Audio;

/// <summary>
/// CO-07: a lock-free record of recent peaks for the live (scrolling) waveform of streams. The audio thread writes
/// one min/max pair per 10 ms of audio into a fixed ring; the UI reads the latest pairs. No allocation, no locks;
/// a reader may see a pair being written, which only affects one pixel of a drawing.
/// </summary>
public sealed class PeakTap
{
    /// <summary>Number of pairs kept: 10 seconds.</summary>
    public const int Capacity = 1000;

    private readonly float[] _min = new float[Capacity];
    private readonly float[] _max = new float[Capacity];
    private long _written;
    private volatile int _framesPerPair = 480;
    private int _inPair;
    private float _pairMin;
    private float _pairMax;

    /// <summary>Pairs written so far (the reader's change counter).</summary>
    public long Written => Interlocked.Read(ref _written);

    public void Configure(int sampleRate) => _framesPerPair = Math.Max(1, sampleRate / 100);

    /// <summary>Audio thread: records interleaved samples (all channels folded together).</summary>
    public void Write(ReadOnlySpan<float> interleaved, int channels)
    {
        var framesPerPair = _framesPerPair;
        var frames = interleaved.Length / channels;
        for (var f = 0; f < frames; f++)
        {
            for (var c = 0; c < channels; c++)
            {
                var v = interleaved[(f * channels) + c];
                if (v < _pairMin)
                {
                    _pairMin = v;
                }
                if (v > _pairMax)
                {
                    _pairMax = v;
                }
            }
            if (++_inPair >= framesPerPair)
            {
                var index = (int)(Interlocked.Read(ref _written) % Capacity);
                _min[index] = _pairMin;
                _max[index] = _pairMax;
                Interlocked.Increment(ref _written);
                _inPair = 0;
                _pairMin = 0;
                _pairMax = 0;
            }
        }
    }

    /// <summary>Copies the latest pairs, oldest first; returns how many were copied.</summary>
    public int ReadLatest(Span<float> min, Span<float> max)
    {
        var written = Written;
        var count = (int)Math.Min(Math.Min(min.Length, max.Length), Math.Min(written, Capacity));
        for (var i = 0; i < count; i++)
        {
            var index = (int)((written - count + i) % Capacity);
            min[i] = _min[index];
            max[i] = _max[index];
        }
        return count;
    }
}
