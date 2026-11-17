using System.Text;
using AudioTastic.Sdk.Audio;
using AudioTastic.TestSupport;

namespace AudioTastic.Input.OpenMpt.Tests;

/// <summary>FM-07: tracker modules through libopenmpt, with a 4-channel ProTracker MOD written by the test.</summary>
public sealed class OpenMptSourceTests : IDisposable
{
    private readonly string _folder = Directory.CreateTempSubdirectory("audiotastic-mod-").FullName;

    public OpenMptSourceTests()
    {
        Assert.SkipUnless(OpenMptNative.IsAvailable, "libopenmpt missing. Run: python native/fetch_libopenmpt.py");
    }

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void Opens_a_mod_with_type_channels_title_and_length()
    {
        using var source = OpenMptSource.Open(WriteMod());

        Assert.Equal("MOD 4ch", source.Info.Codec);
        Assert.Equal(new AudioFormat(OpenMptSource.SampleRate, 2), source.Format);
        Assert.Equal("AudioTastic Test", source.Tags["title"]);
        // 64 rows at speed 6 and 125 BPM: 64 × 6 × 20 ms = 7.68 s, played once.
        Assert.InRange(source.LengthFrames!.Value, (long)(7.6 * OpenMptSource.SampleRate), (long)(7.76 * OpenMptSource.SampleRate));
    }

    [Fact]
    public void Plays_once_with_audible_output_then_ends()
    {
        using var source = OpenMptSource.Open(WriteMod());

        var samples = Signals.ReadAll(source);

        var frames = samples.Length / 2;
        // libopenmpt renders a short release tail after the last row; the length is then exact.
        Assert.InRange(frames, (long)(7.6 * OpenMptSource.SampleRate), (long)(8.0 * OpenMptSource.SampleRate));
        Assert.Equal(frames, source.LengthFrames);
        Assert.True(samples.Max(Math.Abs) > 0.05f, "libopenmpt produced silence.");
        Assert.Equal(0, source.Read(new float[256]));
    }

    [Fact]
    public void Seek_moves_to_the_target_and_plays_the_rest()
    {
        using var source = OpenMptSource.Open(WriteMod());
        var length = source.LengthFrames!.Value;
        var target = length / 2;

        source.Seek(target);

        Assert.Equal(target, source.PositionFrames);
        var rest = Signals.ReadAll(source).Length / 2;
        Assert.InRange(rest, length - target - 2000, length - target + (OpenMptSource.SampleRate / 5));
        source.Seek(0);
        Assert.Equal(0, source.PositionFrames);
    }

    [Fact]
    public void Invalid_files_throw_AudioSourceException()
    {
        var path = Path.Combine(_folder, "not-a-module.xm");
        File.WriteAllText(path, "This is not a tracker module.");
        Assert.Throws<AudioSourceException>(() => OpenMptSource.Open(path));
        Assert.Throws<AudioSourceException>(() => OpenMptSource.Open(Path.Combine(_folder, "missing.mod")));
    }

    [Fact]
    public void Factory_handles_module_extensions()
    {
        var factory = new OpenMptSourceFactory();
        foreach (var extension in new[] { ".mod", ".XM", ".it", ".s3m" })
        {
            Assert.True(factory.CanOpen("song" + extension), extension);
        }
        Assert.False(factory.CanOpen("song.mp3"));
    }

    /// <summary>
    /// A ProTracker "M.K." module: one pattern of 64 rows, an arpeggio C-E-G-C on channel 1 using a looped square wave,
    /// default speed 6 / 125 BPM.
    /// </summary>
    private string WriteMod()
    {
        using var data = new MemoryStream();
        void Bytes(byte[] b) => data.Write(b);
        void Text(string s, int length) => Bytes(Encoding.ASCII.GetBytes(s.PadRight(length, '\0')[..length]));
        void Word(int value) => Bytes([(byte)(value >> 8), (byte)value]);

        const int sampleWords = 512;
        Text("AudioTastic Test", 20);
        for (var i = 0; i < 31; i++)
        {
            Text(i == 0 ? "square" : "", 22);
            Word(i == 0 ? sampleWords : 0); // length in words
            Bytes([0, (byte)(i == 0 ? 64 : 0)]); // finetune, volume
            Word(0); // loop start
            Word(i == 0 ? sampleWords : 1); // loop length (1 = no loop)
        }
        Bytes([1, 127]); // song length, restart
        Bytes(new byte[128]); // order table: pattern 0
        Text("M.K.", 4);

        var pattern = new byte[64 * 4 * 4];
        int[] periods = [214, 170, 143, 107]; // C-3, E-3, G-3, C-4
        for (var n = 0; n < periods.Length; n++)
        {
            var cell = n * 16 * 4 * 4; // rows 0, 16, 32, 48, channel 0
            pattern[cell] = (byte)(periods[n] >> 8); // sample 1 high nibble = 0
            pattern[cell + 1] = (byte)periods[n];
            pattern[cell + 2] = 1 << 4; // sample 1, no effect
        }
        Bytes(pattern);

        var sample = new byte[sampleWords * 2];
        for (var i = 0; i < sample.Length; i++)
        {
            sample[i] = (byte)(i % 32 < 16 ? 100 : unchecked((byte)-100));
        }
        Bytes(sample);

        var path = Path.Combine(_folder, "test.mod");
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }
}
