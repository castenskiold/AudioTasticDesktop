using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using System.Reflection;
using AudioTastic.App.Skins;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Platform;
using AudioTastic.Plugins.Host;
using AudioTastic.Sdk.Plugins;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Ui;

/// <summary>
/// UI-10 part 2: the General settings window. Ten groups on the left, the selected group's options on the right.
/// Changes apply immediately (SettingsStore); every group has "Reset to defaults". Pages of later phases say
/// which phase fills them. Follows the dialog theme (UI-09).
/// </summary>
public sealed class SettingsWindow : Window
{
    internal static readonly (SettingsGroup Group, string Title)[] Groups =
    [
        (SettingsGroup.Playback, "Playback"),
        (SettingsGroup.Output, "Output"),
        (SettingsGroup.Crossfading, "Crossfading"),
        (SettingsGroup.Metadata, "Metadata"),
        (SettingsGroup.CastingNetwork, "Casting & network"),
        (SettingsGroup.Interface, "Interface"),
        (SettingsGroup.SystemIntegration, "System integration"),
        (SettingsGroup.EqualizerDsp, "Equalizer & DSP"),
        (SettingsGroup.Plugins, "Plugins"),
        (SettingsGroup.General, "General"),
    ];

    private readonly PlayerShell _shell;
    private readonly SettingsStore _settings;
    private readonly AppPaths _paths;
    private readonly IPlatformServices _platform;
    private readonly PluginHost _plugins;
    private readonly ILogger<SettingsWindow> _logger;
    private readonly ListBox _groups;
    private readonly ContentControl _page = new();

