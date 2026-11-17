namespace AudioTastic.Data.Tests;

public sealed class AppPathsTests
{
    private static AppPathsContext Context(HostOs os, string exeDir, string home, IReadOnlyList<string>? args = null,
        string? appData = null, string? xdg = null) =>
        new(os, exeDir, home, appData, xdg, args ?? []);

    [Fact]
    public void Windows_uses_appdata()
    {
        var paths = AppPaths.Resolve(Context(HostOs.Windows, "C:/app", "C:/Users/a", appData: "C:/Users/a/AppData/Roaming"));
        Assert.False(paths.IsPortable);
        Assert.Equal(Path.Combine("C:/Users/a/AppData/Roaming", "AudioTastic"), paths.Root);
    }

    [Fact]
    public void MacOS_uses_application_support()
    {
        var paths = AppPaths.Resolve(Context(HostOs.MacOS, "/Applications/AudioTastic.app", "/Users/a"));
        Assert.Equal(Path.Combine("/Users/a", "Library", "Application Support", "AudioTastic"), paths.Root);
    }

    [Fact]
    public void Linux_uses_xdg_config_home_when_set()
    {
        var paths = AppPaths.Resolve(Context(HostOs.Linux, "/opt/at", "/home/a", xdg: "/home/a/.cfg"));
        Assert.Equal(Path.Combine("/home/a/.cfg", "AudioTastic"), paths.Root);
    }

    [Fact]
    public void Linux_falls_back_to_dot_config()
    {
        var paths = AppPaths.Resolve(Context(HostOs.Linux, "/opt/at", "/home/a"));
        Assert.Equal(Path.Combine("/home/a", ".config", "AudioTastic"), paths.Root);
    }

    [Fact]
    public void Portable_switch_uses_data_folder_next_to_executable()
    {
        var paths = AppPaths.Resolve(Context(HostOs.Linux, "/opt/at", "/home/a", ["--PORTABLE"]));
        Assert.True(paths.IsPortable);
        Assert.Equal(Path.Combine("/opt/at", "Data"), paths.Root);
    }

    [Fact]
    public void Portable_marker_file_enables_portable_mode()
    {
        using var dir = new TempDir();
        File.WriteAllText(dir.Combine(AppPaths.PortableMarkerFileName), string.Empty);
        var paths = AppPaths.Resolve(Context(AppPathsContext.CurrentOs, dir.Path, "/home/a"));
        Assert.True(paths.IsPortable);
        Assert.Equal(dir.Combine("Data"), paths.Root);
    }

    [Fact]
    public void EnsureCreated_creates_all_folders_with_spec_names()
    {
        using var dir = new TempDir();
        var paths = AppPaths.Resolve(Context(HostOs.Linux, dir.Path, "/home/a", ["--portable"])).EnsureCreated();
        foreach (var folder in paths.Folders)
        {
            Assert.True(Directory.Exists(folder), folder);
        }
        Assert.Equal(
            ["playlists", "presets", "skins", "plugins", "logs", "crash"],
            paths.Folders.Select(Path.GetFileName));
        Assert.Equal("settings.json", Path.GetFileName(paths.SettingsFile));
        Assert.Equal("cache.db", Path.GetFileName(paths.CacheDatabase));
    }
}
