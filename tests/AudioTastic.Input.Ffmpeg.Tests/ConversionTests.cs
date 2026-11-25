using System.Runtime.InteropServices;
using System.Security.Cryptography;
using AudioTastic.Data;
using AudioTastic.Data.Cache;
using AudioTastic.Data.Settings;
using AudioTastic.Metadata;
using AudioTastic.Metadata.Conversion;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Metadata;
using AudioTastic.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Input.Ffmpeg.Tests;

/// <summary>
/// TL-01 and the P13 exit criterion "converted files keep tags and art and play correctly": every format through
/// the FFmpeg encoders, lossless ones bit-exact, lossy ones with the original length; names, parts, cancelling.
/// </summary>
public sealed class ConversionTests : IDisposable
{
    private static readonly byte[] Png =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01,
        0x00, 0x00, 0x00, 0x01, 0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00, 0x0D, 0x49, 0x44, 0x41,
        0x54, 0x78, 0x9C, 0x63, 0xF8, 0xFF, 0xFF, 0x3F, 0x00, 0x05, 0xFE, 0x02, 0xFE, 0xA7, 0x35, 0x81, 0x84, 0x00, 0x00, 0x00,
        0x00, 0x49, 0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82,
    ];

    private static readonly string[] BatchFiles = ["sine1k.flac.flac", "sweep.flac.flac", "clicks.flac.flac"];

    private readonly string _dir = Directory.CreateTempSubdirectory("audiotastic-convert-").FullName;
    private readonly CacheStore _cache;
    private readonly TagService _tags;
    private readonly ConversionService _service;
    private readonly FfmpegEncoderFactory _encoders = new();

    public ConversionTests()
    {
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, _dir, _dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        _cache = new CacheStore(paths);
        _tags = new TagService(_cache, [new FfmpegSourceFactory()], NullLogger<TagService>.Instance);
        _service = new ConversionService([new FfmpegSourceFactory()], [_encoders], _tags, new ArtService(NullLogger<ArtService>.Instance),
            NullLogger<ConversionService>.Instance);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    private string Tagged(string name)
    {
        var copy = Path.Combine(_dir, "in", name);
        Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
        File.Copy(TestMedia.Require(name), copy, overwrite: true);
        new TagWriter(_tags, null, NullLogger<TagWriter>.Instance).Write(copy, new TagEdit(new Dictionary<TagField, string?>
        {
            [TagField.Title] = "Første sang",
            [TagField.Artist] = "Ærø Band",
            [TagField.Album] = "Prøve: Live?",
            [TagField.Track] = "3",
            [TagField.Year] = "2026",
            [TagField.Genre] = "Psytrance",
        }, new CoverChange(new TagPicture(Png, "image/png"))));
        return copy;
    }

    private ConversionOptions Options(EncodeFormat format, string pattern = ConversionNaming.DefaultPattern, ExistingFileAction ifExists = ExistingFileAction.Skip) =>
        new(new EncodeSettings(format, 0, format == EncodeFormat.Opus ? 160 : 256), Path.Combine(_dir, "out"), pattern, ifExists);

    private static float[] Decode(string path, out AudioFormat format, out int bits)
    {
        using var source = new FfmpegSourceFactory().Open(path);
        format = source.Format;
        bits = source.Info.BitsPerSample;
        var samples = new List<float>();
        var buffer = new float[4096 * format.Channels];
        int frames;
        while ((frames = source.Read(buffer)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, frames * format.Channels));
        }
        return [.. samples];
    }

    public static TheoryData<EncodeFormat, string> Formats => new()
    {
        { EncodeFormat.Flac, ".flac" }, { EncodeFormat.Alac, ".m4a" }, { EncodeFormat.Wav, ".wav" },
        { EncodeFormat.Mp3, ".mp3" }, { EncodeFormat.Aac, ".m4a" }, { EncodeFormat.Opus, ".opus" }, { EncodeFormat.Vorbis, ".ogg" },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void Every_format_keeps_the_tags_the_cover_and_the_length(EncodeFormat format, string extension)
    {
        Assert.SkipUnless(_encoders.Formats.Contains(format), $"This FFmpeg build has no {format} encoder.");
        var original = Tagged("sweep.flac.flac");
        var job = _service.Convert(new ConversionItem(original), Options(format), TestContext.Current.CancellationToken);
        Assert.True(job.State == ConversionState.Done, job.Message);
        Assert.Equal(Path.Combine(_dir, "out", "Ærø Band", "Prøve_ Live_", "03 - Første sang" + extension), job.Output);

        var tags = _tags.Read(job.Output!)!;
        Assert.Equal("Første sang", tags.Title);
        Assert.Equal("Ærø Band", tags.Artist);
        Assert.Equal("Prøve: Live?", tags.Album);
        Assert.Equal(3, tags.TrackNumber);
        Assert.Equal(2026, tags.Year);
        Assert.Equal("Psytrance", tags.Genre);
        Assert.NotNull(new ArtService(NullLogger<ArtService>.Instance).Find(job.Output!)?.Picture);

        var source = Decode(original, out var sourceFormat, out _);
        var converted = Decode(job.Output!, out var convertedFormat, out _);
        Assert.Equal(sourceFormat.Channels, convertedFormat.Channels);
        if (format is EncodeFormat.Flac or EncodeFormat.Alac or EncodeFormat.Wav)
        {
            Assert.Equal(sourceFormat, convertedFormat);
            Assert.True(source.AsSpan().SequenceEqual(converted), "lossless conversion is bit-exact");
        }
        else
        {
            var seconds = (double)source.Length / sourceFormat.Channels / sourceFormat.SampleRate;
            var convertedSeconds = (double)converted.Length / convertedFormat.Channels / convertedFormat.SampleRate;
            Assert.InRange(convertedSeconds - seconds, -0.002, 0.002); // gapless: no added silence
        }
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.converting*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Lossless_24_bit_sources_stay_24_bit()
    {
        var job = _service.Convert(new ConversionItem(TestMedia.Require("sine1k.wav24.wav")), Options(EncodeFormat.Flac, "%filename%"), TestContext.Current.CancellationToken);
        Assert.Equal(ConversionState.Done, job.State);
        var source = Decode(TestMedia.Require("sine1k.wav24.wav"), out _, out _);
        var converted = Decode(job.Output!, out _, out var bits);
        Assert.Equal(24, bits);
        Assert.True(source.AsSpan().SequenceEqual(converted));
    }

    [Fact]
    public void Formats_the_encoder_cannot_take_are_resampled_and_downmixed()
    {
        Assert.SkipUnless(_encoders.Formats.Contains(EncodeFormat.Mp3), "No MP3 encoder.");
        var hires = _service.Convert(new ConversionItem(TestMedia.Require("sine1k.hires-24-96.flac")), Options(EncodeFormat.Mp3, "%filename%"), TestContext.Current.CancellationToken);
        Assert.True(hires.State == ConversionState.Done, hires.Message);
        Decode(hires.Output!, out var format, out _);
        Assert.Equal(48_000, format.SampleRate);

        var surround = _service.Convert(new ConversionItem(TestMedia.Require("surround51.flac")), Options(EncodeFormat.Mp3, "%filename%"), TestContext.Current.CancellationToken);
        Assert.True(surround.State == ConversionState.Done, surround.Message);
        Decode(surround.Output!, out format, out _);
        Assert.Equal(2, format.Channels);

        Assert.Equal(new AudioFormat(48_000, 2), _encoders.InputFormatFor(new EncodeSettings(EncodeFormat.Opus), new AudioFormat(44_100, 2)));
        Assert.Equal(new AudioFormat(96_000, 6), _encoders.InputFormatFor(new EncodeSettings(EncodeFormat.Flac), new AudioFormat(96_000, 6)));
    }

    [Fact]
    public void A_part_of_a_file_is_cut_exactly_and_named_by_its_own_tags()
    {
        var original = Tagged("sweep.flac.flac");
        var item = new ConversionItem(original, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), new TrackTags(Title: "Part two", TrackNumber: 2));
        var job = _service.Convert(item, Options(EncodeFormat.Flac), TestContext.Current.CancellationToken);
        Assert.True(job.State == ConversionState.Done, job.Message);
        Assert.EndsWith("02 - Part two.flac", job.Output, StringComparison.Ordinal);
        var converted = Decode(job.Output!, out var format, out _);
        Assert.Equal(format.SampleRate * format.Channels, converted.Length);
        var whole = Decode(original, out _, out _);
        Assert.True(whole.AsSpan(format.SampleRate * format.Channels, converted.Length).SequenceEqual(converted));
        var tags = _tags.Read(job.Output!)!;
        Assert.Equal("Part two", tags.Title);
        Assert.Equal("Ærø Band", tags.Artist); // the rest from the file
        Assert.Equal(2, tags.TrackNumber);
    }

    [Fact]
    public void Existing_files_are_skipped_numbered_or_replaced_and_the_original_is_never_overwritten()
    {
        var original = Tagged("sine1k.flac.flac");
        var first = _service.Convert(new ConversionItem(original), Options(EncodeFormat.Wav, "%filename%"), TestContext.Current.CancellationToken);
        Assert.Equal(ConversionState.Done, first.State);
        var skipped = _service.Convert(new ConversionItem(original), Options(EncodeFormat.Wav, "%filename%"), TestContext.Current.CancellationToken);
        Assert.Equal(ConversionState.Skipped, skipped.State);
        var numbered = _service.Convert(new ConversionItem(original), Options(EncodeFormat.Wav, "%filename%", ExistingFileAction.Number), TestContext.Current.CancellationToken);
        Assert.Equal(ConversionState.Done, numbered.State);
        Assert.EndsWith("sine1k.flac (2).wav", numbered.Output, StringComparison.Ordinal);
        var replaced = _service.Convert(new ConversionItem(original), Options(EncodeFormat.Wav, "%filename%", ExistingFileAction.Overwrite), TestContext.Current.CancellationToken);
        Assert.Equal(ConversionState.Done, replaced.State);
        Assert.Equal(first.Output, replaced.Output);

        // Same folder, same name, same format: the original would be replaced.
        var before = File.ReadAllBytes(original);
        var self = new ConversionOptions(new EncodeSettings(EncodeFormat.Flac), null, "sine1k.flac", ExistingFileAction.Overwrite);
        var refused = _service.Convert(new ConversionItem(original), self, TestContext.Current.CancellationToken);
        Assert.Equal(ConversionState.Failed, refused.State);
        Assert.Equal(before, File.ReadAllBytes(original));
    }

    [Fact]
    public async Task A_batch_runs_in_parallel_and_cancelling_removes_partial_files()
    {
        var items = BatchFiles.Select(n => new ConversionItem(TestMedia.Require(n)));
        using (var batch = _service.Start(items, Options(EncodeFormat.Flac, "%filename%") with { ParallelJobs = 3 }))
        {
            await batch.Completion.WaitAsync(TimeSpan.FromSeconds(60), TestContext.Current.CancellationToken);
            Assert.All(batch.Jobs, j => Assert.Equal(ConversionState.Done, j.State));
        }

        var changes = 0;
        using var slow = _service.Start([new ConversionItem(TestMedia.Require("long5min.flac"))], Options(EncodeFormat.Flac, "long") with { ParallelJobs = 1 });
        slow.JobChanged += (_, _) => Interlocked.Increment(ref changes);
        while (slow.Jobs[0].Progress == 0 && !slow.Jobs[0].IsFinished)
        {
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        slow.Cancel();
        await slow.Completion.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        Assert.Equal(ConversionState.Cancelled, slow.Jobs[0].State);
        Assert.False(File.Exists(Path.Combine(_dir, "out", "long.flac")));
        Assert.Empty(Directory.EnumerateFiles(_dir, "*.converting*", SearchOption.AllDirectories));
    }

    [Fact]
    public void Names_come_from_the_pattern_with_unsafe_characters_replaced()
    {
        var tags = new TrackTags(Title: "A/B: C?", Artist: "Art*ist", Album: "Al<bum>", TrackNumber: 7, Year: 1999);
        Assert.Equal(Path.Combine("Art_ist", "Al_bum_", "07 - A_B_ C_"), ConversionNaming.Render(ConversionNaming.DefaultPattern, tags, "x.flac"));
        Assert.Equal(Path.Combine("Unknown artist", "Unknown album", "00 - song"), ConversionNaming.Render(ConversionNaming.DefaultPattern, null, @"c:\m\song.flac"));
        Assert.Equal("1999 - song", ConversionNaming.Render("%year% - %filename%", tags, "song.flac"));
        Assert.Equal("song", ConversionNaming.Render("  ", tags, "song.flac"));
        Assert.Equal("_", ConversionNaming.Clean(".."));
    }

    [Fact]
    public void Options_come_from_the_saved_dialog_choices()
    {
        var options = ConversionOptions.From(new ConversionSettings { Format = "Mp3", Mp3Vbr = true, Mp3VbrQuality = 0, SameFolder = true });
        Assert.Equal(EncodeFormat.Mp3, options.Encode.Format);
        Assert.True(options.Encode.Vbr);
        Assert.Equal(0, options.Encode.VbrQuality);
        Assert.Null(options.Destination);
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), ConversionOptions.From(new ConversionSettings()).Destination);
    }
}
