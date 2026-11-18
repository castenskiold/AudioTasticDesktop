using AudioTastic.Sdk.Audio;

namespace AudioTastic.Core.Audio;

/// <summary>
/// PB-08: repeats the region [A, B) of a source. A read stops exactly at B, and the next read continues from A, so the
/// loop is seamless and sample-accurate. The region can be changed from any thread; it applies from the next read.
/// </summary>
public sealed class LoopSource(IAudioSource inner) : IAudioSource
{
    private long _a = -1;
    private long _b = -1;

    public IAudioSource Inner => inner;
    public AudioStreamInfo Info => inner.Info;
    public AudioFormat Format => inner.Format;
    public long? LengthFrames => inner.LengthFrames;
    public long PositionFrames => inner.PositionFrames;
    public bool CanSeek => inner.CanSeek;
    public IReadOnlyDictionary<string, string> Tags => inner.Tags;

    /// <summary>Loops source frames [<paramref name="a"/>, <paramref name="b"/>).</summary>
    public void SetRegion(long a, long b)
    {
        Volatile.Write(ref _a, a);
        Volatile.Write(ref _b, b);
    }

    public void ClearRegion()
    {
        Volatile.Write(ref _b, -1);
        Volatile.Write(ref _a, -1);
    }

    public int Read(Span<float> buffer)
    {
        var a = Volatile.Read(ref _a);
        var b = Volatile.Read(ref _b);
        if (a < 0 || b <= a)
        {
            return inner.Read(buffer);
        }
        var position = inner.PositionFrames;
        if (position >= b)
        {
            return inner.Read(buffer); // past B (the loop was set behind the position): play on
        }
        var channels = inner.Format.Channels;
        var limit = (int)Math.Min(buffer.Length / channels, b - position);
        var frames = inner.Read(buffer[..(limit * channels)]);
        if (frames == 0)
        {
            // The source ended before B: continue from A at once.
            inner.Seek(a);
            limit = (int)Math.Min(buffer.Length / channels, b - a);
            frames = inner.Read(buffer[..(limit * channels)]);
        }
        else if (inner.PositionFrames >= b)
        {
            inner.Seek(a); // B reached: the next read continues from A
        }
        return frames;
    }

    public void Seek(long frame) => inner.Seek(frame);

    public void Dispose() => inner.Dispose();
}
