namespace AudioTastic.Core.Audio.Dsp;

/// <summary>
/// EQ-05: the ordered effects between the deck mix and the master volume (Feature Specification section 3:
/// decks → ReplayGain → DSP chain → volume → output). The stage list is an immutable array swapped atomically, so
/// the audio thread never locks; switched-off stages are skipped.
/// </summary>
public sealed class DspChain
{
    private readonly Lock _control = new(); // SetStages vs Configure (control threads only; never the audio thread)
    private DspStage[] _stages = [];
    private volatile bool _bypass;
    private volatile bool _resetRequested;
    private int _sampleRate;
    private int _channels;

    public IReadOnlyList<DspStage> Stages => Volatile.Read(ref _stages);

    /// <summary>FM-05: true while a bit-perfect stream (DoP) plays: nothing may touch the samples.</summary>
    public bool Bypass
    {
        get => _bypass;
        set => _bypass = value;
    }

    /// <summary>
    /// Control thread: replaces the stage list. New stages are configured for the current format first. Stages that
    /// left the chain may still be inside a render; dispose them only after a render boundary.
    /// </summary>
    public void SetStages(IEnumerable<DspStage> stages)
    {
        ArgumentNullException.ThrowIfNull(stages);
        var next = stages.ToArray();
        lock (_control)
        {
            var current = Volatile.Read(ref _stages);
            if (_sampleRate > 0)
            {
                foreach (var stage in next.Where(s => !current.Contains(s)))
                {
                    stage.Configure(_sampleRate, _channels);
                }
            }
            Volatile.Write(ref _stages, next);
        }
    }

    /// <summary>Graph format change (no audio is rendered meanwhile).</summary>
    public void Configure(int sampleRate, int channels)
    {
        lock (_control)
        {
            _sampleRate = sampleRate;
            _channels = channels;
            foreach (var stage in Volatile.Read(ref _stages))
            {
                stage.Configure(sampleRate, channels);
            }
        }
    }

    /// <summary>Any thread: clear effect state (reverb tails etc.) before the next block (seek, new track).</summary>
    public void RequestReset() => _resetRequested = true;

    /// <summary>Audio thread.</summary>
    public void Process(Span<float> samples, int channels)
    {
        var stages = Volatile.Read(ref _stages);
        if (_resetRequested)
        {
            _resetRequested = false;
            foreach (var stage in stages)
            {
                stage.Reset();
            }
        }
        if (_bypass)
        {
            return;
        }
        foreach (var stage in stages)
        {
            if (stage.Enabled)
            {
                stage.Process(samples, channels);
            }
        }
    }
}
