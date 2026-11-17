using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Metadata;
using AudioTastic.Sdk.Playlists;
using AudioTastic.Sdk.Plugins;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Plugins.Host.Tests;

public sealed class PluginHostTests : IDisposable
{
    private static readonly string TestPluginBuild = Path.Combine(AppContext.BaseDirectory, "test-plugin");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "at-plugins-" + Guid.NewGuid().ToString("N"));
    private readonly string _builtIn;
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;

    public PluginHostTests()
    {
        _builtIn = Path.Combine(_dir, "builtin");
        Directory.CreateDirectory(_builtIn);
        _paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, _dir, _dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        _settings = new SettingsStore(_paths);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Fact]
    public void Loads_plugin_creates_every_plugin_class_and_offers_its_decoders()
    {
        MakePlugin(_builtIn, "test.good");
        using var host = CreateHost();
        host.LoadAll();

        var record = Assert.Single(host.Plugins);
        Assert.Equal(PluginStatus.Enabled, record.Status);
        Assert.True(record.IsBuiltIn);
        Assert.Equal(2, record.Instances.Count);
        Assert.True(record.HasSettingsPage);
        Assert.Single(host.Instances<IGeneralPlugin>());
        Assert.Equal("Stopped", File.ReadAllText(DataFile("test.good", "general.txt")));

        var factory = Assert.Single(host.SourceFactories);
        Assert.True(factory.CanOpen("a.tst"));
        Assert.False(factory.CanOpen("a.mp3"));
        using var source = factory.Open("a.tst");
        var buffer = new float[100];
        Assert.Equal(100, source.Read(buffer));
        Assert.Equal(0.25f, buffer[99]);
    }

    [Fact]
    public void Plugin_types_come_from_their_own_load_context_but_share_the_sdk()
    {
        MakePlugin(_builtIn, "test.good");
        using var host = CreateHost();
        host.LoadAll();
        var plugin = host.Plugins[0].Instances[0];
        Assert.NotSame(System.Runtime.Loader.AssemblyLoadContext.Default, System.Runtime.Loader.AssemblyLoadContext.GetLoadContext(plugin.GetType().Assembly));
        Assert.IsAssignableFrom<IInputPlugin>(plugin);
    }

    [Fact]
    public void Settings_are_stored_per_plugin_and_survive_a_restart()
    {
        MakePlugin(_builtIn, "test.good");
        using (var host = CreateHost())
        {
            host.LoadAll();
        }
        using (var host = CreateHost())
        {
            host.LoadAll();
        }
        Assert.Equal("test.good 2", File.ReadAllText(DataFile("test.good", "initialized.txt")));
        var reloaded = new SettingsStore(_paths);
        Assert.Equal(2, reloaded.Current.Plugins.Data["test.good"]["starts"]!.GetValue<int>());
    }

    [Fact]
    public void Invalid_manifest_marks_the_plugin_incompatible()
    {
        MakePlugin(_builtIn, "test.old", sdkVersion: "0.1.0");
        var broken = Directory.CreateDirectory(Path.Combine(_builtIn, "broken")).FullName;
        File.WriteAllText(Path.Combine(broken, PluginManifest.FileName), "{ not json");
        using var host = CreateHost();
        host.LoadAll();

        Assert.Equal(2, host.Plugins.Count);
        Assert.All(host.Plugins, p => Assert.Equal(PluginStatus.Incompatible, p.Status));
        Assert.All(host.Plugins, p => Assert.False(string.IsNullOrEmpty(p.Error)));
        Assert.Empty(host.SourceFactories);
    }

    [Fact]
    public void Plugin_that_throws_in_initialize_fails_without_affecting_others()
    {
        MakePlugin(_builtIn, "test.bad", mode: "init-throws");
        MakePlugin(_builtIn, "test.good");
        using var host = CreateHost();
        host.LoadAll();

        var bad = host.Plugins.Single(p => p.Id == "test.bad");
        Assert.Equal(PluginStatus.Failed, bad.Status);
        Assert.Contains("init failed on purpose", bad.Error, StringComparison.Ordinal);
        Assert.Equal(PluginStatus.Enabled, host.Plugins.Single(p => p.Id == "test.good").Status);
        Assert.Single(host.SourceFactories);
    }

    [Fact]
    public void Repeated_faults_disable_the_plugin_and_raise_an_event()
    {
        MakePlugin(_builtIn, "test.crash", mode: "read-throws");
        using var host = CreateHost();
        host.LoadAll();
        PluginDisabledEventArgs? disabled = null;
        host.PluginDisabled += (_, e) => disabled = e;
        var factory = host.SourceFactories.Single();

        for (var i = 0; i < 3; i++)
        {
            using var source = factory.Open("a.tst");
            // A plugin fault reaches the engine as a normal decode error.
            Assert.Throws<AudioSourceException>(() => source.Read(new float[16]));
        }

        var record = host.Plugins.Single();
        Assert.Equal(PluginStatus.Failed, record.Status);
        Assert.NotNull(disabled);
        Assert.Same(record, disabled.Plugin);
        Assert.Empty(host.SourceFactories);
        Assert.False(factory.CanOpen("a.tst"));
        // Session-only: the plugin is not added to the persisted Disabled list.
        Assert.DoesNotContain("test.crash", _settings.Current.Plugins.Disabled);
    }

    [Fact]
    public void Open_errors_are_faults_but_audio_source_exceptions_are_not()
    {
        MakePlugin(_builtIn, "test.good");
        using var host = CreateHost();
        host.LoadAll();
        var factory = host.SourceFactories.Single();
        for (var i = 0; i < 5; i++)
        {
            Assert.Throws<AudioSourceException>(() => factory.Open("missing.tst"));
        }
        Assert.Equal(PluginStatus.Enabled, host.Plugins.Single().Status);
    }

    [Fact]
    public void Disable_unloads_the_plugin_and_is_remembered()
    {
        MakePlugin(_builtIn, "test.good");
        using (var host = CreateHost())
        {
            host.LoadAll();
            var changes = 0;
            host.PluginsChanged += (_, _) => changes++;
            host.Disable("test.good");
            Assert.Equal(PluginStatus.Disabled, host.Plugins.Single().Status);
            Assert.Empty(host.SourceFactories);
            Assert.Equal(1, changes);
            Assert.True(File.Exists(DataFile("test.good", "disposed.txt")));
        }
        using (var host = CreateHost())
        {
            host.LoadAll();
            Assert.Equal(PluginStatus.Disabled, host.Plugins.Single().Status);
            host.Enable("test.good");
            Assert.Equal(PluginStatus.Enabled, host.Plugins.Single().Status);
            Assert.Single(host.SourceFactories);
        }
        Assert.Empty(new SettingsStore(_paths).Current.Plugins.Disabled);
    }

    [Fact]
    public void Unloaded_plugin_load_context_is_collected()
    {
        MakePlugin(_builtIn, "test.good");
        using var host = CreateHost();
        host.LoadAll();
        var weak = LoadAndDisable(host);
        for (var i = 0; i < 10 && weak.IsAlive; i++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
        Assert.False(weak.IsAlive, "The plugin's AssemblyLoadContext was not collected after unloading.");
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndDisable(PluginHost host)
    {
        var weak = PluginHost.LoadContextReference(host.Plugins[0])!;
        host.Disable(host.Plugins[0].Id);
        return weak;
    }

    [Fact]
    public void User_plugin_replaces_built_in_plugin_with_the_same_id()
    {
        MakePlugin(_builtIn, "test.good", version: "1.0.0");
        MakePlugin(_paths.PluginsFolder, "test.good", version: "2.0.0");
        using var host = CreateHost();
        host.LoadAll();
        var record = Assert.Single(host.Plugins);
        Assert.Equal("2.0.0", record.Manifest.Version);
        Assert.False(record.IsBuiltIn);
    }

    [Fact]
    public void Install_and_uninstall_an_atplugin_package()
    {
        var source = MakePlugin(Path.Combine(_dir, "staging"), "test.pkg");
        var package = Path.Combine(_dir, "test.atplugin");
        ZipFile.CreateFromDirectory(source, package);
        using var host = CreateHost();
        host.LoadAll();
        Assert.Empty(host.Plugins);

        var record = host.Install(package);
        Assert.Equal(PluginStatus.Enabled, record.Status);
        Assert.Equal(Path.Combine(_paths.PluginsFolder, "test.pkg"), record.Folder);
        Assert.Single(host.SourceFactories);

        // Installing again replaces it.
        record = host.Install(package);
        Assert.Single(host.Plugins);
        Assert.Equal(PluginStatus.Enabled, record.Status);

        host.Uninstall("test.pkg");
        Assert.Empty(host.Plugins);
        Assert.Empty(host.SourceFactories);
        Assert.False(Directory.Exists(record.Folder) && !_settings.Current.Plugins.PendingRemoval.Contains(record.Folder));
        Assert.False(_settings.Current.Plugins.Data.ContainsKey("test.pkg"));
    }

    [Fact]
    public void Install_rejects_packages_without_manifest_or_with_unsafe_paths()
    {
        using var host = CreateHost();
        host.LoadAll();

        var empty = Path.Combine(_dir, "empty.atplugin");
        using (var zip = ZipFile.Open(empty, ZipArchiveMode.Create))
        {
            zip.CreateEntry("readme.txt");
        }
        Assert.Throws<InvalidDataException>(() => host.Install(empty));

        var evil = Path.Combine(_dir, "evil.atplugin");
        using (var zip = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            WriteEntry(zip, PluginManifest.FileName, ManifestJson("test.evil", "1.0.0", SdkInfo.Version));
            WriteEntry(zip, "AudioTastic.TestPlugin.dll", "x");
            WriteEntry(zip, "../../escaped.txt", "x");
        }
        Assert.Throws<InvalidDataException>(() => host.Install(evil));
        Assert.False(File.Exists(Path.Combine(_paths.Root, "escaped.txt")));
    }

    [Fact]
    public void Built_in_plugins_cannot_be_uninstalled()
    {
        MakePlugin(_builtIn, "test.good");
        using var host = CreateHost();
        host.LoadAll();
        Assert.Throws<InvalidOperationException>(() => host.Uninstall("test.good"));
    }

    [Fact]
    public void Pending_removals_are_deleted_at_the_next_start()
    {
        var leftover = Directory.CreateDirectory(Path.Combine(_paths.PluginsFolder, "old")).FullName;
        _settings.Update(SettingsGroup.Plugins, s => s.Plugins.PendingRemoval.Add(leftover));
        using var host = CreateHost();
        host.LoadAll();
        Assert.False(Directory.Exists(leftover));
        Assert.Empty(_settings.Current.Plugins.PendingRemoval);
    }

    // ---- helpers ----

    private PluginHost CreateHost()
    {
        var host = new PluginHost(_paths, _settings, NullLoggerFactory.Instance, [_builtIn]);
        host.AttachPlayer(new FakePlayer());
        return host;
    }

    private string DataFile(string id, string name) => Path.Combine(_paths.PluginDataFolder, id, name);

    private static string MakePlugin(string root, string id, string mode = "good", string version = "1.0.0", string? sdkVersion = null)
    {
        Assert.True(File.Exists(Path.Combine(TestPluginBuild, "AudioTastic.TestPlugin.dll")), "Test plugin was not built.");
        var folder = Directory.CreateDirectory(Path.Combine(root, id)).FullName;
        foreach (var file in Directory.GetFiles(TestPluginBuild))
        {
            File.Copy(file, Path.Combine(folder, Path.GetFileName(file)), overwrite: true);
        }
        File.WriteAllText(Path.Combine(folder, "mode.txt"), mode);
        File.WriteAllText(Path.Combine(folder, PluginManifest.FileName), ManifestJson(id, version, sdkVersion ?? SdkInfo.Version));
        return folder;
    }

    private static string ManifestJson(string id, string version, string sdkVersion) => JsonSerializer.Serialize(new PluginManifest
    {
        Id = id,
        Name = "Test " + id,
        Version = version,
        SdkVersion = sdkVersion,
        EntryAssembly = "AudioTastic.TestPlugin.dll",
        Types = [PluginType.Input, PluginType.General],
    }, PluginManifest.JsonOptions);

    private static void WriteEntry(ZipArchive zip, string name, string text)
    {
        using var writer = new StreamWriter(zip.CreateEntry(name).Open());
        writer.Write(text);
    }

    private sealed class FakePlayer : IPlayerApi
    {
        public PlayerState State => PlayerState.Stopped;
        public NowPlaying? NowPlaying => null;
        public TimeSpan Position => TimeSpan.Zero;
        public int Volume { get; set; } = 80;
#pragma warning disable CS0067 // Not raised in these tests.
        public event EventHandler<NowPlayingEventArgs>? TrackChanged;
        public event EventHandler<PlayerStateEventArgs>? StateChanged;
#pragma warning restore CS0067
        public Task PlayAsync() => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task StopAsync() => Task.CompletedTask;
        public Task NextAsync() => Task.CompletedTask;
        public Task PreviousAsync() => Task.CompletedTask;
        public Task SeekAsync(TimeSpan position) => Task.CompletedTask;
        public IReadOnlyList<PlaylistItem> GetActivePlaylist() => [];
        public Task AddToPlaylistAsync(IEnumerable<string> paths, bool play) => Task.CompletedTask;
        public TrackTags? ReadTags(string path) => null;
    }
}
