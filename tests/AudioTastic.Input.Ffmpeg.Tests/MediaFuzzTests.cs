using AudioTastic.Metadata;
using AudioTastic.Sdk.Audio;
using AudioTastic.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>
/// P14 stability: damaged audio files (mostly in their headers and tags, where the parsers are) through the tag reader,
/// the file info reader and the FFmpeg decoder. The tag and info readers must never throw; the decoder may refuse a
/// file (AudioSourceException: the player skips it), but must not fail otherwise or hang.
/// </summary>
public sealed class MediaFuzzTests : IDisposable
{
    /// <summary>Damaged copies per format; AUDIOTASTIC_FUZZ_COUNT raises it for a deeper local run.</summary>
    private static readonly int Count = int.TryParse(Environment.GetEnvironmentVariable("AUDIOTASTIC_FUZZ_COUNT"), out var n) && n > 0 ? n / 5 : 60;

    private readonly string _dir = Directory.CreateTempSubdirectory("audiotastic-mediafuzz-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    public static TheoryData<string> Files =>
    [
        "sine1k.flac.flac", "sine1k.mp3.mp3", "sine1k.aac.m4a", "sine1k.alac.m4a", "sine1k.opus.opus", "sine1k.vorbis.ogg",
        "sine1k.wav16.wav", "sine1k.wavpack.wv", "sine1k.aiff.aiff", "sine1k.wma.wma", "sine1k.dsd64.dsf",
    ];

    [Theory]
    [MemberData(nameof(Files))]
    public void Damaged_files_are_read_or_refused_never_crash(string name)
    {
        var original = File.ReadAllBytes(TestMedia.Require(name));
        var head = original.AsSpan(0, Math.Min(4096, original.Length)).ToArray();
        var tail = original.AsSpan(head.Length).ToArray();
        var target = Path.Combine(_dir, "fuzz" + Path.GetExtension(name));
        var tags = new TagService(null, [new FfmpegSourceFactory()], NullLogger<TagService>.Instance);
        var info = new FileInfoReader([new FfmpegSourceFactory()], NullLogger<FileInfoReader>.Instance);
        var factory = new FfmpegSourceFactory();
        var buffer = new float[8192];

        void Check(byte[] data)
        {
            File.WriteAllBytes(target, data);
            _ = tags.Read(target); // never throws
            _ = info.Read(target); // never throws
            using var source = factory.Open(target);
            var frames = buffer.Length / Math.Max(1, source.Format.Channels);
            if (source.CanSeek && source.LengthFrames is > 0 and var length)
            {
                source.Seek(length / 2);
            }
            var total = 0L;
            int read;
            while ((read = source.Read(buffer.AsSpan(0, frames * source.Format.Channels))) > 0 && total < 50_000_000)
            {
                total += read;
            }
        }

        // Damage in the first 4 KB (headers, tags), and anywhere in the file.
        var headDamage = Fuzz.Mutations(head, Count, name.Sum(c => c)).Select(h => (byte[])[.. h, .. tail]);
        var anyDamage = Fuzz.Mutations(original, Count / 2, name.Length);
        var problems = Fuzz.Run(headDamage.Concat(anyDamage), Check, [typeof(AudioSourceException)], TimeSpan.FromSeconds(15));
        Assert.True(problems.Count == 0, $"{name}:\n" + string.Join("\n", problems.Take(5)));
    }
}
