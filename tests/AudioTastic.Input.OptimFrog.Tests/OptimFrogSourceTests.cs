using AudioTastic.Sdk.Audio;
using AudioTastic.TestSupport;

namespace AudioTastic.Input.OptimFrog.Tests;

/// <summary>FM-06: OptimFROG decoding is lossless (bit-identical to the source WAV), seekable and reads tags.</summary>
public sealed class OptimFrogSourceTests
{
    public OptimFrogSourceTests()
    {
        Assert.SkipUnless(OptimFrogNative.IsAvailable, "OptimFROG library missing (x64 only). Run: python native/fetch_optimfrog.py");
    }

    [Theory]
    [InlineData("sine1k.ofr16.ofr", "sine1k.wav16.wav", 16)]
    [InlineData("sweep.ofr16.ofr", "sweep.wav16.wav", 16)]
    [InlineData("sine1k.ofr24.ofr", "sine1k.wav24.wav", 24)]
    public void Decodes_bit_identically_to_the_wav(string file, string wav, int bits)
    {
        var reference = ReferenceWav.Read(TestMedia.Require(wav)).Samples;
        using var source = OptimFrogSource.Open(TestMedia.Require(file));

        Assert.Equal("optimfrog", source.Info.Codec);
        Assert.True(source.Info.IsLossless);
        Assert.Equal(bits, source.Info.BitsPerSample);
        Assert.Equal(new AudioFormat(44_100, 2), source.Format);
        Assert.Equal(reference.Length / 2, source.LengthFrames);
        Assert.Equal(Signals.Sha256(reference), Signals.Sha256(Signals.ReadAll(source)));
    }

    [Fact]
    public void Seek_lands_on_the_exact_target_frame()
    {
        var path = TestMedia.Require("sweep.ofr16.ofr");
        float[] sequential;
        using (var whole = OptimFrogSource.Open(path))
        {
            sequential = Signals.ReadAll(whole);
        }
        using var source = OptimFrogSource.Open(path);
        Assert.True(source.CanSeek);
        foreach (var target in new long[] { 220_500, 1, 1234, 44_100, 100_003, 0, 7, (sequential.Length / 2) - 4096 })
        {
            source.Seek(target);
            Assert.Equal(target, source.PositionFrames);
            var got = Signals.ReadFrames(source, 2048);
            Assert.True(sequential.AsSpan((int)target * 2, got.Length).SequenceEqual(got), $"Seek to {target} is not sample-exact.");
        }
    }

    [Fact]
    public void Unicode_paths_open()
    {
        var folder = Directory.CreateTempSubdirectory("audiotastic-ofr-");
        try
        {
            var path = Path.Combine(folder.FullName, "Sigur Rós – Æ.ofr");
            File.Copy(TestMedia.Require("sine1k.ofr16.ofr"), path);
            using var source = OptimFrogSource.Open(path);
            Assert.Equal(5 * 44_100, source.LengthFrames);
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    [Fact]
    public void Invalid_files_throw_AudioSourceException()
    {
        Assert.Throws<AudioSourceException>(() => OptimFrogSource.Open(Path.Combine(TestMedia.RepoRoot, "AudioTastic.slnx")));
        Assert.Throws<AudioSourceException>(() => OptimFrogSource.Open(Path.Combine(TestMedia.RepoRoot, "missing.ofr")));
    }

    [Theory]
    [InlineData("SINT16", 16, OptimFrogSource.SampleKind.Signed, 2)]
    [InlineData("UINT8", 8, OptimFrogSource.SampleKind.Unsigned, 1)]
    [InlineData("SINT24", 24, OptimFrogSource.SampleKind.Signed, 3)]
    [InlineData("FLOAT32", 32, OptimFrogSource.SampleKind.Float, 4)]
    internal void Sample_types_are_understood(string type, int bits, OptimFrogSource.SampleKind kind, int bytes) =>
        Assert.Equal((kind, bytes), OptimFrogSource.ParseSampleType(type, bits));
}
