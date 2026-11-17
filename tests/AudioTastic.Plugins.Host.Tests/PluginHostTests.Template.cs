using System.Runtime.CompilerServices;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Metadata;
using AudioTastic.Sdk.Plugins;

namespace AudioTastic.Plugins.Host.Tests;

// Compiled only with -p:TemplateCheck=true (scripts/check_plugin_template.py), so normal runs have nothing to skip.
public sealed partial class PluginHostTests
{
    /// <summary>
    /// P4 exit criterion: a plugin made from the 'dotnet new audiotastic-plugin' template and built only against the
    /// AudioTastic.Sdk NuGet package loads, runs (reacts to the player) and unloads.
    /// scripts/check_plugin_template.py builds it and runs this test with AUDIOTASTIC_TEMPLATE_PLUGIN set.
    /// </summary>
    [Fact]
    public void Plugin_built_from_the_sdk_package_loads_runs_and_unloads()
    {
        var built = Environment.GetEnvironmentVariable("AUDIOTASTIC_TEMPLATE_PLUGIN");
        Assert.False(string.IsNullOrEmpty(built), "Set AUDIOTASTIC_TEMPLATE_PLUGIN (scripts/check_plugin_template.py does).");
        var folder = Directory.CreateDirectory(Path.Combine(_builtIn, "from-template")).FullName;
        foreach (var file in Directory.GetFiles(built!))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        }
        Assert.False(File.Exists(Path.Combine(folder, "AudioTastic.Sdk.dll")), "The SDK must not be shipped with a plugin.");
        using var host = CreateHost(_templatePlayer);
        host.LoadAll();

        var weak = RunTemplatePluginAndDisable(host);
        for (var i = 0; i < 10 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive, "The template plugin was not unloaded.");
    }

    private readonly FakePlayer _templatePlayer = new();

    /// <summary>Kept out of the test method so no plugin object stays referenced from its stack frame.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private WeakReference RunTemplatePluginAndDisable(PluginHost host)
    {
        var record = Assert.Single(host.Plugins);
        Assert.True(record.Status == PluginStatus.Enabled, record.Error);
        Assert.Single(host.Instances<IGeneralPlugin>());
        var stream = new AudioStreamInfo("flac", 44_100, 2, 16, 0, true);
        _templatePlayer.RaiseTrackChanged(new NowPlaying("a.flac", new TrackTags("Song", "Artist"), stream, null));
        _templatePlayer.RaiseTrackChanged(new NowPlaying("b.flac", new TrackTags("Next"), stream, null));
        Assert.Equal(2, _settings.Current.Plugins.Data[record.Id]["tracksPlayed"]!.GetValue<int>());
        return LoadAndDisable(host);
    }
}
