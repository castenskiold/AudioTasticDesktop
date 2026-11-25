using System.Text;
using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Core.Playlists.Formats;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Playlists;
using AudioTastic.TestSupport;

namespace AudioTastic.Core.Tests.Playlists;

/// <summary>
/// P14 stability: the playlist, CUE and EQF parsers fed thousands of mutated files. They may refuse a file (the
/// exceptions the app catches when it imports one), but must never fail in any other way or hang.
/// </summary>
public sealed class ParserFuzzTests : IDisposable
{
    /// <summary>Mutations per parser; AUDIOTASTIC_FUZZ_COUNT raises it for a deeper local run.</summary>
    private static readonly int Count = int.TryParse(Environment.GetEnvironmentVariable("AUDIOTASTIC_FUZZ_COUNT"), out var n) && n > 0 ? n : 400;
    private static readonly Type[] PlaylistRefusals = [typeof(IOException), typeof(System.Xml.XmlException), typeof(NotSupportedException), typeof(UnauthorizedAccessException)];
    private readonly string _dir = Directory.CreateTempSubdirectory("audiotastic-fuzz-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static IReadOnlyList<PlaylistItem> Sample() =>
    [
        new(Path.Combine("music", "Ærø – første.flac"), "Første sang", "Ærø Band", TimeSpan.FromSeconds(245)),
        new("http://radio.example.com/stream?id=1&x=2", "Radio", null, null),
        new(Path.Combine("cd", "album.wav"), "Track 2", "Artist", TimeSpan.FromSeconds(180), TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(240), 2, "Album"),
    ];

    public static TheoryData<string> Formats => [.. PlaylistFormats.BuiltIn.Select(f => f.Name)];

    [Theory]
    [MemberData(nameof(Formats))]
    public void Mutated_playlists_are_read_or_refused_never_crash(string formatName)
    {
        var format = PlaylistFormats.BuiltIn.Single(f => f.Name == formatName);
        var extension = format.Extensions.First();
        var seedFile = Path.Combine(_dir, "seed" + extension);
        byte[] seed;
        if (format.CanWrite)
        {
            format.Write(seedFile, Sample(), relativePaths: true);
            seed = File.ReadAllBytes(seedFile);
        }
        else
        {
            seed = Encoding.UTF8.GetBytes(CueSeed);
        }
        var target = Path.Combine(_dir, "fuzz" + extension);
        var problems = Fuzz.Run(Fuzz.Mutations(seed, Count, formatName.Sum(ch => ch)) /* stable seed: string hashes differ per process */, data =>
        {
            File.WriteAllBytes(target, data);
            _ = format.Read(target);
        }, PlaylistRefusals);
        Assert.True(problems.Count == 0, $"{format.Name}:\n" + string.Join("\n", problems.Take(5)));
    }

    private const string CueSeed = """
        REM GENRE "Psytrance"
        REM DATE 1995
        PERFORMER "Hallucinogen"
        TITLE "Twisted"
        FILE "twisted.flac" WAVE
          TRACK 01 AUDIO
            TITLE "LSD"
            INDEX 01 00:00:00
          TRACK 02 AUDIO
            TITLE "Dark Magus"
            PERFORMER "Hallucinogen"
            INDEX 00 08:12:40
            INDEX 01 08:14:00
        FILE "second.wav" WAVE
          TRACK 03 AUDIO
            INDEX 01 00:00:00
        """;

    [Fact]
    public void Mutated_cue_sheets_never_crash_the_parser()
    {
        var problems = Fuzz.Run(Fuzz.Mutations(Encoding.UTF8.GetBytes(CueSeed), Count * 3, 7),
            data => _ = CueParser.Parse(PlaylistText.Decode(data)), [typeof(FormatException)]);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(5)));
    }

    [Fact]
    public void Mutated_winamp_eqf_files_are_read_or_refused()
    {
        var seed = EqfFile.Write([new EqPreset("Rock", new EqualizerSettings { PreampDb = 3, Bands = [5, 4, 3, 0, -2, -2, 0, 3, 4, 5] }),
            new EqPreset("Flat", new EqualizerSettings())]);
        var problems = Fuzz.Run(Fuzz.Mutations(seed, Count * 2, 11), data => _ = EqfFile.Parse(data), [typeof(InvalidDataException), typeof(FormatException)]);
        Assert.True(problems.Count == 0, string.Join("\n", problems.Take(5)));
    }
}
