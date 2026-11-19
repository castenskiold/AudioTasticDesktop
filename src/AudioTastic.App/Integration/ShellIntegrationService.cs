using System.Security;
using AudioTastic.Core.Playlists;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Platform;
using AudioTastic.Plugins.Host;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Integration;

/// <summary>
/// SI-05: keeps the file manager's "Open in AudioTastic" / "Enqueue in AudioTastic" entries in line with the setting,
/// the program's current location and the file types the enabled plugins play. Written at every start (the program
/// may have moved), when the setting changes and when plugins change. In portable mode nothing is written unless the
/// setting was switched on explicitly.
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
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or SecurityException)
        {
            _logger.LogWarning(ex, "Could not update the context menu entries");
        }
    }

    /// <summary>The entries for this program, or null when it does not run from its own executable (e.g. via dotnet).</summary>
    private ContextMenuEntries? Entries()
    {
        var exe = Environment.ProcessPath;
        if (exe is null || Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var extensions = _plugins.LiveSourceFactories.SelectMany(f => f.Extensions)
            .Concat(_playlists.Formats.SelectMany(f => f.Extensions))
            .Where(e => e.StartsWith('.'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new ContextMenuEntries(exe, extensions, OpenText, EnqueueText);
    }

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _plugins.PluginsChanged -= OnPluginsChanged;
    }
}
