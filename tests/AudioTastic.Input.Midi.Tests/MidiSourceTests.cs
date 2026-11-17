using System.Diagnostics;
using AudioTastic.Sdk.Audio;
using AudioTastic.TestSupport;
using MeltySynth;

namespace AudioTastic.Input.Midi.Tests;

/// <summary>FM-08: MIDI rendering through the bundled SoundFont: format, length, audio, seeking and errors.</summary>
public sealed class MidiSourceTests
{
    private static readonly Lazy<SoundFont?> BundledSoundFont = new(() =>
    {
        var path = Path.Combine(TestMedia.RepoRoot, "native", "downloads", "soundfont", MidiInputPlugin.BundledFileName);
        return File.Exists(path) ? new SoundFont(path) : null;
    });

    private readonly SoundFont _soundFont;

    public MidiSourceTests()
    {
        Assert.SkipUnless(BundledSoundFont.Value is not null, "SoundFont missing. Run: python native/fetch_soundfont.py");
        _soundFont = BundledSoundFont.Value!;
    }

    [Fact]
    public void Renders_stereo_audio_for_the_whole_length()
    {
        using var source = Open();

        Assert.Equal(new AudioFormat(MidiSource.SampleRate, 2), source.Format);
        Assert.Equal("midi", source.Info.Codec);
        Assert.True(source.CanSeek);
        var samples = Signals.ReadAll(source);

        Assert.Equal(source.LengthFrames, samples.Length / 2);
        Assert.True(samples.Length / 2 > MidiSource.SampleRate, "The arpeggio should last more than a second.");
        Assert.True(samples.Max(Math.Abs) > 0.01f, "The synthesizer produced silence.");
        Assert.All(samples, s => Assert.InRange(s, -1.5f, 1.5f));
    }

    [Fact]
    public void Seek_moves_to_the_target_quickly_and_keeps_playing()
    {
        using var source = Open();
        var length = source.LengthFrames!.Value;
        var target = length / 2;

        var watch = Stopwatch.StartNew();
        source.Seek(target);
        watch.Stop();

        Assert.Equal(target, source.PositionFrames);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"Seek took {watch.ElapsedMilliseconds} ms.");
        var rest = Signals.ReadAll(source);
        Assert.Equal(length - target, rest.Length / 2);
        Assert.True(rest.Max(Math.Abs) > 0.01f, "Nothing played after the seek.");

        source.Seek(0);
        Assert.Equal(0, source.PositionFrames);
        Assert.Equal(length, Signals.ReadAll(source).Length / 2);
    }

    [Fact]
    public void Seek_past_the_end_reads_nothing()
    {
        using var source = Open();
        source.Seek(source.LengthFrames!.Value + 1000);
        Assert.Equal(0, source.Read(new float[256]));
    }

    [Fact]
    public void Invalid_files_throw_AudioSourceException()
    {
        var notMidi = Path.Combine(TestMedia.RepoRoot, "AudioTastic.slnx");
        Assert.Throws<AudioSourceException>(() => new MidiSource(notMidi, _soundFont));
    }

    [Fact]
    public void Factory_opens_midi_extensions_and_loads_the_soundfont_once()
    {
        var loads = 0;
        var factory = new MidiSourceFactory(() =>
        {
            loads++;
            return _soundFont;
        });

        Assert.True(factory.CanOpen("song.MID"));
        Assert.True(factory.CanOpen("song.midi"));
        Assert.False(factory.CanOpen("song.mp3"));
        using (factory.Open(TestMedia.Require("arpeggio.mid")))
        using (factory.Open(TestMedia.Require("arpeggio.mid")))
        {
        }
        Assert.Equal(1, loads);
        factory.Reset();
        using (factory.Open(TestMedia.Require("arpeggio.mid")))
        {
        }
        Assert.Equal(2, loads);
    }

    private MidiSource Open() => new(TestMedia.Require("arpeggio.mid"), _soundFont);
}