    public SettingsWindow(PlayerShell shell, SettingsStore settings, AppPaths paths, IPlatformServices platform, PluginHost plugins,
        ILogger<SettingsWindow> logger)
    {
        _plugins = plugins;
        _shell = shell;
        _settings = settings;
        _paths = paths;
        _platform = platform;
        _logger = logger;
        Title = "AudioTastic settings";
        Width = 760;
        Height = 540;
        MinWidth = 600;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://AudioTastic/Assets/audiotastic.ico")));

        _groups = new ListBox { ItemsSource = Groups.Select(g => g.Title).ToArray(), Width = 190, SelectedIndex = 5 };
        Avalonia.Automation.AutomationProperties.SetName(_groups, "Settings groups");
        _groups.SelectionChanged += (_, _) => ShowPage();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(12) };
        grid.Children.Add(_groups);
        var scroller = new ScrollViewer { Content = _page, Margin = new Thickness(16, 0, 0, 0) };
        Grid.SetColumn(scroller, 1);
        grid.Children.Add(scroller);
        Content = grid;
        ShowPage();
    }

    private SettingsGroup CurrentGroup => Groups[Math.Max(0, _groups.SelectedIndex)].Group;

    /// <summary>User manual screenshots (Debug builds): shows a group's page.</summary>
    internal void SelectGroup(int index) => _groups.SelectedIndex = index;

    /// <summary>User manual screenshots: the current page's content, rendered whole (not just the visible part).</summary>
    internal Control? PageContent => _page.Content as Control;

    private void ShowPage()
    {
        var (group, title) = Groups[Math.Max(0, _groups.SelectedIndex)];
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeight.Light });
        switch (group)
        {
            case SettingsGroup.Playback:
                PlaybackPage(panel);
                break;
            case SettingsGroup.Output:
                OutputPage(panel);
                break;
            case SettingsGroup.Crossfading:
                CrossfadingPage(panel);
                break;
            case SettingsGroup.Metadata:
                MetadataPage(panel);
                break;
            case SettingsGroup.CastingNetwork:
                CastingPage(panel);
                break;
            case SettingsGroup.Interface:
                InterfacePage(panel);
                break;
            case SettingsGroup.SystemIntegration:
                SystemIntegrationPage(panel);
                break;
            case SettingsGroup.EqualizerDsp:
                EqualizerDspPage(panel);
                break;
            case SettingsGroup.Plugins:
                panel.Children.Add(new PluginManagerPage(_plugins, _paths, _platform, _logger));
                break;
            case SettingsGroup.General:
                GeneralPage(panel);
                break;
        }
        if (group == SettingsGroup.Plugins)
        {
            _page.Content = panel; // plugin choices are managed per plugin; no "reset to defaults"
            return;
        }
        var reset = new Button { Content = "Reset to defaults", Margin = new Thickness(0, 12, 0, 0) };
        reset.Click += (_, _) =>
        {
            _settings.ResetGroup(CurrentGroup);
            ShowPage();
        };
        panel.Children.Add(reset);
        _page.Content = panel;
    }

    /// <summary>CN-01 / CN-04 / CN-05: casting (more options arrive with the other device families in P11).</summary>
    private void CastingPage(StackPanel panel)
    {
        var cast = _settings.Current.CastingNetwork;
        var port = new NumericUpDown { Minimum = 0, Maximum = 65535, Increment = 1, Value = cast.ServerPort, FormatString = "0", Width = 160 };
        port.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } v)
            {
                _settings.Update(SettingsGroup.CastingNetwork, s => s.CastingNetwork.ServerPort = (int)v);
            }
        };
        Row(panel, "Streaming server port", port,
            "0 = a free port chosen when casting starts. Set a fixed port if your firewall needs a rule for it. Takes effect the next time casting starts.");
        Note(panel, "Cast with the CAST button in the main window: it lists this computer, Chromecasts and DLNA devices (smart TVs, network receivers) on your network. " +
            "The device plays the file itself when it can; other formats are converted while they are sent (FLAC for Chromecast, WAV for DLNA). " +
            "AirPlay is not supported yet.");

        // CN-09: the video sent to a TV (visuals only, and later audio and video).
        Heading(panel, "Video on a TV");
        var choices = new List<(string Id, string Name)> { ("spectrum", "Spectrum analyser"), ("oscilloscope", "Oscilloscope") };
        foreach (var record in _plugins.Plugins.Where(r => r.Status == PluginStatus.Enabled))
        {
            foreach (var plugin in record.Instances.OfType<IVisualisationPlugin>())
            {
                var offscreen = false;
                var name = record.Manifest.Name;
                _plugins.Guard(record, () =>
                {
                    offscreen = plugin.CanRenderOffscreen;
                    name = plugin.Name;
                });
                if (offscreen)
                {
                    choices.Add((record.Id, $"{name} (plugin)"));
                }
            }
        }
        var visual = new ComboBox { ItemsSource = choices.Select(c => c.Name).ToList(), Width = 320,
            SelectedIndex = Math.Max(0, choices.FindIndex(c => string.Equals(c.Id, cast.VideoVisualisation, StringComparison.OrdinalIgnoreCase))) };
        visual.SelectionChanged += (_, _) =>
        {
            if (visual.SelectedIndex >= 0)
            {
                _settings.Update(SettingsGroup.CastingNetwork, s => s.CastingNetwork.VideoVisualisation = choices[visual.SelectedIndex].Id);
            }
        };
        Row(panel, "Visualisation", visual, "What the TV shows. Visualisation plugins appear here when they can draw for a TV (SDK 0.4.2).");
        var heights = new[] { 720, 1080 };
        var size = new ComboBox { ItemsSource = new[] { "1280 × 720", "1920 × 1080" }, Width = 160, SelectedIndex = cast.VideoHeight >= 1080 ? 1 : 0 };
        size.SelectionChanged += (_, _) => _settings.Update(SettingsGroup.CastingNetwork, s => s.CastingNetwork.VideoHeight = heights[Math.Max(0, size.SelectedIndex)]);
        Row(panel, "Resolution", size);
        var rates = new[] { 25, 30, 60 };
        var fps = new ComboBox { ItemsSource = rates.Select(r => $"{r} frames a second").ToList(), Width = 200, SelectedIndex = Math.Max(0, Array.IndexOf(rates, cast.VideoFps)) };
        fps.SelectionChanged += (_, _) => _settings.Update(SettingsGroup.CastingNetwork, s => s.CastingNetwork.VideoFps = rates[Math.Max(0, fps.SelectedIndex)]);
        Row(panel, "Frame rate", fps, "Takes effect the next time video casting starts. 60 needs a fast computer.");
        var delay = new Button { Content = _shell.Cast.VideoDevice is { } tv ? $"Picture delay for {tv.Name}…" : "Picture delay…", IsEnabled = _shell.Cast.VideoDevice is not null };
        delay.Click += (_, _) => _shell.ShowPictureDelay();
        Row(panel, "Match the picture to the music", delay,
            "A TV shows the picture a few seconds late; AudioTastic draws it that far ahead. Set the delay for each TV while it shows the visuals " +
            "(also in the CAST menu under the TV).");
    }

    /// <summary>SI-05 / SI-06: the file manager's context menu and what opened files do.</summary>
    private void SystemIntegrationPage(StackPanel panel)
    {
        var si = _settings.Current.SystemIntegration;
        void Update(Action<SystemIntegrationSettings> change) => _settings.Update(SettingsGroup.SystemIntegration, s => change(s.SystemIntegration));

        var open = new ComboBox { ItemsSource = new[] { "Play them", "Enqueue them (add to the playlist)" }, SelectedIndex = (int)si.OpenFiles, Width = 320 };
        open.SelectionChanged += (_, _) => Update(s => s.OpenFiles = (OpenFilesAction)Math.Max(0, open.SelectedIndex));
        Row(panel, "Files opened from the file manager or the command line", open,
            "AudioTastic runs once: files opened while it is running go to the running player, into the active playlist.");

        if (_platform.Shell.SupportsContextMenu)
        {
            Check(panel, $"Context menu: '{Integration.ShellIntegrationService.OpenText}' and '{Integration.ShellIntegrationService.EnqueueText}'",
                Integration.ShellIntegrationService.IsEnabled(si, _paths.IsPortable), v => Update(s => s.ContextMenu = v));
            Note(panel, "Shown when you right-click audio files and playlists in Explorer. On Windows 11 they are under 'Show more options' " +
                "(or Shift+right-click). Switch this off before deleting the program to remove them." +
                (_paths.IsPortable ? " Portable mode: off unless you switch it on." : ""));
        }
        else
        {
            Note(panel, "The file manager's context menu entries (Open / Enqueue in AudioTastic) are available on Windows only for now.");
        }

        if (_platform.Shell.SupportsFileAssociations)
        {
            Heading(panel, "File types");
            Check(panel, "Register AudioTastic for the audio and playlist types it plays", si.FileAssociations == true,
                v => Update(s => s.FileAssociations = v));
            var choose = new Button { Content = "Choose default apps…" };
            Avalonia.Automation.AutomationProperties.SetName(choose, "Choose default apps");
            choose.Click += async (_, _) => await _shell.ChooseDefaultAppsAsync();
            panel.Children.Add(choose);
            Note(panel, "Registering puts AudioTastic in 'Open with' and in Windows Settings › Default apps; Windows lets only you make it the default. " +
                "'Choose default apps…' registers it and opens that page. Double-clicked files go to the running player and are played or enqueued as chosen above." +
                (_paths.IsPortable ? " Portable mode: nothing is registered unless you switch it on." : ""));
        }
        if (_platform.Shell.SupportsStartMenuShortcut)
        {
            Heading(panel, "Start menu");
            Check(panel, "Start menu shortcut (Windows also uses it to name AudioTastic in its media controls)",
                si.StartMenuShortcut ?? !_paths.IsPortable, v => Update(s => s.StartMenuShortcut = v));
        }

        if (_platform.MediaControls.IsSupported)
        {
            Heading(panel, "Media controls");
            Check(panel, "Show AudioTastic in the Windows media controls (media overlay, lock screen, keyboard media keys, headset buttons)",
                si.MediaControls, v => Update(s => s.MediaControls = v));
        }

        if (_shell.Hotkeys.IsSupported)
        {
            HotkeysSection(panel, si);
        }

        if (_platform.Taskbar.IsSupported)
        {
            Heading(panel, "Taskbar");
            Check(panel, "Show playback progress on the taskbar button (previous / play / next are always under the taskbar preview)",
                si.TaskbarProgress, v => Update(s => s.TaskbarProgress = v));
        }

        Heading(panel, "Tray");
        Check(panel, "Show the tray icon (player menu, sleep timer; click it to show or hide the player)", si.TrayIcon, v =>
        {
            // Without the icon there is no way back from the tray, so its options go off with it (Anders).
            Update(s =>
            {
                s.TrayIcon = v;
                if (!v)
                {
                    s.MinimiseToTray = false;
                    s.CloseToTray = false;
                }
            });
            ShowPage();
        });
        var trayOptions = new StackPanel { Spacing = 6, Margin = new Thickness(24, 0, 0, 0), IsEnabled = si.TrayIcon };
        Check(trayOptions, "Minimise to the tray", si.MinimiseToTray, v => Update(s => s.MinimiseToTray = v));
        Check(trayOptions, "Close to the tray (exit from the tray menu or Main menu › Exit)", si.CloseToTray, v => Update(s => s.CloseToTray = v));
        panel.Children.Add(trayOptions);
        Note(panel, "Track-change notifications are switched on the Interface page.");
    }

    /// <summary>
    /// SI-01: global hotkeys on/off (Anders) and one hotkey per action. Click a field and press the keys; Backspace
    /// clears it. While a field has the focus the global hotkeys are paused, so a taken combination can be re-pressed.
    /// </summary>
    private void HotkeysSection(StackPanel panel, SystemIntegrationSettings si)
    {
        Heading(panel, "Global hotkeys");
        Check(panel, "Global hotkeys (work while another program is in front)", si.GlobalHotkeys, v =>
        {
            _settings.Update(SettingsGroup.SystemIntegration, s => s.SystemIntegration.GlobalHotkeys = v);
            ShowPage();
        });
        var table = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("180,Auto,*"),
            RowSpacing = 4,
            ColumnSpacing = 8,
            Margin = new Thickness(24, 0, 0, 0),
            IsEnabled = si.GlobalHotkeys,
        };
        var hotkeys = _shell.Hotkeys;
        var row = 0;
        foreach (var (action, label) in Integration.HotkeyManager.Actions)
        {
            table.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var name = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            var box = new HotkeyBox(si.Hotkeys.GetValueOrDefault(action));
            Avalonia.Automation.AutomationProperties.SetName(box, "Hotkey for " + label);
            var status = new TextBlock
            {
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Orange,
                FontSize = 12,
                Text = hotkeys.Failed.Contains(action) && si.GlobalHotkeys ? "In use by another program" : string.Empty,
            };
            box.GotFocus += (_, _) => hotkeys.Suspend(true);
            box.LostFocus += (_, _) => hotkeys.Suspend(false);
            box.Rejected += (_, message) => status.Text = message;
            box.HotkeyChanged += (_, text) =>
            {
                status.Text = string.Empty;
                _settings.Update(SettingsGroup.SystemIntegration, s =>
                {
                    s.SystemIntegration.Hotkeys = new Dictionary<string, string>(s.SystemIntegration.Hotkeys) { [action] = text };
                    // One combination runs one action: take it away from any other action.
                    foreach (var other in s.SystemIntegration.Hotkeys.Where(h => h.Key != action && text.Length > 0 && h.Value == text).ToList())
                    {
                        s.SystemIntegration.Hotkeys[other.Key] = string.Empty;
                    }
                });
            };
            Grid.SetRow(name, row);
            Grid.SetRow(box, row);
            Grid.SetColumn(box, 1);
            Grid.SetRow(status, row);
            Grid.SetColumn(status, 2);
            table.Children.Add(name);
            table.Children.Add(box);
            table.Children.Add(status);
            row++;
        }
        panel.Children.Add(table);
        var defaults = new Button { Content = "Default hotkeys", Margin = new Thickness(24, 0, 0, 0), IsEnabled = si.GlobalHotkeys };
        defaults.Click += (_, _) =>
        {
            _settings.Update(SettingsGroup.SystemIntegration, s => s.SystemIntegration.Hotkeys = SystemIntegrationSettings.DefaultHotkeys());
            ShowPage();
        };
        panel.Children.Add(defaults);
        Note(panel, "Click a field and press the keys (Ctrl, Alt or Win with a key); Backspace clears it. Keyboard media keys always work " +
            "through the media controls. A combination another program already uses shows 'In use by another program'.");
    }

    /// <summary>MD-02..MD-04: album art, lyrics and online lookup.</summary>
    private void MetadataPage(StackPanel panel)
    {
        var m = _settings.Current.Metadata;
        Check(panel, "Show album art (embedded cover, else cover/folder/front .jpg or .png in the folder)", m.AlbumArt,
            v => _settings.Update(SettingsGroup.Metadata, s => s.Metadata.AlbumArt = v));
        Check(panel, "Show lyrics (a .lrc file next to the track, or lyrics in its tags)", m.Lyrics,
            v => _settings.Update(SettingsGroup.Metadata, s => s.Metadata.Lyrics = v));
        Check(panel, "Online lookup (Look up online… in the tag editor and the playlist menu)", m.OnlineLookup,
            v => _settings.Update(SettingsGroup.Metadata, s => s.Metadata.OnlineLookup = v));
        var services = new StackPanel { Spacing = 6, Margin = new Thickness(24, 0, 0, 0) };
        Check(services, "Tags from MusicBrainz", m.LookupTags, v => _settings.Update(SettingsGroup.Metadata, s => s.Metadata.LookupTags = v));
        Check(services, "Covers from the Cover Art Archive", m.LookupCovers, v => _settings.Update(SettingsGroup.Metadata, s => s.Metadata.LookupCovers = v));
        // MD-03: optional Discogs fallback for covers, with the user's own token.
        Check(services, "Discogs first (tags and covers; MusicBrainz and the Cover Art Archive when Discogs has nothing)", m.LookupDiscogs,
            v => _settings.Update(SettingsGroup.Metadata, s => s.Metadata.LookupDiscogs = v));
        var discogsToken = new TextBox { Text = m.DiscogsToken ?? string.Empty, PasswordChar = '•', Width = 320, PlaceholderText = "Personal access token" };
        discogsToken.LostFocus += (_, _) =>
        {
            var value = string.IsNullOrWhiteSpace(discogsToken.Text) ? null : discogsToken.Text.Trim();
            _settings.Update(SettingsGroup.Metadata, s => s.Metadata.DiscogsToken = value);
        };
        Row(services, "Discogs token", discogsToken,
            "Discogs needs your own token: discogs.com › Settings › Developers › Generate new token. It stays in this computer's settings file; settings exports leave it out.");
        panel.Children.Add(services);
        Note(panel, "Lookups happen only when you ask for them; they send the track's title, artist and length to MusicBrainz. Results are shown for you to accept before anything is saved.");
        Note(panel, "Album art shows in the main window and in the Art & Lyrics window (Alt+L), lyrics below the art; click a line of synced lyrics to jump there. Tags, covers and lyrics are edited with Edit tags… (Ctrl+E) in the playlist.");
    }

    private void PlaybackPage(StackPanel panel)
    {
        var rg = _settings.Current.Playback.ReplayGain;
        void Update(Action<ReplayGainSettings> change) => _settings.Update(SettingsGroup.Playback, s => change(s.Playback.ReplayGain));

        var mode = new ComboBox
        {
            ItemsSource = new[] { "Off", "Track gain", "Album gain", "Auto (album gain for album neighbours)" },
            SelectedIndex = (int)rg.Mode,
            Width = 320,
        };
        mode.SelectionChanged += (_, _) => Update(r => r.Mode = (ReplayGainMode)Math.Max(0, mode.SelectedIndex));
        Row(panel, "ReplayGain", mode,
            "Plays tracks at the same loudness, using ReplayGain tags or, for untagged files, an EBU R128 analysis in the background. " +
            "'RG' appears in the main window while a gain is applied.");

        NumericUpDown Db(double value, Action<double> changed)
        {
            var box = new NumericUpDown { Minimum = -15, Maximum = 15, Increment = 0.5m, Value = (decimal)value, FormatString = "+0.0;-0.0;0.0", Width = 140 };
            box.ValueChanged += (_, e) =>
            {
                if (e.NewValue is { } v)
                {
                    changed((double)v);
                }
            };
            return box;
        }
        Row(panel, "Pre-amp for tagged files (dB)", Db(rg.PreampTaggedDb, v => Update(r => r.PreampTaggedDb = v)));
        Row(panel, "Pre-amp for untagged files (dB)", Db(rg.PreampUntaggedDb, v => Update(r => r.PreampUntaggedDb = v)),
            "Used for files without tags while they are analysed (or when analysis is off), and added to analysed gains.");
        Check(panel, "Analyse untagged files (EBU R128)", rg.AnalyseUntagged, v => Update(r => r.AnalyseUntagged = v));
        Check(panel, "Prevent clipping (limit the gain to the peak)", rg.PreventClipping, v => Update(r => r.PreventClipping = v));
        Check(panel, "Write analysed values to the files' tags", rg.WriteTags, v => Update(r => r.WriteTags = v));
        var playback = _settings.Current.Playback;
        void P(Action<PlaybackSettings> change) => _settings.Update(SettingsGroup.Playback, s => change(s.Playback));

        Heading(panel, "Gapless and fades");
        Check(panel, "Gapless playback (the next track starts exactly where this one ends)", playback.Gapless, v => P(p => p.Gapless = v));
        FadeRow(panel, "Fade on pause / resume", playback.Fades.OnPause, playback.Fades.PauseMilliseconds,
            v => P(p => p.Fades.OnPause = v), ms => P(p => p.Fades.PauseMilliseconds = ms));
        FadeRow(panel, "Fade on stop", playback.Fades.OnStop, playback.Fades.StopMilliseconds,
            v => P(p => p.Fades.OnStop = v), ms => P(p => p.Fades.StopMilliseconds = ms));
        FadeRow(panel, "Fade on seek", playback.Fades.OnSeek, playback.Fades.SeekMilliseconds,
            v => P(p => p.Fades.OnSeek = v), ms => P(p => p.Fades.SeekMilliseconds = ms));

        Heading(panel, "Speed");
        Check(panel, "Remember the speed per file", playback.RememberSpeedPerFile, v => P(p => p.RememberSpeedPerFile = v));
        Note(panel, "Set the speed in Main menu › Playback › Speed… (0.5–2.0×, with or without keeping the pitch).");

        Heading(panel, "Resume");
        var resume = new ComboBox { ItemsSource = new[] { "Off", "Reopen the last track paused", "Reopen the last track and play" }, SelectedIndex = (int)playback.ResumeSession, Width = 320 };
        resume.SelectionChanged += (_, _) => P(p => p.ResumeSession = (ResumeMode)Math.Max(0, resume.SelectedIndex));
        Row(panel, "On start", resume);
        var minutes = new NumericUpDown { Minimum = 1, Maximum = 600, Value = playback.ResumeMinimumMinutes, FormatString = "0", Width = 140 };
        minutes.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } v)
            {
                P(p => p.ResumeMinimumMinutes = (int)v);
            }
        };
        Check(panel, "Remember the position in long files (audiobooks, mixes)", playback.ResumeLongFiles, v => P(p => p.ResumeLongFiles = v));
        Row(panel, "Long file: at least (minutes)", minutes, "Such files start where you left them; '»' marks them in the playlist. Reset it from the playlist's right-click menu.");

        Heading(panel, "Queue and sleep timer");
        Check(panel, "Clear the play-next queue on Stop", playback.ClearQueueOnStop, v => P(p => p.ClearQueueOnStop = v));
        Check(panel, "Sleep timer: fade out before stopping", playback.SleepTimer.FadeOut, v => P(p => p.SleepTimer.FadeOut = v));
        var fadeSeconds = new NumericUpDown { Minimum = 1, Maximum = 300, Value = playback.SleepTimer.FadeSeconds, FormatString = "0", Width = 140 };
        fadeSeconds.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } v)
            {
                P(p => p.SleepTimer.FadeSeconds = (int)v);
            }
        };
        Row(panel, "Sleep timer fade (seconds)", fadeSeconds);
        Check(panel, "Sleep timer: close AudioTastic afterwards", playback.SleepTimer.CloseApp, v => P(p => p.SleepTimer.CloseApp = v));
        Note(panel, "Shuffle and repeat are switched in the main window (S, R) and are remembered.");
    }

    private static void Heading(StackPanel panel, string text) =>
        panel.Children.Add(new TextBlock { Text = text, FontWeight = FontWeight.Bold, FontSize = 14, Margin = new Thickness(0, 10, 0, 0) });

    private static void FadeRow(StackPanel panel, string label, bool on, int milliseconds, Action<bool> changedOn, Action<int> changedMs)
    {
        var box = new CheckBox { Content = label, IsChecked = on, VerticalAlignment = VerticalAlignment.Center, MinWidth = 220 };
        var ms = new NumericUpDown { Minimum = 10, Maximum = 2000, Increment = 10, Value = milliseconds, FormatString = "0", Width = 130, IsEnabled = on };
        Avalonia.Automation.AutomationProperties.SetName(ms, label + " (ms)");
        box.IsCheckedChanged += (_, _) =>
        {
            changedOn(box.IsChecked == true);
            ms.IsEnabled = box.IsChecked == true;
        };
        ms.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } v)
            {
                changedMs((int)v);
            }
        };
        panel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children = { box, ms, new TextBlock { Text = "ms", VerticalAlignment = VerticalAlignment.Center } },
        });
    }

    private void OutputPage(StackPanel panel)
    {
        var output = _settings.Current.Output;
        var devices = new List<string> { "System default" };
        try
        {
            devices.AddRange(_shell.Engine.GetOutputDevices().Select(d => d.Name));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Could not list output devices");
        }
        var device = new ComboBox { ItemsSource = devices, MinWidth = 320 };
        device.SelectedIndex = output.DeviceName is { } name && devices.IndexOf(name) is var i and > 0 ? i : 0;
        device.SelectionChanged += (_, _) =>
        {
            var chosen = device.SelectedIndex <= 0 ? null : devices[device.SelectedIndex];
            _ = _shell.Engine.SetOutputDeviceAsync(chosen);
        };
        Row(panel, "Device", device, "Output plugins (Settings › Plugins) add their devices here as 'Plugin: device'. Right-click the volume slider for a quick device menu.");

        void O(Action<OutputSettings> change) => _settings.Update(SettingsGroup.Output, s => change(s.Output));
        var mode = new ComboBox { ItemsSource = new[] { "Shared (mixed with other apps)", "Exclusive (bit-perfect)" }, SelectedIndex = output.Exclusive ? 1 : 0, Width = 320 };
        mode.SelectionChanged += (_, _) => O(o => o.Exclusive = mode.SelectedIndex == 1);
        Row(panel, "Mode", mode,
            "Exclusive mode takes over the device: other apps are silent, and at 100 % volume with ReplayGain, speed and fades " +
            "off the samples reach the device unchanged. If the device refuses, shared mode is used. Applies from the next track.");
        var depth = new ComboBox { ItemsSource = new[] { "Auto (the file's bit depth)", "16 bit", "24 bit", "32 bit" }, Width = 320 };
        depth.SelectedIndex = output.BitDepth switch { 16 => 1, 24 => 2, 32 => 3, _ => 0 };
        depth.SelectionChanged += (_, _) => O(o => o.BitDepth = depth.SelectedIndex switch { 1 => 16, 2 => 24, 3 => 32, _ => 0 });
        Row(panel, "Bit depth (exclusive)", depth);
        Check(panel, "Match the output rate to each file (no resampling where the device allows)", output.MatchOutputRate, v => O(o => o.MatchOutputRate = v));
        Check(panel, "Allow resampling when the device cannot play a file's rate", output.AllowResampling, v => O(o => o.AllowResampling = v));
        Check(panel, "Dither when reducing bit depth", output.Dither, v => O(o => o.Dither = v));
        var dsd = new ComboBox { ItemsSource = new[] { "Convert to PCM", "DoP (DSD over PCM, exclusive mode)" }, SelectedIndex = (int)output.DsdMode, Width = 320 };
        dsd.SelectionChanged += (_, _) => O(o => o.DsdMode = (DsdMode)Math.Max(0, dsd.SelectedIndex));
        Row(panel, "DSD files", dsd, "DoP needs a DAC that understands it and exclusive mode; otherwise DSD is converted to PCM. 'OUT' in the main window means the device runs at another rate than the file.");

        var buffer = new NumericUpDown { Minimum = 10, Maximum = 500, Increment = 5, Value = output.BufferMilliseconds, FormatString = "0", Width = 140 };
        buffer.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } v)
            {
                _settings.Update(SettingsGroup.Output, s => s.Output.BufferMilliseconds = (int)v);
            }
        };
        Row(panel, "Buffer (ms)", buffer, "Used the next time the output opens (new sample rate or device).");
    }

    private void InterfacePage(StackPanel panel)
    {
        var ui = _settings.Current.Interface;

        var skins = _shell.Skins.GetInstalledSkins();
        var list = new ListBox
        {
            ItemsSource = skins.Select(s => $"{s.Name}{(s.Author is null ? "" : $" — {s.Author}")}{(s.Version is null ? "" : $" ({s.Version})")}").ToArray(),
            SelectedIndex = Math.Max(0, skins.ToList().FindIndex(s => s.Id == _shell.Skins.Current.Id)),
            MaxHeight = 160,
        };
        Avalonia.Automation.AutomationProperties.SetName(list, "Skins");
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex >= 0 && skins[list.SelectedIndex].Id != _shell.Skins.Current.Id)
            {
                _shell.ApplySkin(skins[list.SelectedIndex].Id, this);
            }
        };
        var install = new Button { Content = "Install skin…" };
        install.Click += async (_, _) =>
        {
            await _shell.InstallSkinAsync(this);
            ShowPage();
        };
        var folder = new Button { Content = "Open skins folder" };
        folder.Click += (_, _) => _shell.OpenSkinsFolder();
        Row(panel, "Skin", new StackPanel
        {
            Spacing = 6,
            Children = { list, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { install, folder } } },
        }, "Skins can be switched while playing. See docs/skins/skin-format.md to make your own.");

        int[] sizes = [100, 125, 150, 175, 200];
        var size = new ComboBox { ItemsSource = sizes.Select(s => $"{s} %").ToArray(), Width = 140 };
        size.SelectedIndex = Math.Max(0, Array.IndexOf(sizes, ui.PlayerScale));
        size.SelectionChanged += (_, _) => _settings.Update(SettingsGroup.Interface, s => s.Interface.PlayerScale = sizes[Math.Max(0, size.SelectedIndex)]);
        Row(panel, "Player size", size, "Size of the main window and mini player. Double size doubles it.");

        Check(panel, "Double size (Ctrl+D)", ui.DoubleSize, v => _settings.Update(SettingsGroup.Interface, s => s.Interface.DoubleSize = v));
        Check(panel, "Main window always on top", ui.AlwaysOnTop, v => _settings.Update(SettingsGroup.Interface, s => s.Interface.AlwaysOnTop = v));
        Check(panel, "Mini player always on top", ui.MiniPlayerAlwaysOnTop, v => _settings.Update(SettingsGroup.Interface, s => s.Interface.MiniPlayerAlwaysOnTop = v));
        Check(panel, "Scroll the title when it does not fit", ui.ScrollTitle, v => _settings.Update(SettingsGroup.Interface, s => s.Interface.ScrollTitle = v));
        if (_shell.Notifications.IsSupported)
        {
            Check(panel, "Show a notification when the track changes (not while AudioTastic is in front)", ui.NotifyOnTrackChange,
                v => _settings.Update(SettingsGroup.Interface, s => s.Interface.NotifyOnTrackChange = v));
            if (_paths.IsPortable)
            {
                Note(panel, "Portable mode: Windows shows notifications only for programs with a Start menu shortcut (Settings › System integration › Start menu).");
            }
        }

        var speed = new NumericUpDown { Minimum = 0, Maximum = 200, Increment = 5, Value = ui.ScrollSpeed, FormatString = "0", Width = 140 };
        speed.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } v)
            {
                _settings.Update(SettingsGroup.Interface, s => s.Interface.ScrollSpeed = (int)v);
            }
        };
        Row(panel, "Scroll speed (px/s)", speed, "0 uses the skin's speed.");

        var theme = new ComboBox { ItemsSource = new[] { "Follow the system", "Dark", "Light" }, SelectedIndex = (int)ui.DialogTheme, Width = 200 };
        theme.SelectionChanged += (_, _) => _settings.Update(SettingsGroup.Interface, s => s.Interface.DialogTheme = (DialogTheme)Math.Max(0, theme.SelectedIndex));
        Row(panel, "Dialogs theme", theme, "Settings and dialogs. Player windows use the skin.");

        // UI-07: the visualisation in the main window (MW6).
        var vis = new ComboBox { ItemsSource = new[] { "Spectrum analyser", "Oscilloscope", "Off" }, SelectedIndex = (int)ui.Visualisation, Width = 200 };
        vis.SelectionChanged += (_, _) => _settings.Update(SettingsGroup.Interface, s => s.Interface.Visualisation = (VisualisationType)Math.Max(0, vis.SelectedIndex));
        Row(panel, "Visualisation", vis, "Clicking the visualisation in the main window also switches it. Plugin visualisations: Main menu › Visualisation (Ctrl+Shift+K).");
        Row(panel, "Spectrum bars", Number(ui.SpectrumBars, 8, 64, 1, v => _settings.Update(SettingsGroup.Interface, s => s.Interface.SpectrumBars = (int)v)));
        Row(panel, "Peak fall speed (heights per second)", Number(ui.PeakFallSpeed, 0.1, 5, 0.1m, v => _settings.Update(SettingsGroup.Interface, s => s.Interface.PeakFallSpeed = v), "0.0"));
        Row(panel, "Frame rate (fps)", Number(ui.VisualisationFps, 15, 60, 5, v => _settings.Update(SettingsGroup.Interface, s => s.Interface.VisualisationFps = (int)v)));
    }

    private static NumericUpDown Number(double value, double min, double max, decimal step, Action<double> changed, string format = "0")
    {
        var box = new NumericUpDown { Minimum = (decimal)min, Maximum = (decimal)max, Increment = step, Value = (decimal)value, FormatString = format, Width = 140 };
        box.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } v)
            {
                changed((double)v);
            }
        };
        return box;
    }

    /// <summary>XF-01..XF-04 (Required Features 4.4): Intro/Outro and beat matching.</summary>
    private void CrossfadingPage(StackPanel panel)
    {
        var xf = _settings.Current.Crossfading;
        void X(Action<CrossfadingSettings> change) => _settings.Update(SettingsGroup.Crossfading, s => change(s.Crossfading));

        Check(panel, "Crossfade between tracks (the XF button in the main window)", xf.Enabled, v => X(c => c.Enabled = v));
        var mode = new ComboBox
        {
            ItemsSource = new[] { "Intro/Outro: the tracks fade into each other", "Beat matching: tempo and beats matched, like a DJ" },
            SelectedIndex = (int)xf.Mode,
            Width = 360,
        };
        mode.SelectionChanged += (_, _) => X(c => c.Mode = (CrossfadeMode)Math.Max(0, mode.SelectedIndex));
        Row(panel, "Mode", mode, "The main window shows IO or BM while crossfading is on.");
        Row(panel, "Overlap (seconds)", Number(xf.OverlapSeconds, 0.5, 30, 0.5m, v => X(c => c.OverlapSeconds = v), "0.0"),
            "Intro/Outro: how long the tracks overlap; at the end the old track is silent and the new one at full volume. " +
            "Beat matching: how long both play at full volume (after the fade-in) before the first track is cut or faded out.");
        Check(panel, "Bring the new track's bass in gradually (from low to normal over its fade-in)", xf.BassFadeIn, v => X(c => c.BassFadeIn = v));

        Heading(panel, "Beat matching");
        Row(panel, "Start mixing before the beat ends (seconds)", Number(xf.MixBeforeBeatEndSeconds, 0, 180, 1, v => X(c => c.MixBeforeBeatEndSeconds = v)),
            "The beat end is where the drums of the playing track stop (its outro is not counted). The mix starts on the phrase boundary (8 beats) at or before this point.");
        Row(panel, "Mix the new track in after its beat starts (seconds)", Number(xf.MixInAfterBeatStartSeconds, 0, 180, 1, v => X(c => c.MixInAfterBeatStartSeconds = v)),
            "The beat start is where the new track's drums come in (its intro is skipped). It enters on the phrase boundary at or after this point.");
        Row(panel, "Fade-in (seconds)", Number(xf.FadeInSeconds, 0.5, 30, 0.5m, v => X(c => c.FadeInSeconds = v), "0.0"));
        var end = new ComboBox { ItemsSource = new[] { "Cut: the first track stops at once", "Fade out" }, SelectedIndex = (int)xf.EndBehaviour, Width = 360 };
        end.SelectionChanged += (_, _) => X(c => c.EndBehaviour = (CrossfadeEnd)Math.Max(0, end.SelectedIndex));
        Row(panel, "When the overlap ends", end);
        Row(panel, "Fade-out (seconds)", Number(xf.FadeOutSeconds, 0.1, 60, 0.5m, v => X(c => c.FadeOutSeconds = v), "0.0"), "Only with Fade out.");
        Row(panel, "Tempo limit (%)", Number(xf.TempoLimitPercent, 1, 20, 1, v => X(c => c.TempoLimitPercent = v)),
            "The largest tempo change (half and double time are tried too). Tracks further apart, or without a reliable beat analysis, use Intro/Outro.");
        var tempoReturn = new ComboBox { ItemsSource = new[] { "Cut back at the next break", "Gradually" }, SelectedIndex = (int)xf.TempoReturn, Width = 360 };
        tempoReturn.SelectionChanged += (_, _) => X(c => c.TempoReturn = (TempoReturnMode)Math.Max(0, tempoReturn.SelectedIndex));
        Row(panel, "Back to the new track's own tempo", tempoReturn, "After the first track has ended. Without a break later in the track, it returns gradually.");
        Row(panel, "Tempo return (seconds)", Number(xf.TempoReturnSeconds, 1, 120, 1, v => X(c => c.TempoReturnSeconds = v)), "Only with Gradually.");
        Check(panel, "Analyse BPM, beats and breaks of the playlist in the background", xf.AnalyseInBackground, v => X(c => c.AnalyseInBackground = v));
    }

    /// <summary>EQ-01..EQ-05: equalizer and DSP chain shortcuts, and the auto EQ rules (EQ-03).</summary>
    private void EqualizerDspPage(StackPanel panel)
    {
        var dsp = _settings.Current.EqualizerDsp;
        var equalizer = new Button { Content = "Open equalizer (Alt+G)" };
        equalizer.Click += (_, _) => _shell.ShowEqualizer();
        var chain = new Button { Content = "DSP chain…" };
        chain.Click += (_, _) => _shell.Equalizer.OpenDspChain(this);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { equalizer, chain } });
        Note(panel, "The DSP chain runs the equalizer, compressor, stereo widening, reverb, limiter, VST3 effects and plugin effects in the order you set, before the volume. " +
                    "Add VST3 effects in the DSP chain window.");

        Vst3Section(panel);

        Heading(panel, "Auto EQ");
        Check(panel, "Load a preset automatically when a track starts (AUTO in the equalizer)", dsp.AutoEq,
            v => _settings.Update(SettingsGroup.EqualizerDsp, s => s.EqualizerDsp.AutoEq = v));
        Note(panel, "A preset assigned to the file wins over one assigned to its genre. Assign them from the equalizer's PRESETS menu " +
                    "while the track plays, or add genre rules here.");
        var rules = new StackPanel { Spacing = 4 };
        void ShowRules()
        {
            rules.Children.Clear();
            foreach (var (genre, preset) in _settings.Current.EqualizerDsp.GenrePresets.OrderBy(p => p.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                var remove = new Button { Content = "Remove", Padding = new Thickness(8, 2) };
                Avalonia.Automation.AutomationProperties.SetName(remove, $"Remove the rule for {genre}");
                var key = genre;
                remove.Click += (_, _) =>
                {
                    _settings.Update(SettingsGroup.EqualizerDsp, s => s.EqualizerDsp.GenrePresets.Remove(key));
                    ShowRules();
                };
                rules.Children.Add(new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = $"{genre}  →  {preset}", Width = 260, VerticalAlignment = VerticalAlignment.Center },
                        remove,
                    },
                });
            }
            if (rules.Children.Count == 0)
            {
                rules.Children.Add(new TextBlock { Text = "No genre rules yet.", Foreground = Brushes.Gray });
            }
        }
        ShowRules();
        Row(panel, "Genre rules", rules);
        var genreBox = new TextBox { PlaceholderText = "Genre, e.g. Rock", Width = 160 };
        Avalonia.Automation.AutomationProperties.SetName(genreBox, "Genre");
        var presetBox = new ComboBox { ItemsSource = _shell.Equalizer.Presets.All.Select(p => p.Name).ToList(), SelectedIndex = 0, Width = 180 };
        Avalonia.Automation.AutomationProperties.SetName(presetBox, "Preset");
        var add = new Button { Content = "Add rule" };
        add.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(genreBox.Text) || presetBox.SelectedItem is not string preset)
            {
                return;
            }
            var genre = genreBox.Text.Trim();
            _settings.Update(SettingsGroup.EqualizerDsp, s =>
            {
                foreach (var key in s.EqualizerDsp.GenrePresets.Keys.Where(k => string.Equals(k, genre, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    s.EqualizerDsp.GenrePresets.Remove(key);
                }
                s.EqualizerDsp.GenrePresets[genre] = preset;
            });
            genreBox.Text = string.Empty;
            ShowRules();
        };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { genreBox, presetBox, add } });
    }

    /// <summary>EQ-06: where VST3 effects are found, a rescan, and the modules that are blacklisted.</summary>
    private void Vst3Section(StackPanel panel)
    {
        Heading(panel, "VST3 effects");
        var library = _shell.Equalizer.Vst3;
        if (!library.IsAvailable)
        {
            Note(panel, "VST3 is not available: the VST3 host library is missing from this installation.");
            return;
        }
        var section = new StackPanel { Spacing = 8 };
        panel.Children.Add(section);
        void Show()
        {
            section.Children.Clear();
            Note(section, "Searched: " + string.Join("; ", AudioTastic.Core.Audio.Dsp.Vst3.Vst3Scanner.DefaultFolders()) + ", and the folders below.");
            foreach (var folder in _settings.Current.EqualizerDsp.Vst3Folders)
            {
                var remove = new Button { Content = "Remove", Tag = folder };
                remove.Click += (_, _) =>
                {
                    _settings.Update(SettingsGroup.EqualizerDsp, s => s.EqualizerDsp.Vst3Folders.Remove(folder));
                    Show();
                };
                section.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = folder, VerticalAlignment = VerticalAlignment.Center }, remove } });
            }
            var add = new Button { Content = "Add folder…" };
            add.Click += async (_, _) =>
            {
                var picked = await StorageProvider.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions { Title = "Folder with VST3 effects" });
                if (picked.Count > 0 && Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(picked[0]) is { } path)
                {
                    _settings.Update(SettingsGroup.EqualizerDsp, s =>
                    {
                        if (!s.EqualizerDsp.Vst3Folders.Contains(path, StringComparer.OrdinalIgnoreCase))
                        {
                            s.EqualizerDsp.Vst3Folders.Add(path);
                        }
                    });
                    Show();
                }
            };
            var status = new TextBlock { Foreground = Brushes.Gray, FontSize = 12, VerticalAlignment = VerticalAlignment.Center };
            var rescan = new Button { Content = "Rescan all" };
            rescan.Click += async (_, _) =>
            {
                rescan.IsEnabled = false;
                await library.RescanAsync(full: true, new Progress<string>(name => status.Text = $"Scanning {name}…"));
                Show();
            };
            section.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { add, rescan, status } });
            Note(section, $"{library.Effects.Count} effect(s) found." + (library.HasScanned ? string.Empty : " Not scanned yet: Rescan, or open DSP chain › Add VST3 effect…"));
            foreach (var module in library.Blacklisted)
            {
                Note(section, $"Blacklisted: {module.Path} ({module.Problem}). Rescan all tries it again.");
            }
        }
        Show();
    }

    private void GeneralPage(StackPanel panel)
    {
        var general = _settings.Current.General;
        var level = new ComboBox { ItemsSource = Enum.GetNames<LogLevelSetting>(), SelectedIndex = (int)general.LogLevel, Width = 200 };
        level.SelectionChanged += (_, _) => _settings.Update(SettingsGroup.General, s => s.General.LogLevel = (LogLevelSetting)Math.Max(0, level.SelectedIndex));
        Row(panel, "Log level", level, "Log files are in the logs folder of the data folder and are kept for 7 days.");

        var threads = new NumericUpDown { Minimum = 1, Maximum = 8, Increment = 1, Value = general.AnalysisThreads, FormatString = "0", Width = 140 };
        threads.ValueChanged += (_, e) =>
        {
            if (e.NewValue is { } v)
            {
                _settings.Update(SettingsGroup.General, s => s.General.AnalysisThreads = (int)v);
            }
        };
        Row(panel, "Analysis threads", threads,
            "Background analysis of waveforms, loudness and beats runs at low priority on this many threads.");

        var open = new Button { Content = "Open data folder" };
        open.Click += (_, _) => _platform.OpenFolder(_paths.Root);
        Row(panel, "Data folder", new StackPanel { Spacing = 6, Children = { new SelectableTextBlock { Text = _paths.Root }, open } },
            _paths.IsPortable
                ? "Portable mode is on: settings are stored next to the program."
                : "Portable mode: start with --portable, or put an empty file named 'portable' next to the program.");

        // TL-02: settings export and import.
        var export = new Button { Content = "Export settings…" };
        var import = new Button { Content = "Import settings…" };
        var status = new TextBlock { Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 480 };
        export.Click += async (_, _) =>
        {
            var file = await StorageProvider.SaveFilePickerAsync(new Avalonia.Platform.Storage.FilePickerSaveOptions
            {
                Title = "Export settings",
                SuggestedFileName = $"AudioTastic settings {DateTime.Now:yyyy-MM-dd}{AudioTastic.Data.Backup.BackupService.Extension}",
                DefaultExtension = AudioTastic.Data.Backup.BackupService.Extension,
                FileTypeChoices = [new Avalonia.Platform.Storage.FilePickerFileType("AudioTastic backup") { Patterns = ["*" + AudioTastic.Data.Backup.BackupService.Extension] }],
            });
            if (file?.TryGetLocalPath() is not { } path)
            {
                return;
            }
            _shell.SaveForBackup();
            var version = typeof(SettingsWindow).Assembly.GetCustomAttribute<System.Reflection.AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            try
            {
                await Task.Run(() => AudioTastic.Data.Backup.BackupService.Export(_paths, path, version.Split('+')[0]));
                status.Text = $"Exported to {path}.";
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                status.Text = $"Could not export: {ex.Message}";
            }
        };
        import.Click += async (_, _) =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Import settings",
                FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("AudioTastic backup") { Patterns = ["*" + AudioTastic.Data.Backup.BackupService.Extension] }],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            {
                return;
            }
            try
            {
                var info = AudioTastic.Data.Backup.BackupService.Read(path);
                if (await ImportBackupWindow.AskAsync(this, info) is not { } selection)
                {
                    return;
                }
                AudioTastic.Data.Backup.BackupService.Stage(_paths, path, selection);
                _shell.Restart();
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                status.Text = ex.Message;
            }
        };
        Row(panel, "Settings backup", new StackPanel { Spacing = 6, Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { export, import } }, status } },
            "Export writes your settings, playlists, equalizer presets, DSP chain, hotkeys and installed skins to one .atbackup file. " +
            "Import lets you choose what to bring in, then restarts AudioTastic; what it replaces is first saved to the backups folder in the data folder.");
    }

    private static void Note(StackPanel panel, string text) =>
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Left });

    private static void Row(StackPanel panel, string label, Control control, string? help = null)
    {
        var stack = new StackPanel { Spacing = 4 };
        stack.Children.Add(new TextBlock { Text = label, FontWeight = FontWeight.SemiBold });
        Avalonia.Automation.AutomationProperties.SetName(control, label);
        stack.Children.Add(control);
        if (help is not null)
        {
            stack.Children.Add(new TextBlock { Text = help, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12, MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Left });
        }
        panel.Children.Add(stack);
    }

    private static void Check(StackPanel panel, string label, bool value, Action<bool> changed)
    {
        var box = new CheckBox { Content = label, IsChecked = value };
        box.IsCheckedChanged += (_, _) => changed(box.IsChecked == true);
        panel.Children.Add(box);
    }
}
