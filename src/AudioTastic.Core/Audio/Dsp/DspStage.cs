namespace AudioTastic.Core.Audio.Dsp;

/// <summary>
/// EQ-05: one effect in the <see cref="DspChain"/>. <see cref="Process"/> runs on the audio thread and must not
/// allocate, lock or block; <see cref="Configure"/> runs while no audio is rendered (it may allocate). Parameters are
/// changed from other threads by publishing immutable values (Volatile.Write), never by mutating shared arrays.
/// </summary>
public abstract class DspStage
{
    private volatile bool _enabled;

    protected DspStage(string id, string name)
    {
        Id = id;
        Name = name;
    }

    /// <summary>Chain id (see <c>DspChainEntry</c>).</summary>
    public string Id { get; }

    public string Name { get; }

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>Called for each new graph format, before any <see cref="Process"/> at that format.</summary>
    public abstract void Configure(int sampleRate, int channels);

    /// <summary>EQ-06: how many frames this stage delays the audio (look-ahead, plug-in latency); 0 for most.</summary>
    public virtual int LatencyFrames => 0;

    /// <summary>Audio thread: processes interleaved samples in place.</summary>
    public abstract void Process(Span<float> samples, int channels);

    /// <summary>Audio thread: clears internal state (tails, envelopes) after a seek or a new track.</summary>
    public virtual void Reset()
    {
    }
}
