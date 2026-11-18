using AudioTastic.Sdk.Audio;

namespace AudioTastic.Core.Audio;

/// <summary>
/// PB-01 / PB-05: converts a source to another sample rate (own code, no native dependency). Band-limited
/// interpolation with a Kaiser-windowed sinc (32 zero crossings, β = 9.5, about −95 dB stop band), the cutoff
/// lowered to 97 % of the lower Nyquist frequency when converting down. The kernel is symmetric, so the output is
/// time-aligned with the input (no delay). Runs on the deck's decoder thread; seeks are sample-accurate in output
/// frames.
/// </summary>
public sealed class ResamplingSource : IAudioSource
{
    private const int ZeroCrossings = 32;
    private const int TablePhases = 512;
    private const double KaiserBeta = 9.5;
    private const int ReadFrames = 4096;

    private readonly IAudioSource _inner;
    private readonly int _channels;
    private readonly double _step;       // input frames per output frame
    private readonly int _halfWidth;     // kernel half-width in input frames
    private readonly float[] _table;     // kernel sampled at 1/TablePhases input frames, index 0 = centre
    private readonly float[] _readBuffer;
    private float[] _input;              // input frames [_inputStart, _inputStart + _inputFrames)
    private long _inputStart;
    private int _inputFrames;
    private bool _innerEnded;
    private long _outputIndex;

    public ResamplingSource(IAudioSource inner, int targetRate)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetRate, 1);
        _inner = inner;
        _channels = inner.Format.Channels;
        var sourceRate = inner.Format.SampleRate;
        _step = (double)sourceRate / targetRate;
        var cutoff = 0.97 * Math.Min(1.0, (double)targetRate / sourceRate); // fraction of the input Nyquist
        _halfWidth = (int)Math.Ceiling(ZeroCrossings / cutoff);
        _table = BuildTable(cutoff, _halfWidth);
        _readBuffer = new float[ReadFrames * _channels];
        _input = new float[(ReadFrames + (4 * _halfWidth) + 16) * _channels];
        Format = new AudioFormat(targetRate, _channels);
        LengthFrames = inner.LengthFrames is long n ? (long)Math.Floor(n / _step) : null;
        Restart(0);
    }

    public AudioStreamInfo Info => _inner.Info;
    public AudioFormat Format { get; }
    public long? LengthFrames { get; }
    public long PositionFrames => _outputIndex;
    public bool CanSeek => _inner.CanSeek;
    public IReadOnlyDictionary<string, string> Tags => _inner.Tags;

    /// <summary>The wrapped source (for stream metadata and diagnostics).</summary>
    public IAudioSource Inner => _inner;

    public int Read(Span<float> buffer)
    {
        var frames = buffer.Length / _channels;
        var written = 0;
        while (written < frames)
        {
            if (LengthFrames is long length && _outputIndex >= length)
            {
                break;
            }
            var t = _outputIndex * _step;
            var centre = (long)Math.Floor(t);
            var first = centre - _halfWidth + 1;
            var last = centre + _halfWidth;
            if (!Ensure(first, last))
            {
                break; // input exhausted (unknown length)
            }
            var o = written * _channels;
            for (var c = 0; c < _channels; c++)
            {
                buffer[o + c] = 0;
            }
            for (var k = first; k <= last; k++)
            {
                var weight = Kernel(k - t);
                if (weight == 0)
                {
                    continue;
                }
                var i = (int)(k - _inputStart) * _channels;
                for (var c = 0; c < _channels; c++)
                {
                    buffer[o + c] += weight * _input[i + c];
                }
            }
            written++;
            _outputIndex++;
        }
        return written;
    }

    public void Seek(long frame)
    {
        var target = Math.Max(0, frame);
        if (LengthFrames is long length)
        {
            target = Math.Min(target, length);
        }
        Restart(target);
    }

    public void Dispose() => _inner.Dispose();

    private void Restart(long outputFrame)
    {
        _outputIndex = outputFrame;
        var first = Math.Max(0, (long)Math.Floor(outputFrame * _step) - _halfWidth + 1);
        if (first > 0 || _inner.PositionFrames != 0)
        {
            _inner.Seek(first);
        }
        _inputStart = _inner.PositionFrames;
        _inputFrames = 0;
        _innerEnded = false;
    }

    /// <summary>Makes input frames [first, last] available (zeros before the start and after the end).</summary>
    private bool Ensure(long first, long last)
    {
        // Drop frames that are no longer needed.
        var drop = (int)Math.Clamp(first - _inputStart, 0, _inputFrames);
        if (drop > 0)
        {
            Array.Copy(_input, drop * _channels, _input, 0, (_inputFrames - drop) * _channels);
            _inputStart += drop;
            _inputFrames -= drop;
        }
        // Zeros before the start of the stream (leading kernel half).
        if (first < _inputStart && _inputFrames == 0 && _inputStart > first)
        {
            var zeros = (int)(_inputStart - first);
            EnsureCapacity(zeros + ReadFrames);
            Array.Clear(_input, 0, zeros * _channels);
            _inputStart = first;
            _inputFrames = zeros;
        }
        while (_inputStart + _inputFrames <= last)
        {
            var n = _innerEnded ? 0 : _inner.Read(_readBuffer);
            if (n == 0)
            {
                _innerEnded = true;
                if (LengthFrames is null && _outputIndex * _step >= _inputStart + _inputFrames - _halfWidth)
                {
                    return false; // unknown length: stop once the real input is used up
                }
                // Pad with silence after the end.
                var pad = (int)(last - (_inputStart + _inputFrames) + 1);
                EnsureCapacity(_inputFrames + pad);
                Array.Clear(_input, _inputFrames * _channels, pad * _channels);
                _inputFrames += pad;
                break;
            }
            EnsureCapacity(_inputFrames + n);
            Array.Copy(_readBuffer, 0, _input, _inputFrames * _channels, n * _channels);
            _inputFrames += n;
        }
        return true;
    }

    private void EnsureCapacity(int frames)
    {
        if (_input.Length < frames * _channels)
        {
            Array.Resize(ref _input, Math.Max(frames * _channels, _input.Length * 2));
        }
    }

    /// <summary>Kernel value at offset <paramref name="x"/> input frames (linear interpolation in the table).</summary>
    private float Kernel(double x)
    {
        var position = Math.Abs(x) * TablePhases;
        var index = (int)position;
        if (index >= _table.Length - 1)
        {
            return 0;
        }
        var f = (float)(position - index);
        return _table[index] + ((_table[index + 1] - _table[index]) * f);
    }

    private static float[] BuildTable(double cutoff, int halfWidth)
    {
        var size = (halfWidth * TablePhases) + 2;
        var table = new float[size];
        var i0Beta = BesselI0(KaiserBeta);
        for (var i = 0; i < size; i++)
        {
            var x = (double)i / TablePhases;
            if (x >= halfWidth)
            {
                table[i] = 0;
                continue;
            }
            var sinc = x == 0 ? 1.0 : Math.Sin(Math.PI * cutoff * x) / (Math.PI * cutoff * x);
            var r = x / halfWidth;
            var window = BesselI0(KaiserBeta * Math.Sqrt(1 - (r * r))) / i0Beta;
            table[i] = (float)(cutoff * sinc * window);
        }
        return table;
    }

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1;
        for (var k = 1; k < 50; k++)
        {
            term *= x / (2 * k);
            var t2 = term * term;
            sum += t2;
            if (t2 < sum * 1e-12)
            {
                break;
            }
        }
        return sum;
    }
}
