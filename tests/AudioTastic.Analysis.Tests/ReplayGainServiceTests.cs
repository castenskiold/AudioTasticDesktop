using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using AudioTastic.Data;
using AudioTastic.Data.Cache;
using AudioTastic.Data.Settings;
using AudioTastic.Input.Ffmpeg;
using AudioTastic.Metadata;
using AudioTastic.Sdk.Metadata;
using AudioTastic.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Analysis.Tests;

/// <summary>PB-03: gain decisions from tags and from analysis, modes, pre-amps, clipping prevention, tag writing.</summary>
public sealed class ReplayGainServiceTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("audiotastic-rg-").FullName;
    private readonly SettingsStore _settings;
    private readonly CacheStore _cache;
    private readonly TagService _tags;
    private readonly AnalysisService _analysis;
    private readonly ReplayGainService _service;

    public ReplayGainServiceTests()
    {
        Assert.SkipUnless(FfmpegLibrary.IsAvailable, "FFmpeg libraries missing. Run: python native/fetch_ffmpeg.py --rid <rid>");
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, _dir, _dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        _settings = new SettingsStore(paths);
        _cache = new CacheStore(paths);
        var factories = new[] { new FfmpegSourceFactory() };
        _tags = new TagService(_cache, factories, NullLogger<TagService>.Instance);
        _analysis = new AnalysisService(_cache, factories, _settings, NullLogger<AnalysisService>.Instance);
        _service = new ReplayGainService(_settings, _tags, _analysis, NullLogger<ReplayGainService>.Instance);
    }

    public void Dispose()
    {
        _service.Dispose();
        _analysis.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Off_by_default()
    {
        var entry = Entry(Copy("sine1k.flac.flac", "a.flac"));
        Assert.Null(_service.GetGain(new Playlist("t"), entry));
    }

    [Fact]
    public void Tag_gains_with_mode_preamp_and_clipping_prevention()
    {
        var path = Copy("sine1k.flac.flac", "tagged.flac");
        WriteRg(path, track: -7.5, trackPeak: 0.5, album: -9.0, albumPeak: 0.6);
        var entry = Entry(path);
        var playlist = new Playlist("t");
        playlist.Add([entry]);

        SetMode(ReplayGainMode.Track, preampTagged: 1);
        var d = _service.GetGain(playlist, entry)!;
        Assert.Equal(ReplayGainSource.TrackTag, d.Source);
        Assert.Equal(-6.5, d.GainDb, 6);
        Assert.Equal(Math.Pow(10, -6.5 / 20), d.Linear, 4);

        SetMode(ReplayGainMode.Album);
        d = _service.GetGain(playlist, entry)!;
        Assert.Equal(ReplayGainSource.AlbumTag, d.Source);
        Assert.Equal(-9.0, d.GainDb, 6);

        // +12 dB would push the 0.5 peak to 2.0: clipping prevention limits the factor to 1 / peak.
        WriteRg(path, track: 12, trackPeak: 0.5, album: null, albumPeak: null);
        _tags.Read(path); // re-read (the file changed)
        SetMode(ReplayGainMode.Track);
        Assert.Equal(2.0f, _service.GetGain(playlist, Entry(path))!.Linear, 4);
        _settings.Update(SettingsGroup.Playback, s => s.Playback.ReplayGain.PreventClipping = false);
        Assert.Equal((float)Math.Pow(10, 12 / 20.0), _service.GetGain(playlist, Entry(path))!.Linear, 3);
    }

    [Fact]
    public async Task Untagged_files_play_with_the_preamp_until_their_analysis_is_ready()
    {
        var entry = Entry(Copy("sine1k.flac.flac", "plain.flac"));
        var playlist = new Playlist("t");
        playlist.Add([entry]);
        SetMode(ReplayGainMode.Track, preampUntagged: -2);
        var changed = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _service.GainChanged += (_, e) => changed.TrySetResult(e.Path);

        var first = _service.GetGain(playlist, entry)!;
        Assert.Equal(ReplayGainSource.Preamp, first.Source);
        Assert.Equal(-2, first.GainDb);

        Assert.Equal(entry.Path, await changed.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));
        var second = _service.GetGain(playlist, entry)!;
        Assert.Equal(ReplayGainSource.TrackAnalysis, second.Source);
        // FFmpeg measures -21.07 LUFS for this file: track gain -18 - (-21.07) = +3.07 dB, plus the -2 dB pre-amp.
        Assert.InRange(second.GainDb, 0.97, 1.17);
    }

    [Fact]
    public async Task Auto_mode_uses_album_gain_for_album_neighbours_once_all_are_analysed()
    {
        var a = Entry(Copy("sine1k.flac.flac", "album/01.flac"), new TrackTags("One", Album: "Test album"));
        var b = Entry(Copy("sweep.flac.flac", "album/02.flac"), new TrackTags("Two", Album: "Test album"));
        var single = Entry(Copy("clicks.flac.flac", "other/single.flac"), new TrackTags("Single", Album: "Other"));
        var playlist = new Playlist("t");
        playlist.Add([a, b, single]);
        SetMode(ReplayGainMode.Auto);

        foreach (var e in new[] { a, b, single })
        {
            await AnalyseAsync(e);
        }

        Assert.Equal(ReplayGainSource.AlbumAnalysis, _service.GetGain(playlist, a)!.Source);
        Assert.Equal(_service.GetGain(playlist, a)!.GainDb, _service.GetGain(playlist, b)!.GainDb, 6);
        Assert.Equal(ReplayGainSource.TrackAnalysis, _service.GetGain(playlist, single)!.Source);
        Assert.True(ReplayGainService.SameAlbum(a, b));
        Assert.False(ReplayGainService.SameAlbum(a, single));
    }

    [Fact]
    public async Task Analysed_values_are_written_to_tags_when_enabled()
    {
        var path = Copy("sine1k.flac.flac", "write.flac");
        var entry = Entry(path);
        var playlist = new Playlist("t");
        playlist.Add([entry]);
        SetMode(ReplayGainMode.Track);
        _settings.Update(SettingsGroup.Playback, s => s.Playback.ReplayGain.WriteTags = true);

        await AnalyseAsync(entry);

        using (var file = TagLib.File.Create(path))
        {
            Assert.InRange(file.Tag.ReplayGainTrackGain, 2.9, 3.2);
            Assert.InRange(file.Tag.ReplayGainTrackPeak, 0.08, 0.09);
        }
        // The analysis stays cached for the changed file, and the tags are now preferred.
        Assert.NotNull(_analysis.TryGetLoudness(AnalysisTarget.For(entry)));
        Assert.Equal(ReplayGainSource.TrackTag, _service.GetGain(playlist, Entry(path))!.Source);
    }

    [Fact]
    public void Tags_are_read_with_ReplayGain_values()
    {
        var path = Copy("sine1k.flac.flac", "read.flac");
        WriteRg(path, track: -3.21, trackPeak: 0.9, album: -4.5, albumPeak: 0.95);
        var rg = _tags.Read(path)!.ReplayGain!;
        Assert.Equal(-3.21, rg.TrackGainDb!.Value, 2);
        Assert.Equal(0.9, rg.TrackPeak!.Value, 4);
        Assert.Equal(-4.5, rg.AlbumGainDb!.Value, 2);
        Assert.Equal(-6.54, TagService.ParseGain("-6.54 dB"));
        Assert.Null(TagService.ParseGain("n/a"));
    }

    private async Task AnalyseAsync(PlaylistEntry entry)
    {
        var target = AnalysisTarget.For(entry);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(object? s, ReplayGainChangedEventArgs e)
        {
            if (e.Path == entry.Path)
            {
                done.TrySetResult();
            }
        }
        _service.GainChanged += OnChanged;
        _analysis.Request(target, AnalysisKinds.Loudness, AnalysisPriority.Current);
        await done.Task.WaitAsync(TimeSpan.FromSeconds(30));
        _service.GainChanged -= OnChanged;
    }

    private void SetMode(ReplayGainMode mode, double preampTagged = 0, double preampUntagged = 0) =>
        _settings.Update(SettingsGroup.Playback, s =>
        {
            s.Playback.ReplayGain.Mode = mode;
            s.Playback.ReplayGain.PreampTaggedDb = preampTagged;
            s.Playback.ReplayGain.PreampUntaggedDb = preampUntagged;
        });

    private PlaylistEntry Entry(string path, TrackTags? tags = null) => new(path, tags: tags ?? _tags.Read(path));

    private string Copy(string media, string name)
    {
        var target = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(TestMedia.Require(media), target, overwrite: true);
        return target;
    }

    private static void WriteRg(string path, double track, double trackPeak, double? album, double? albumPeak)
    {
        using var file = TagLib.File.Create(path);
        file.Tag.ReplayGainTrackGain = track;
        file.Tag.ReplayGainTrackPeak = trackPeak;
        file.Tag.ReplayGainAlbumGain = album ?? double.NaN;
        file.Tag.ReplayGainAlbumPeak = albumPeak ?? double.NaN;
        file.Save();
    }
}
