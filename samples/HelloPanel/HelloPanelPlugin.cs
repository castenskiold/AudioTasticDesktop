using AudioTastic.Sdk.Plugins;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace HelloPanel;

/// <summary>
/// A UI panel (Main menu › Windows › Hello panel) that shows the playing track, plus a settings page with one
/// option. Player events arrive on background threads, so UI updates are posted to the UI thread.
/// </summary>
public sealed class HelloPanelPlugin : IUiPanelPlugin, IPluginSettingsPage
{
    private IPluginContext? _context;
    private TextBlock? _label;

    public string Title => "Hello panel";

    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.Player.TrackChanged += OnTrackChanged;
        context.Logger.LogInformation("Hello panel loaded");
    }

    public object CreatePanel()
    {
        _label = new TextBlock { Margin = new Avalonia.Thickness(16), TextWrapping = Avalonia.Media.TextWrapping.Wrap };
        Update();
        return _label;
    }

    public object CreateSettingsPage()
    {
        var settings = _context!.Settings;
        var showFormat = new CheckBox { Content = "Show the stream format", IsChecked = settings.GetValue("showFormat", true) };
        showFormat.IsCheckedChanged += (_, _) =>
        {
            settings.SetValue("showFormat", showFormat.IsChecked == true);
            Update();
        };
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                showFormat,
                new TextBlock { Text = $"Track changes seen: {settings.GetValue("trackChanges", 0)}" },
            },
            HorizontalAlignment = HorizontalAlignment.Left,
        };
    }

    public void Dispose()
    {
        if (_context is not null)
        {
            _context.Player.TrackChanged -= OnTrackChanged;
        }
    }

    private void OnTrackChanged(object? sender, NowPlayingEventArgs e)
    {
        var settings = _context!.Settings;
        settings.SetValue("trackChanges", settings.GetValue("trackChanges", 0) + 1);
        Dispatcher.UIThread.Post(Update);
    }

    private void Update()
    {
        if (_label is null || _context is null)
        {
            return;
        }
        var now = _context.Player.NowPlaying;
        if (now is null)
        {
            _label.Text = "Nothing is playing.";
            return;
        }
        var title = now.Tags.DisplayName ?? Path.GetFileNameWithoutExtension(now.Path);
        _label.Text = _context.Settings.GetValue("showFormat", true)
            ? $"{title}\n{now.Stream.Codec}, {now.Stream.SampleRate} Hz, {now.Stream.Channels} ch"
            : title;
    }
}
