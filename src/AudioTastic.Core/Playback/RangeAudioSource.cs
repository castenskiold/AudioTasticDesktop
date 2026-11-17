using AudioTastic.Sdk.Audio;

namespace AudioTastic.Core.Playback;

/// <summary>
/// FM-09: a CUE virtual track: exposes only [<c>start</c>, <c>end</c>) of another source. Positions, length and
/// seeking are relative to the start of the range, so the time display, seek bar and track end work per track.
/// Takes ownership of the inner source.
/// </summary>
public sealed class RangeAudioSource : IAudioSource
{
    private readonly IAudioSource _inner;
    private readonly long _startFrame;
    private readonly long? _endFrame;

    public RangeAudioSource(IAudioSource inner, TimeSpan start, TimeSpan? end)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        var rate = inner.Format.SampleRate;
        _startFrame = ToFrames(start, rate);
        _endFrame = end is { } e ? ToFrames(e, rate) : null;
        if (inner.LengthFrames is long length)
        {
            _startFrame = Math.Min(_startFrame, length);
            _endFrame = _endFrame is long f ? Math.Min(f, length) : null;
        }
        if (_endFrame is long endFrame && endFrame < _startFrame)
        {
            _endFrame = _startFrame;
        }
        if (_startFrame > 0)
        {
            inner.Seek(_startFrame);
        }
    }

    public AudioStreamInfo Info => _inner.Info;
    public AudioFormat Format => _inner.Format;
    public IReadOnlyDictionary<string, string> Tags => _inner.Tags;
    public bool CanSeek => _inner.CanSeek;

    public long? LengthFrames =>
        _endFrame is long end ? end - _startFrame
        : _inner.LengthFrames is long length ? length - _startFrame
        : null;

    public long PositionFrames => _inner.PositionFrames - _startFrame;

    public int Read(Span<float> buffer)
    {
        if (_endFrame is long end)
        {
            var remaining = Math.Max(0, end - _inner.PositionFrames) * Format.Channels;
            if (remaining < buffer.Length)
            {
                buffer = buffer[..(int)remaining];
            }
            if (buffer.IsEmpty)
            {
                return 0;
            }
        }
        return _inner.Read(buffer);
    }

    public void Seek(long frame)
    {
        var target = Math.Max(0, frame);
        if (LengthFrames is long length)
        {
            target = Math.Min(target, length);
        }
        _inner.Seek(_startFrame + target);
    }

    public void Dispose() => _inner.Dispose();

    /// <summary>Exact for CUE times (1/75 s) at 44.1 kHz and 48 kHz.</summary>
    internal static long ToFrames(TimeSpan time, int sampleRate) =>
        (long)Math.Round(time.Ticks * (double)sampleRate / TimeSpan.TicksPerSecond);
}
