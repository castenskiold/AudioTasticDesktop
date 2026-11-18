using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AudioTastic.App.Skins;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Platform;
using AudioTastic.Plugins.Host;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Ui;

/// <summary>
/// UI-10 part 2: the General settings window. Ten groups on the left, the selected group's options on the right.
/// Changes apply immediately (SettingsStore); every group has "Reset to defaults". Pages of later phases say
/// which phase fills them. Follows the dialog theme (UI-09).
/// </summary>
public sealed class SettingsWindow : Window
{
    private static readonly (SettingsGroup Group, string Title)[] Groups =
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
                Note(panel, "DJ-style crossfading (Intro/Outro and beat matching) arrives in phase P8.");
                break;
            case SettingsGroup.Metadata:
                Note(panel, "Album art, online lookup and lyrics arrive in phase P9.");
                break;
            case SettingsGroup.CastingNetwork:
                Note(panel, "Casting, streaming server, remote control, scrobbling and Discord arrive in phase P11.");
                break;
            case SettingsGroup.Interface:
                InterfacePage(panel);
                break;
            case SettingsGroup.SystemIntegration:
                Note(panel, "Hotkeys, tray, file associations and the context menu arrive in phase P10.");
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

    /// <summary>EQ-01..EQ-05: equalizer and DSP chain shortcuts, and the auto EQ rules (EQ-03).</summary>
    private void EqualizerDspPage(StackPanel panel)
    {
        var dsp = _settings.Current.EqualizerDsp;
        var equalizer = new Button { Content = "Open equalizer (Alt+G)" };
        equalizer.Click += (_, _) => _shell.ShowEqualizer();
        var chain = new Button { Content = "DSP chain…" };
        chain.Click += (_, _) => _shell.Equalizer.OpenDspChain(this);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { equalizer, chain } });
        Note(panel, "The DSP chain runs the equalizer, compressor, stereo widening, reverb, limiter and plugin effects in the order you set, before the volume. " +
                    "VST3 effects and room correction come in a later phase.");

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
            "Background analysis of waveforms and loudness (and BPM later) runs at low priority on this many threads.");

        var open = new Button { Content = "Open data folder" };
        open.Click += (_, _) => _platform.OpenFolder(_paths.Root);
        Row(panel, "Data folder", new StackPanel { Spacing = 6, Children = { new SelectableTextBlock { Text = _paths.Root }, open } },
            _paths.IsPortable
                ? "Portable mode is on: settings are stored next to the program."
                : "Portable mode: start with --portable, or put an empty file named 'portable' next to the program. Settings import/export arrives in phase P13.");
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
