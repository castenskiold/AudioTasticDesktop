namespace AudioTastic.Data;

/// <summary>
/// TL-05: resolves every data path the app uses. Normal mode uses the per-user data folder of the OS;
/// portable mode keeps everything in a "Data" folder next to the executable.
/// Every component that stores files must get its paths from here.
/// </summary>
public sealed class AppPaths
{
    public const string AppFolderName = "AudioTastic";
    public const string PortableMarkerFileName = "portable";
    public const string PortableSwitch = "--portable";
    public const string PortableDataFolderName = "Data";

    private AppPaths(string root, bool isPortable)
    {
        Root = root;
        IsPortable = isPortable;
    }

    /// <summary>Data folder root.</summary>
    public string Root { get; }

    /// <summary>True when running in portable mode.</summary>
    public bool IsPortable { get; }

    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string CacheDatabase => Path.Combine(Root, "cache.db");
    public string PlaylistsFolder => Path.Combine(Root, "playlists");
    public string PresetsFolder => Path.Combine(Root, "presets");
    public string SkinsFolder => Path.Combine(Root, "skins");
    public string PluginsFolder => Path.Combine(Root, "plugins");

    /// <summary>PG-01: writable folders for plugins' own files (one sub-folder per plugin id), created on demand.</summary>
    public string PluginDataFolder => Path.Combine(Root, "plugin-data");
    public string LogsFolder => Path.Combine(Root, "logs");
    public string CrashFolder => Path.Combine(Root, "crash");

    /// <summary>EQ-06: the VST3 scan results (modules, their effects, the blacklist).</summary>
    public string Vst3CacheFile => Path.Combine(Root, "vst3-plugins.json");

    /// <summary>All sub-folders, created by <see cref="EnsureCreated"/>.</summary>
    public IReadOnlyList<string> Folders =>
        [PlaylistsFolder, PresetsFolder, SkinsFolder, PluginsFolder, LogsFolder, CrashFolder];

    /// <summary>Creates the root and all sub-folders if they do not exist.</summary>
    public AppPaths EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        foreach (var folder in Folders)
        {
            Directory.CreateDirectory(folder);
        }
        return this;
    }

    /// <summary>Resolves paths for the current process.</summary>
    public static AppPaths ForCurrentProcess(IReadOnlyList<string> args) =>
        Resolve(AppPathsContext.FromCurrentProcess(args));

    /// <summary>Resolves paths from an explicit context (used by tests).</summary>
    public static AppPaths Resolve(AppPathsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var portable = context.Arguments.Any(a => string.Equals(a, PortableSwitch, StringComparison.OrdinalIgnoreCase))
                       || File.Exists(Path.Combine(context.ExecutableDirectory, PortableMarkerFileName));

        if (portable)
        {
            return new AppPaths(Path.Combine(context.ExecutableDirectory, PortableDataFolderName), isPortable: true);
        }

        return new AppPaths(Path.Combine(UserDataBase(context), AppFolderName), isPortable: false);
    }

    private static string UserDataBase(AppPathsContext c) => c.Os switch
    {
        // %APPDATA%
        HostOs.Windows => c.WindowsAppData ?? Path.Combine(c.HomeDirectory, "AppData", "Roaming"),
        // ~/Library/Application Support
        HostOs.MacOS => Path.Combine(c.HomeDirectory, "Library", "Application Support"),
        // $XDG_CONFIG_HOME or ~/.config
        _ => string.IsNullOrWhiteSpace(c.XdgConfigHome) ? Path.Combine(c.HomeDirectory, ".config") : c.XdgConfigHome,
    };
}

/// <summary>Operating systems AudioTastic runs on.</summary>
public enum HostOs
{
    Windows,
    MacOS,
    Linux,
}

/// <summary>Inputs for <see cref="AppPaths.Resolve"/>; separated from the process so tests can vary them.</summary>
public sealed record AppPathsContext(
    HostOs Os,
    string ExecutableDirectory,
    string HomeDirectory,
    string? WindowsAppData,
    string? XdgConfigHome,
    IReadOnlyList<string> Arguments)
{
    public static HostOs CurrentOs =>
        OperatingSystem.IsWindows() ? HostOs.Windows
        : OperatingSystem.IsMacOS() ? HostOs.MacOS
        : HostOs.Linux;

    public static AppPathsContext FromCurrentProcess(IReadOnlyList<string> args) => new(
        CurrentOs,
        AppContext.BaseDirectory,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : null,
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME"),
        args);
}
