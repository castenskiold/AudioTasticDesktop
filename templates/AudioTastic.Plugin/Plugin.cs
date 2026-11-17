using AudioTastic.Sdk.Plugins;
using Microsoft.Extensions.Logging;

namespace AudioTasticPlugin1;

/// <summary>
/// A general plugin: it runs in the background and uses the player API. This one counts the tracks played (kept in
/// its own settings) and logs them.
/// For other plugin types implement IInputPlugin, IDspPlugin, IVisualisationPlugin, IUiPanelPlugin, IOutputPlugin or
/// ICastTargetPlugin instead (see docs/plugins/plugin-guide.md), and list the types in plugin.json.
/// </summary>
public sealed class Plugin : IGeneralPlugin
{
    private IPluginContext? _context;

    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.Player.TrackChanged += OnTrackChanged;
        context.Logger.LogInformation("AudioTasticPlugin1 started");
    }

    public void Dispose()
    {
        if (_context is not null)
        {
            _context.Player.TrackChanged -= OnTrackChanged; // let the player unload the plugin cleanly
        }
    }

    // Player events arrive on background threads. Exceptions are caught by the player; three within a minute
    // switch the plugin off.
    private void OnTrackChanged(object? sender, NowPlayingEventArgs e)
    {
        if (_context is null || e.NowPlaying is null)
        {
            return;
        }
        var count = _context.Settings.GetValue("tracksPlayed", 0) + 1;
        _context.Settings.SetValue("tracksPlayed", count);
        _context.Logger.LogInformation("Track {Count}: {Title}", count, e.NowPlaying.Tags.DisplayName ?? e.NowPlaying.Path);
    }
}
