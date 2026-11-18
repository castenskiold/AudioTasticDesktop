using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using AudioTastic.Data;
using AudioTastic.Data.Cache;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Metadata;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Core.Tests.Audio;

/// <summary>EQ-02 presets and Winamp .eqf import; EQ-03 auto EQ.</summary>
public sealed class EqPresetTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("audiotastic-eq-").FullName;
    private readonly AppPaths _paths;

    public EqPresetTests() =>
        _paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, _dir, _dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    [Fact]
    public void Eqf_values_map_0_to_plus_12_and_63_to_minus_12_dB()
    {
        var bytes = EqfFile.Write([new EqPreset("Test", new EqualizerSettings { Bands = [12, -12, 0, 6, -6, 3, -3, 1.5, -1.5, 9], PreampDb = -12 })]);
        Assert.Equal(31 + 268, bytes.Length);
        Assert.Equal(0, bytes[31 + 257]);      // +12 dB
        Assert.Equal(63, bytes[31 + 257 + 1]); // -12 dB
        Assert.Equal(63, bytes[31 + 257 + 10]); // preamp -12 dB

        var preset = Assert.Single(EqfFile.Parse(bytes));
        Assert.Equal("Test", preset.Name);
        double[] original = [12, -12, 0, 6, -6, 3, -3, 1.5, -1.5, 9];
        Assert.All(original.Zip(preset.Settings.Bands), p => Assert.InRange(p.Second, p.First - 0.2, p.First + 0.2)); // 64 steps of 0.38 dB
        Assert.Equal(-12, preset.Settings.PreampDb);
    }

    [Fact]
    public void Library_files_with_several_presets_import_with_unique_names()
    {
        var file = Path.Combine(_dir, "winamp.q1");
        File.WriteAllBytes(file, EqfFile.Write([
            new EqPreset("Rock", new EqualizerSettings { Bands = [6, 3, 0, 0, 0, 0, 0, 3, 6, 6] }),
            new EqPreset("Mine", new EqualizerSettings()),
        ]));
        var store = new EqPresetStore(_paths.PresetsFolder);

        var imported = store.Import(file);

        Assert.Equal(["Rock (2)", "Mine"], imported.Select(p => p.Name)); // "Rock" is built in
        Assert.Equal(2, new EqPresetStore(_paths.PresetsFolder).User.Count); // saved
        Assert.Throws<InvalidDataException>(() => store.Import(WriteText("not-eq.eqf", "hello")));
    }

    [Fact]
    public void User_presets_can_be_saved_renamed_and_deleted_and_shadow_built_ins()
    {
        var store = new EqPresetStore(_paths.PresetsFolder);
        store.Save("Pop", new EqualizerSettings { Bands = [1, 1, 1, 1, 1, 1, 1, 1, 1, 1] });
        Assert.False(store.Find("pop")!.IsBuiltIn); // the user's copy wins
        Assert.True(store.Rename("Pop", "My pop"));
        Assert.True(store.Find("Pop")!.IsBuiltIn);
        Assert.True(store.Delete("My pop"));
        Assert.Empty(store.User);
        Assert.Contains(EqPresetStore.BuiltIn, p => p.Name == "Flat");
    }

    [Fact]
    public async Task Auto_eq_loads_the_file_preset_before_the_genre_preset_and_switches_the_eq_on()
    {
        var settings = new SettingsStore(_paths);
        var cache = new CacheStore(_paths);
        var factory = new SyntheticFactory();
        using var output = new FakeAudioOutput();
        using var engine = new PlaybackEngine([factory], output, settings, NullLogger<PlaybackEngine>.Instance);
        var presets = new EqPresetStore(_paths.PresetsFolder);
        using var auto = new AutoEqService(engine, settings, presets, cache);
        settings.Update(SettingsGroup.EqualizerDsp, s => s.EqualizerDsp.AutoEq = true);
        auto.AssignToGenre("Rock", "Rock");
        var rock = new PlaylistEntry(TempFiles.Touch(SyntheticFactory.PathFor("rock", seconds: 5)), tags: new TrackTags(Title: "r", Genre: "rock"));
        var special = new PlaylistEntry(TempFiles.Touch(SyntheticFactory.PathFor("special", seconds: 5)), tags: new TrackTags(Title: "s", Genre: "Rock"));
        auto.AssignToFile(special.Path, "Vocal");
        var playlist = new Playlist("test", [rock, special]);

        await engine.PlayAsync(playlist, rock);
        await Wait.UntilAsync(() => settings.Current.EqualizerDsp.Equalizer.Preset == "Rock", 5_000);
        Assert.True(settings.Current.EqualizerDsp.Chain.Single(e => e.Id == DspChainEntry.Equalizer).Enabled);

        await engine.PlayAsync(playlist, special);
        await Wait.UntilAsync(() => settings.Current.EqualizerDsp.Equalizer.Preset == "Vocal", 5_000);
        Assert.Equal(EqPresetStore.BuiltIn.Single(p => p.Name == "Vocal").Settings.Bands, settings.Current.EqualizerDsp.Equalizer.Bands);
        await engine.StopAsync();
    }

    private string WriteText(string name, string text)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, text);
        return path;
    }
}
