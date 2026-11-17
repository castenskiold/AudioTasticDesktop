using AudioTastic.Sdk.Audio;
using MeltySynth;

namespace AudioTastic.Input.Midi;

/// <summary>
/// FM-08: renders a standard MIDI file through a SoundFont. Loops are played once; the length includes a short
/// release tail so the last notes fade naturally.
/// Seeking restarts the sequence and fast-forwards to the target with new notes muted (controllers and program
/// changes still apply), so it is quick; notes already sounding at the target start with their next note-on.
/// </summary>
internal sealed class MidiSource : IAudioSource
{
    public const int SampleRate = 44_100;
    private static readonly TimeSpan Tail = TimeSpan.FromSeconds(1.5);

    private readonly MidiFile _midi;
    private readonly Synthesizer _synth;
    private readonly MidiFileSequencer _sequencer;
    private float[] _left = new float[4096];
    private float[] _right = new float[4096];
    private bool _fastForward;

    public MidiSource(string path, SoundFont soundFont)
    {
        try
        {
            _midi = new MidiFile(path, MidiFileLoopType.None);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or NotSupportedException or EndOfStreamException)
        {
            throw new AudioSourceException($"Not a valid MIDI file: {ex.Message}", ex);
        }
        _synth = new Synthesizer(soundFont, new SynthesizerSettings(SampleRate));
        _sequencer = new MidiFileSequencer(_synth) { OnSendMessage = OnMessage };
        _sequencer.Play(_midi, loop: false);
        LengthFrames = (long)Math.Ceiling((_midi.Length + Tail).TotalSeconds * SampleRate);
        Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    public AudioStreamInfo Info { get; } = new("midi", SampleRate, 2, 0, 0, IsLossless: false);
    public AudioFormat Format => Info.OutputFormat;
    public long? LengthFrames { get; }
    public long PositionFrames { get; private set; }
    public bool CanSeek => true;
    public IReadOnlyDictionary<string, string> Tags { get; }

    public int Read(Span<float> buffer)
    {
        if (buffer.Length % 2 != 0)
        {
            throw new ArgumentException("Buffer length must be a multiple of the channel count (2).", nameof(buffer));
        }
        var frames = (int)Math.Min(buffer.Length / 2, LengthFrames!.Value - PositionFrames);
        if (frames <= 0)
        {
            return 0;
        }
        if (_left.Length < frames)
        {
            _left = new float[frames];
            _right = new float[frames];
        }
        _sequencer.Render(_left.AsSpan(0, frames), _right.AsSpan(0, frames));
        for (var i = 0; i < frames; i++)
        {
            buffer[2 * i] = _left[i];
            buffer[(2 * i) + 1] = _right[i];
        }
        PositionFrames += frames;
        return frames;
    }

    public void Seek(long frame)
    {
        var target = Math.Clamp(frame, 0, LengthFrames!.Value);
        _sequencer.Play(_midi, loop: false); // restarts the sequence and resets the synthesizer
        var targetTime = TimeSpan.FromSeconds((double)target / SampleRate);
        var block = _synth.BlockSize;
        var blockTime = (double)block / SampleRate;
        Span<float> left = stackalloc float[block];
        Span<float> right = stackalloc float[block];
        _fastForward = true;
        try
        {
            // Each block advances the sequence by Speed × block length: aim straight at the target.
            while (_sequencer.Position < targetTime && !_sequencer.EndOfSequence)
            {
                var remaining = (targetTime - _sequencer.Position).TotalSeconds;
                _sequencer.Speed = (float)Math.Clamp(remaining / blockTime, 1, 100_000);
                _sequencer.Render(left, right);
            }
        }
        finally
        {
            _sequencer.Speed = 1;
            _fastForward = false;
        }
        PositionFrames = target;
    }

    public void Dispose()
    {
    }

    private void OnMessage(Synthesizer synthesizer, int channel, int command, int data1, int data2)
    {
        if (_fastForward && command == 0x90 && data2 > 0)
        {
            return; // no new notes while fast-forwarding to a seek target
        }
        synthesizer.ProcessMidiMessage(channel, command, data1, data2);
    }
}
