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
                DeckA.MixInto(chunk, _scratch);
                DeckB.MixInto(chunk, _scratch);
            }
            Master.Process(output, Format.Channels);
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
