using AudioTastic.Core.Audio.Dsp.Vst3;
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
}
