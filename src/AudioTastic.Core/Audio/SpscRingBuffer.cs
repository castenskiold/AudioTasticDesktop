namespace AudioTastic.Core.Audio;

/// <summary>
/// Lock-free single-producer / single-consumer ring buffer of float samples between a deck's decoder thread
/// (producer) and the audio callback (consumer). Neither side allocates or locks.
/// </summary>
internal sealed class SpscRingBuffer
{
    private readonly float[] _buffer;
    private readonly int _mask;
    private long _write; // written only by the producer
    private long _read;  // written only by the consumer

    /// <param name="minimumCapacity">Rounded up to a power of two.</param>
    public SpscRingBuffer(int minimumCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minimumCapacity, 1);
        var capacity = (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)minimumCapacity);
        _buffer = new float[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _buffer.Length;

    /// <summary>Samples ready for the consumer.</summary>
    public int AvailableToRead => (int)(Volatile.Read(ref _write) - Volatile.Read(ref _read));

    /// <summary>Free space for the producer.</summary>
    public int AvailableToWrite => Capacity - AvailableToRead;

    /// <summary>Producer: copies as much of <paramref name="data"/> as fits; returns the number of samples written.</summary>
    public int Write(ReadOnlySpan<float> data)
    {
        var write = _write;
        var free = Capacity - (int)(write - Volatile.Read(ref _read));
        var n = Math.Min(free, data.Length);
        if (n <= 0)
        {
            return 0;
        }
        var start = (int)(write & _mask);
        var first = Math.Min(n, Capacity - start);
        data[..first].CopyTo(_buffer.AsSpan(start, first));
        data[first..n].CopyTo(_buffer.AsSpan(0, n - first));
        Volatile.Write(ref _write, write + n); // publish after the copy
        return n;
    }

    /// <summary>Consumer: fills <paramref name="destination"/> with up to its length; returns the number of samples read.</summary>
    public int Read(Span<float> destination)
    {
        var read = _read;
        var available = (int)(Volatile.Read(ref _write) - read);
        var n = Math.Min(available, destination.Length);
        if (n <= 0)
        {
            return 0;
        }
        var start = (int)(read & _mask);
        var first = Math.Min(n, Capacity - start);
        _buffer.AsSpan(start, first).CopyTo(destination);
        _buffer.AsSpan(0, n - first).CopyTo(destination[first..]);
        Volatile.Write(ref _read, read + n); // release the space after the copy
        return n;
    }

    /// <summary>Empties the buffer. Only call while neither side is using it (deck stalled).</summary>
    public void Reset()
    {
        Volatile.Write(ref _read, 0);
        Volatile.Write(ref _write, 0);
    }
}
