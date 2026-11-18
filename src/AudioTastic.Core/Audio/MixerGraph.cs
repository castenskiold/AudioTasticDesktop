using System.Diagnostics;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.Core.Audio;

/// <summary>
/// The engine's mixer graph (audio pipeline, Feature Specification section 3):
/// deck A + deck B → (P6+: time-stretch, ReplayGain) → sum → (P7: DSP chain) → master <see cref="GainStage"/> → output.
/// <see cref="Render"/> runs on the audio thread and never allocates or locks.
/// </summary>
public sealed class MixerGraph : IAudioRenderer, IDisposable
{
    /// <summary>Largest block rendered in one pass; bigger requests are split.</summary>
    private const int MaxBlockFrames = 4096;

    private float[] _scratch;
    private long _renderCount;
    private volatile bool _paused;
    private volatile bool _outputActive;
    private Deck? _next;
    private long _handovers;

    public MixerGraph(AudioFormat format)
    {
        Format = format;
        _scratch = new float[MaxBlockFrames * format.Channels];
        DeckA = new Deck("A", this);
        DeckB = new Deck("B", this);
        Configure(format);
    }

    /// <summary>Format the graph renders (output side). Changed only via <see cref="Configure"/>.</summary>
    public AudioFormat Format { get; private set; }

    public Deck DeckA { get; }
    public Deck DeckB { get; }

    /// <summary>CO-07: peaks of the mixed decks before the master volume (live waveform for streams).</summary>
    public PeakTap Peaks { get; } = new();

    /// <summary>PB-02 / PB-10: fades on pause, stop, seek and the sleep timer, after the master volume.</summary>
    public FadeStage Fade { get; } = new();

    /// <summary>CO-05: master volume and mute, applied after mixing (after the DSP chain from P7).</summary>
    public GainStage Master { get; } = new();

    /// <summary>When true the graph outputs silence and does not consume the decks.</summary>
    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Set by the engine while an output device is pulling audio; lets decks skip waiting when it is not.</summary>
    public bool OutputActive
    {
        get => _outputActive;
        set => _outputActive = value;
    }

    /// <summary>
    /// PB-01: the armed deck to start, in the same render, the moment the other deck ends (gapless hand-over);
    /// null for none. Set by the engine after arming the deck.
    /// </summary>
    public Deck? Next
    {
        get => Volatile.Read(ref _next);
        set => Volatile.Write(ref _next, value);
    }

    /// <summary>Number of gapless hand-overs so far (the engine watches it).</summary>
    public long Handovers => Interlocked.Read(ref _handovers);

    /// <summary>Number of completed <see cref="Render"/> calls.</summary>
    public long RenderCount => Interlocked.Read(ref _renderCount);

    /// <summary>Changes the graph format. Only while both decks are idle and no output is running.</summary>
    public void Configure(AudioFormat format)
    {
        if (DeckA.IsActive || DeckB.IsActive)
        {
            throw new InvalidOperationException("Unload both decks before changing the graph format.");
        }
        Format = format;
        if (_scratch.Length < MaxBlockFrames * format.Channels)
        {
            _scratch = new float[MaxBlockFrames * format.Channels];
        }
        Master.Configure(format.SampleRate);
        Peaks.Configure(format.SampleRate);
        Fade.Configure(format.SampleRate);
        DeckA.Gain.Configure(format.SampleRate);
        DeckB.Gain.Configure(format.SampleRate);
    }

    /// <summary>Audio thread: fills <paramref name="output"/> (interleaved, <see cref="Format"/>).</summary>
    public void Render(Span<float> output)
    {
        output.Clear();
        if (!_paused)
        {
            var block = MaxBlockFrames * Format.Channels;
            for (var offset = 0; offset < output.Length; offset += block)
            {
                var chunk = output.Slice(offset, Math.Min(block, output.Length - offset));
                var a = DeckA.MixInto(chunk, _scratch);
                var b = DeckB.MixInto(chunk, _scratch);
                // PB-01: when the playing deck ends inside this block, the armed deck continues right after its
                // last sample, so the join is sample-exact.
                if (Volatile.Read(ref _next) is { } next && next.IsArmed)
                {
                    var current = ReferenceEquals(next, DeckA) ? DeckB : DeckA;
                    var written = ReferenceEquals(current, DeckA) ? a : b;
                    if (current.HasEnded && next.StartArmed())
                    {
                        Volatile.Write(ref _next, null);
                        next.MixInto(chunk[written..], _scratch);
                        Interlocked.Increment(ref _handovers);
                    }
                }
            }
            Peaks.Write(output, Format.Channels);
            Master.Process(output, Format.Channels);
            Fade.Process(output, Format.Channels);
        }
        Interlocked.Increment(ref _renderCount);
    }

    /// <summary>
    /// Blocks until any render that may have started before the call has completed (two render boundaries).
    /// Returns at once when no output is active, and gives up after 250 ms if the device stalls.
    /// </summary>
    internal void WaitForRenderBoundary()
    {
        if (!_outputActive)
        {
            return;
        }
        var start = RenderCount;
        var timer = Stopwatch.StartNew();
        while (RenderCount < start + 2 && timer.ElapsedMilliseconds < 250 && _outputActive)
        {
            Thread.Sleep(1);
        }
    }

    public void Dispose()
    {
        DeckA.Dispose();
        DeckB.Dispose();
    }
}
