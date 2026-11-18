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
                Note(panel, "The equalizer, presets and DSP chain arrive in phase P7; VST3 and room correction in P12.");
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
        Note(panel, "Gapless playback, fades, speed, resume, stop after current and the sleep timer arrive in phase P6. " +
                    "Shuffle and repeat are switched in the main window (S, R) and are remembered.");
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
        Row(panel, "Device", device, "Shared mode. Exclusive / bit-perfect output arrives in phase P6. Right-click the volume slider for a quick device menu.");

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
