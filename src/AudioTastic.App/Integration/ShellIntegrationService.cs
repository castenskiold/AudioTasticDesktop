using System.Security;
using AudioTastic.Core.Playlists;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Platform;
using AudioTastic.Plugins.Host;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Integration;

/// <summary>
/// SI-05: keeps the file manager's "Open in AudioTastic" / "Enqueue in AudioTastic" entries and the file type
/// registration in line with the settings, the program's current location and the file types the enabled plugins
/// play. Written at every start (the program may have moved), when the settings change and when plugins change.
/// In portable mode nothing is written unless a setting was switched on explicitly.
/// </summary>
public sealed class ShellIntegrationService : IDisposable
{
    public const string OpenText = "Open in AudioTastic";

    public const string EnqueueText = "Enqueue in AudioTastic";

    private readonly IPlatformServices _platform;
    private readonly SettingsStore _settings;
    private readonly AppPaths _paths;
    private readonly PluginHost _plugins;
    private readonly PlaylistService _playlists;
    private readonly ILogger<ShellIntegrationService> _logger;
    private readonly Lock _gate = new();
    private Task _last = Task.CompletedTask;

    public ShellIntegrationService(IPlatformServices platform, SettingsStore settings, AppPaths paths, PluginHost plugins,
        PlaylistService playlists, ILogger<ShellIntegrationService> logger)
    {
        _platform = platform;
        _settings = settings;
        _paths = paths;
        _plugins = plugins;
        _playlists = playlists;
        _logger = logger;
    }

    /// <summary>True when the entries should be there: the setting, or by default on unless portable.</summary>
    public static bool IsEnabled(SystemIntegrationSettings settings, bool portable) => settings.ContextMenu ?? !portable;

    /// <summary>SI-05: true when the first start should ask whether to use AudioTastic for audio files.</summary>
    public bool ShouldAskAboutFileTypes =>
        _platform.Shell.SupportsFileAssociations && !_paths.IsPortable && _settings.Current.SystemIntegration.FileAssociations is null
        && Environment.ProcessPath is { } exe && !IsDotnetHost(exe);

    /// <summary>Opens Windows' Default apps page for AudioTastic.</summary>
    public void OpenDefaultApps() => _platform.Shell.OpenDefaultAppsSettings();

    public void Start()
    {
        _settings.Changed += OnSettingsChanged;
        _plugins.PluginsChanged += OnPluginsChanged;
        Refresh();
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Group == SettingsGroup.SystemIntegration)
        {
            Refresh();
        }
    }

    private void OnPluginsChanged(object? sender, EventArgs e) => Refresh();

    /// <summary>Writes (or removes) the entries in the background, one update at a time.</summary>
    public Task Refresh()
    {
        if (!_platform.Shell.SupportsContextMenu)
        {
            return Task.CompletedTask;
        }
        lock (_gate)
        {
            _last = _last.ContinueWith(_ => Apply(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            return _last;
        }
    }

    private void Apply()
    {
        var settings = _settings.Current.SystemIntegration;
        try
        {
            if (IsEnabled(settings, _paths.IsPortable))
            {
                if (Entries() is { } entries)
                {
                    _platform.Shell.SetContextMenu(entries);
                    _logger.LogInformation("Context menu entries registered for {Count} file types ({Exe})", entries.Extensions.Count, entries.ExecutablePath);
                }
            }
            else if (settings.ContextMenu == false)
            {
                _platform.Shell.SetContextMenu(null);
                _logger.LogInformation("Context menu entries removed");
            }
            if (_platform.Shell.SupportsStartMenuShortcut)
            {
                if (settings.StartMenuShortcut ?? !_paths.IsPortable)
                {
                    if (Executable() is { } exe)
                    {
                        _platform.Shell.SetStartMenuShortcut(exe, Program.AppId);
                    }
                }
                else if (settings.StartMenuShortcut == false)
                {
                    _platform.Shell.SetStartMenuShortcut(null, Program.AppId);
                }
            }
            if (_platform.Shell.SupportsFileAssociations && settings.FileAssociations is { } associate)
            {
                var associations = associate ? Associations() : null;
                if ((!associate || associations is not null) && _platform.Shell.SetFileAssociations(associations))
                {
                    _logger.LogInformation("File type registration {State} ({Count} types)", associate ? "written" : "removed",
                        associations is null ? 0 : associations.AudioExtensions.Count + associations.PlaylistExtensions.Count);
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException or System.Runtime.InteropServices.COMException)
        {
            _logger.LogWarning(ex, "Could not update the context menu entries");
        }
    }

    /// <summary>The entries for this program, or null when it does not run from its own executable (e.g. via dotnet).</summary>
    private ContextMenuEntries? Entries() => Executable() is { } exe
        ? new ContextMenuEntries(exe, [.. AudioExtensions().Concat(PlaylistExtensions()).Distinct(StringComparer.OrdinalIgnoreCase)], OpenText, EnqueueText)
        : null;

    private FileAssociations? Associations() => Executable() is { } exe
        ? new FileAssociations(exe, AudioExtensions(), PlaylistExtensions())
        : null;

    private static string? Executable() => Environment.ProcessPath is { } exe && !IsDotnetHost(exe) ? exe : null;

    private static bool IsDotnetHost(string exe) => Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

    private string[] AudioExtensions() =>
        [.. _plugins.LiveSourceFactories.SelectMany(f => f.Extensions).Where(e => e.StartsWith('.')).Distinct(StringComparer.OrdinalIgnoreCase)];

    private string[] PlaylistExtensions() =>
        [.. _playlists.Formats.SelectMany(f => f.Extensions).Where(e => e.StartsWith('.')).Distinct(StringComparer.OrdinalIgnoreCase)];

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _plugins.PluginsChanged -= OnPluginsChanged;
    }
}
