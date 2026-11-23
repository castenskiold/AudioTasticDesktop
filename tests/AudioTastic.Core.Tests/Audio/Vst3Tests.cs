using AudioTastic.Core.Audio;
using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Core.Audio.Dsp.Vst3;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Audio;
using AudioTastic.TestSupport;

namespace AudioTastic.Core.Tests.Audio;

/// <summary>
/// EQ-06: the VST3 host shim against the test fixtures built by native/CMakeLists.txt with -DAT_VST3_TEST_PLUGINS=ON:
/// the SDK's ADelay example (a one-second delay by default) and a module that crashes when scanned.
/// </summary>
public sealed class Vst3Tests
{
    private static string Fixtures => Path.Combine(TestMedia.RepoRoot, "native", "out", AudioTastic.Sdk.Interop.NativeLibraries.PortableRid, "vst3-test");

    private static string Require(string name)
    {
        var path = Path.Combine(Fixtures, name);
        Assert.SkipUnless(Vst3Plugin.IsAvailable && (File.Exists(path) || Directory.Exists(path)),
            $"VST3 host or fixture '{name}' missing. Build native/ with -DAT_VST3_TEST_PLUGINS=ON (native/README.md).");
        return path;
    }

    private static Vst3Class ADelayClass()
    {
        var module = new Vst3Scanner().ScanAsync(Require("adelay.vst3"), cancellationToken: TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        Assert.False(module.IsBlacklisted, module.Problem);
        return Assert.Single(module.Classes);
    }

    [Fact]
    public async Task Scanning_lists_the_effect_classes_of_a_module()
    {
        var module = await new Vst3Scanner().ScanAsync(Require("adelay.vst3"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Null(module.Problem);
        var effect = Assert.Single(module.Classes);
        Assert.Equal("ADelay", effect.Name);
        Assert.Equal("Steinberg Media Technologies", effect.Vendor);
        Assert.Contains("Fx", effect.SubCategories, StringComparison.Ordinal);
        Assert.Equal(32, effect.Id.Length);
    }

    [Fact]
    public async Task A_module_that_crashes_while_scanned_is_blacklisted_and_the_player_survives()
    {
        var module = await new Vst3Scanner().ScanAsync(Require("crash-on-scan.vst3"), cancellationToken: TestContext.Current.CancellationToken);
        Assert.True(module.IsBlacklisted);
        Assert.Contains("Crashed", module.Problem, StringComparison.Ordinal);
        Assert.Empty(module.Classes);
    }

    [Fact]
    public async Task A_file_that_is_not_a_module_is_blacklisted()
    {
        Require("adelay.vst3"); // host present
        var fake = Path.Combine(Path.GetTempPath(), $"not-a-plugin-{Guid.NewGuid():N}.vst3");
        await File.WriteAllTextAsync(fake, "hello", TestContext.Current.CancellationToken);
        try
        {
            var module = await new Vst3Scanner().ScanAsync(fake, cancellationToken: TestContext.Current.CancellationToken);
            Assert.True(module.IsBlacklisted);
        }
        finally
        {
            File.Delete(fake);
        }
    }

    [Fact]
    public void Modules_are_found_in_folders_without_looking_inside_bundles()
    {
        Require("adelay.vst3");
        var modules = Vst3Scanner.FindModules([Fixtures]).Select(Path.GetFileName).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["adelay.vst3", "crash-on-scan.vst3"], modules); // not the adelay.vst3 binary inside the bundle
    }

    [Fact]
    public void ADelay_delays_an_impulse_by_exactly_one_second_through_long_and_short_blocks()
    {
        var effect = ADelayClass();
        using var plugin = Vst3Plugin.Open(Require("adelay.vst3"), effect.Id);
        plugin.Setup(44_100);
        Assert.Equal(0, plugin.Latency);
        Assert.False(plugin.HasEditor); // built without VSTGUI

        var audio = new float[2 * 44_100 * 2]; // two seconds, stereo
        audio[0] = 1f;
        audio[1] = -0.5f;
        var at = 0;
        foreach (var frames in new[] { 10_000, 7, 512, 30_000, 40_000, 7_681 }) // blocks longer than MaxBlockFrames too
        {
            Assert.True(plugin.Process(audio.AsSpan(at * 2, frames * 2)));
            at += frames;
        }
        Assert.Equal(88_200, at);
        for (var i = 0; i < audio.Length; i++)
        {
            var expected = i == 44_100 * 2 ? 1f : i == (44_100 * 2) + 1 ? -0.5f : 0f;
            Assert.Equal(expected, audio[i]);
        }
    }

    [Fact]
    public void State_round_trips_into_a_second_instance()
    {
        var effect = ADelayClass();
        using var first = Vst3Plugin.Open(Require("adelay.vst3"), effect.Id);
        var state = first.GetState();
        Assert.True(state.Length > 8);

        using var second = Vst3Plugin.Open(Require("adelay.vst3"), effect.Id);
        second.SetState(state);
        Assert.Equal(state, second.GetState());
        Assert.Throws<Vst3Exception>(() => second.SetState([1, 2, 3]));
    }

    [Fact]
    public void A_missing_class_or_module_is_a_clear_error()
    {
        var path = Require("adelay.vst3");
        Assert.Throws<Vst3Exception>(() => Vst3Plugin.Open(path, new string('0', 32)));
        Assert.Throws<Vst3Exception>(() => Vst3Plugin.Open(Path.Combine(Fixtures, "missing.vst3"), new string('0', 32)));
    }

    private static SettingsStore Settings()
    {
        var dir = Directory.CreateTempSubdirectory("audiotastic-vst3-").FullName;
        var settings = new SettingsStore(AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated());
        settings.Update(SettingsGroup.EqualizerDsp, s => s.EqualizerDsp.Chain = [.. DspChainEntry.Defaults().Select(e => e with { Enabled = false })]);
        return settings;
    }

    [Fact]
    public void A_vst3_effect_joins_the_chain_before_the_limiter_and_processes_without_allocating()
    {
        var effect = ADelayClass();
        using var graph = new MixerGraph(new AudioFormat(44_100, 2));
        var settings = Settings();
        using var dsp = new DspService(graph, settings);

        var id = dsp.AddVst3(Require("adelay.vst3"), effect.Id, effect.Name);
        var items = dsp.Items;
        var item = Assert.Single(items, i => i.Id == id);
        Assert.True(item.IsVst3);
        Assert.True(item.Enabled);
        Assert.Null(item.Problem);
        Assert.True(items.ToList().IndexOf(item) < items.ToList().FindIndex(i => i.Id == DspChainEntry.Limiter));
        Assert.NotNull(dsp.FindVst3(id)?.Plugin);

        var block = new float[1024 * 2];
        block[0] = 1f;
        graph.Dsp.Process(block, 2);
        long allocated = 0;
        var heard = -1;
        for (var n = 1; n < 50 && heard < 0; n++)
        {
            Array.Clear(block);
            var before = GC.GetAllocatedBytesForCurrentThread();
            graph.Dsp.Process(block, 2);
            allocated += n > 2 ? GC.GetAllocatedBytesForCurrentThread() - before : 0; // after the JIT has settled
            for (var i = 0; i < block.Length; i += 2)
            {
                if (block[i] == 1f)
                {
                    heard = (n * 1024) + (i / 2);
                }
            }
        }
        Assert.Equal(0, allocated);
        Assert.Equal(44_100, heard); // the impulse, one second later, through the chain
    }

    [Fact]
    public void States_are_saved_and_restored_and_removed_effects_leave_the_chain()
    {
        var effect = ADelayClass();
        var settings = Settings();
        string id;
        using (var graph = new MixerGraph(new AudioFormat(44_100, 2)))
        using (var dsp = new DspService(graph, settings))
        {
            id = dsp.AddVst3(Require("adelay.vst3"), effect.Id, effect.Name);
        } // Dispose saves the states
        var saved = Assert.Single(settings.Current.EqualizerDsp.Vst3);
        Assert.False(string.IsNullOrEmpty(saved.State));

        using var again = new MixerGraph(new AudioFormat(48_000, 2));
        using var dsp2 = new DspService(again, settings);
        Assert.NotNull(dsp2.FindVst3(id)?.Plugin); // loaded again from the settings, state restored
        dsp2.RemoveVst3(id);
        Assert.DoesNotContain(dsp2.Items, i => i.Id == id);
        Assert.Null(dsp2.FindVst3(id));
        Assert.Empty(settings.Current.EqualizerDsp.Vst3);
        Assert.DoesNotContain(settings.Current.EqualizerDsp.Chain, e => e.Id == id);
    }

    [Fact]
    public void A_missing_module_is_bypassed_with_a_reason()
    {
        Require("adelay.vst3"); // host present
        using var graph = new MixerGraph(new AudioFormat(44_100, 2));
        using var dsp = new DspService(graph, Settings());
        var id = dsp.AddVst3(Path.Combine(Fixtures, "gone.vst3"), new string('A', 32), "Gone");
        var item = Assert.Single(dsp.Items, i => i.Id == id);
        Assert.NotNull(item.Problem);
        var block = new float[64 * 2];
        block[0] = 0.5f;
        graph.Dsp.Process(block, 2);
        Assert.Equal(0.5f, block[0]); // passes through untouched
    }

    /// <summary>
    /// Local check (EQ-06 exit criterion): every effect in the folder AUDIOTASTIC_VST3_DIR scans, loads, processes a
    /// second of noise into finite audio and round-trips its state. Does nothing when the variable is not set.
    /// </summary>
    [Fact]
    public async Task Real_plugins_in_a_folder_load_process_and_keep_their_state()
    {
        var folder = Environment.GetEnvironmentVariable("AUDIOTASTIC_VST3_DIR");
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder) || !Vst3Plugin.IsAvailable)
        {
            return; // a local check (like the kick count on real tracks): the suite stays at 0 skipped
        }
        var scanner = new Vst3Scanner();
        var report = new List<string>();
        var random = new Random(1);
        foreach (var path in Vst3Scanner.FindModules([folder!]))
        {
            var module = await scanner.ScanAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            Assert.False(module.IsBlacklisted, $"{path}: {module.Problem}");
            foreach (var effect in module.Classes)
            {
                using var plugin = Vst3Plugin.Open(path, effect.Id);
                plugin.Setup(48_000);
                var audio = new float[48_000 * 2];
                for (var i = 0; i < audio.Length; i++)
                {
                    audio[i] = (float)((random.NextDouble() * 2) - 1) * 0.25f;
                }
                Assert.True(plugin.Process(audio), effect.Name);
                Assert.All(audio, v => Assert.True(float.IsFinite(v), effect.Name));
                var state = plugin.GetState();
                plugin.SetState(state);
                report.Add($"{effect.Name} ({effect.Vendor}): latency {plugin.Latency}, editor {plugin.HasEditor}, state {state.Length} bytes");
            }
        }
        Assert.NotEmpty(report);
        File.WriteAllLines(Path.Combine(Path.GetTempPath(), "audiotastic-vst3-report.txt"), report);
    }
}
