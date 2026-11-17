using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using AudioTastic.App.Skins;
using AudioTastic.App.Ui.Playlist;
using AudioTastic.Core.Diagnostics;
using AudioTastic.Core.Playback;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Platform;
using AudioTastic.Plugins.Host;
using AudioTastic.Sdk.Plugins;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Ui;

/// <summary>
/// The player UI: owns the skinned main window and mini player, runs every named action (buttons, menu, keys),
/// builds the main menu, and handles skins (live switching), double size, always on top, window positions,
/// snapping and the dialogs (settings, about, crash report). UI thread only.
/// </summary>
public sealed class PlayerShell : ISkinHost, IDisposable
{
    private readonly PlaybackEngine _engine;
    private readonly SettingsStore _settings;
    private readonly SkinManager _skins;
    private readonly CrashReporter _crashReporter;
    private readonly IPlatformServices _platform;
    private readonly StartupOptions _options;
    private readonly AppPaths _paths;
    private readonly ILogger<PlayerShell> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly WindowSnapManager _snap = new();
    private IClassicDesktopStyleApplicationLifetime? _desktop;
    private SkinnedWindow? _main;
    private SkinnedWindow? _mini;
    private SkinnedWindow? _playlist;
    private bool _playlistBeforeMini;
    private readonly PlaylistController _playlists;
    private readonly PluginHost _plugins;
    private Skin? _fallbackSkin;
    private SettingsWindow? _settingsWindow;
    private bool _exiting;

    public PlayerShell(PlaybackEngine engine, SettingsStore settings, SkinManager skins, CrashReporter crashReporter,
        IPlatformServices platform, StartupOptions options, AppPaths paths, PlaylistController playlists, PluginHost plugins,
        ILoggerFactory loggerFactory)
    {
        _playlists = playlists;
        _plugins = plugins;
        _engine = engine;
        _settings = settings;
        _skins = skins;
        _crashReporter = crashReporter;
        _platform = platform;
        _options = options;
        _paths = paths;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PlayerShell>();
        Player = new PlayerViewModel(engine, settings);
        _plugins.PluginDisabled += OnPluginDisabled;
    }

    /// <summary>PG-03: a plugin that kept failing was switched off; say so in the main window.</summary>
    private void OnPluginDisabled(object? sender, PluginDisabledEventArgs e) =>
        Dispatcher.UIThread.Post(() => Player.ShowMessage($"Plugin '{e.Plugin.Manifest.Name}' was disabled after repeated errors (see Settings › Plugins)."));

    public PlayerViewModel Player { get; }
    public InterfaceSettings UiSettings => _settings.Current.Interface;
    public SkinManager Skins => _skins;
    public PlaybackEngine Engine => _engine;

    /// <summary>UI-08: drawing scale of the player windows (player size × double size).</summary>
    public double Scale => Math.Clamp(UiSettings.PlayerScale, 50, 400) / 100.0 * (UiSettings.DoubleSize ? 2 : 1);

    /// <summary>Creates and shows the player windows.</summary>
    public Window Start(IClassicDesktopStyleApplicationLifetime desktop)
    {
        _desktop = desktop;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        ApplyDialogTheme();

        _main = new SkinnedWindow("main", this);
        _mini = new SkinnedWindow("mini", this);
        _playlist = new SkinnedWindow("playlist", this) { ShowInTaskbar = false };
        _snap.Register(_main, isMain: true);
        _snap.Register(_mini);
        _snap.Register(_playlist);
        _snap.DragCompleted += (_, _) => SavePositions();
        BuildWindows();
        Restore(_main, UiSettings.MainWindow);
        Restore(_mini, UiSettings.MiniPlayerWindow);
        Restore(_playlist, UiSettings.PlaylistWindow);
        if (UiSettings.PlaylistSize is { } size)
        {
            _playlist.Width = size.Width * Scale;
            _playlist.Height = size.Height * Scale;
        }
        _playlist.Closing += (_, e) =>
        {
            if (!_exiting)
            {
                e.Cancel = true; // Alt+F4 on the playlist hides it
                SetPlaylistVisible(false);
            }
        };
        ApplyTopmost();

        foreach (var window in new[] { _main, _mini })
        {
            window.Closing += (_, e) =>
            {
                if (!_exiting)
                {
                    e.Cancel = true; // Alt+F4 on a player window exits the app cleanly
                    Exit();
                }
            };
        }
        _skins.SkinChanged += (_, _) => BuildWindows();
        _settings.Changed += OnSettingsChanged;
        _main.Opened += (_, _) => OnFirstShown();

        desktop.MainWindow = _main;
        if (UiSettings.MiniPlayerActive)
        {
            _mini.Show();
            _mini.Opened += (_, _) => OnFirstShown();
        }
        else
        {
            _main.Show();
        }
        return _main;
    }

    // ---- ISkinHost ------------------------------------------------------------------------------------

    public void Execute(string action, Control source)
    {
        var window = TopLevel.GetTopLevel(source) as Window ?? _main!;
        var player = Player.Player;
        switch (action)
        {
            case "previous": Run(player.PreviousAsync); break;
            case "play": Run(player.PlayAsync); break;
            case "pause": Run(player.PauseAsync); break;
            case "playPause": Run(player.State == PlaybackState.Playing ? player.PauseAsync : player.PlayAsync); break;
            case "stop": Run(player.StopAsync); break;
            case "next": Run(player.NextAsync); break;
            case "shuffle": player.Shuffle = !player.Shuffle; break;
            case "repeat": player.Repeat = (RepeatMode)(((int)player.Repeat + 1) % 3); break;
            case "volumeUp": Player.Volume += 5; break;
            case "volumeDown": Player.Volume -= 5; break;
            case "mute": Player.Muted = !Player.Muted; break;
            case "seekForward": Run(() => player.SeekAsync(player.Position + TimeSpan.FromSeconds(5))); break;
            case "seekBack": Run(() => player.SeekAsync(player.Position - TimeSpan.FromSeconds(5))); break;
            case "open": _ = OpenFilesAsync(window); break;
            case "openUrl": _ = OpenUrlAsync(window); break;
            case "playlist": SetPlaylistVisible(!_playlist!.IsVisible); break;
            case "hide" when window == _playlist: SetPlaylistVisible(false); break;
            case "search":
                SetPlaylistVisible(true);
                _playlists.FocusSearch();
                break;
            case "plAdd": _playlists.ShowAddMenu(source); break;
            case "plRemove": _playlists.ShowRemoveMenu(source); break;
            case "plSelect": _playlists.ShowSelectMenu(source); break;
            case "plSort": _playlists.ShowSortMenu(source); break;
            case "plList": _playlists.ShowListMenu(source); break;
            case "menu": ShowMainMenu(source); break;
            case "minimize": window.WindowState = WindowState.Minimized; break;
            case "close": case "exit": Exit(); break;
            case "miniPlayer": case "expand": case "titleDoubleClick": ToggleMiniPlayer(); break;
            case "doubleSize": _settings.Update(SettingsGroup.Interface, s => s.Interface.DoubleSize = !s.Interface.DoubleSize); break;
            case "settings": ShowSettings(); break;
            case "about": ShowAbout(); break;
            default:
                // equalizer, crossfade, cast: windows/features arrive in later phases.
                _logger.LogDebug("Action {Action} is not available yet", action);
                break;
        }
    }

    public void BeginDrag(Control source, PointerPressedEventArgs e) => _snap.BeginDrag(source, e);

    public bool IsAvailable(string bind) => bind is "shuffle" or "repeat" or "mute" or "playlist";

    public Control? CreateElement(Skin skin, SkinElement element) => PlaylistViews.Create(skin, element, _playlists);

    /// <summary>Files dropped on a player window: added to the active playlist; on the main window or mini player they play (PL-04).</summary>
    public void DropFiles(IReadOnlyList<string> paths, Window window) =>
        _ = _playlists.AddAsync(paths, play: window != _playlist);

    public bool IsToggleOn(string bind) => bind == "playlist" && _playlist?.IsVisible == true;

    public void ShowDeviceMenu(Control source)
    {
        var current = _settings.Current.Output.DeviceName;
        var items = new List<MenuItem>
        {
            DeviceItem("System default", null, current is null),
        };
        try
        {
            items.AddRange(_engine.GetOutputDevices().Select(d => DeviceItem(d.IsDefault ? $"{d.Name} (default)" : d.Name, d.Name, d.Name == current)));
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Could not list output devices");
        }
        var menu = new ContextMenu { ItemsSource = items };
        menu.Open(source);

        MenuItem DeviceItem(string label, string? name, bool selected)
        {
            var item = new MenuItem { Header = label, ToggleType = MenuItemToggleType.Radio, IsChecked = selected };
            item.Click += (_, _) => Run(() => _engine.SetOutputDeviceAsync(name));
            return item;
        }
    }

    // ---- windows --------------------------------------------------------------------------------------

    private void BuildWindows()
    {
        var skin = _skins.Current;
        var scale = Scale;
        _main!.Build(skin, skin.Window("main")!, scale);
        if (skin.Window("mini") is { } mini)
        {
            _mini!.Build(skin, mini, scale);
        }
        else
        {
            // A skin without a mini player uses the default skin's (skin-format.md).
            _fallbackSkin ??= _skins.Load(SkinManager.DefaultSkinId);
            _mini!.Build(_fallbackSkin, _fallbackSkin.Window("mini")!, scale);
        }
        if (skin.Window("playlist") is { } playlist)
        {
            _playlist!.Build(skin, playlist, scale);
        }
        else
        {
            _fallbackSkin ??= _skins.Load(SkinManager.DefaultSkinId);
            _playlist!.Build(_fallbackSkin, _fallbackSkin.Window("playlist")!, scale);
        }
    }

    /// <summary>UI-01: shows or hides the playlist window (PL toggle, Alt+E, Windows menu); the choice is saved.</summary>
    private void SetPlaylistVisible(bool visible)
    {
        if (visible == _playlist!.IsVisible)
        {
            return;
        }
        if (visible)
        {
            ShowPlaylist();
        }
        else
        {
            SavePositions();
            _playlist.Hide();
        }
        _settings.Update(SettingsGroup.Interface, s => s.Interface.PlaylistVisible = visible);
        _main!.Refresh();
        _mini!.Refresh();
    }

    /// <summary>
    /// Shows the playlist window owned by the main window, so clicking the app in the taskbar brings both to the front,
    /// and they minimise and restore together (like Winamp's docked windows).
    /// </summary>
    private void ShowPlaylist()
    {
        if (!_playlist!.IsVisible)
        {
            _playlist.Show(_main!);
        }
    }

    /// <summary>First run: dock the playlist window directly below the main window.</summary>
    private void PlacePlaylistBelowMain()
    {
        var bounds = WindowSnapManager.Bounds(_main!);
        _playlist!.WindowStartupLocation = WindowStartupLocation.Manual;
        _playlist.Position = new PixelPoint(bounds.X, bounds.Bottom);
    }

    private void ToggleMiniPlayer()
    {
        var toMini = !_mini!.IsVisible;
        var (show, hide) = toMini ? (_mini, _main!) : (_main!, _mini);
        SavePositions();
        show.Show();
        show.Activate();
        hide.Hide();
        // UI-05: other windows are hidden in the mini player and come back when expanding.
        if (toMini)
        {
            _playlistBeforeMini = _playlist!.IsVisible;
            _playlist.Hide();
        }
        else if (_playlistBeforeMini)
        {
            ShowPlaylist();
        }
        _main!.Refresh();
        _mini.Refresh();
        if (_desktop is not null)
        {
            _desktop.MainWindow = show;
        }
        _settings.Update(SettingsGroup.Interface, s => s.Interface.MiniPlayerActive = toMini);
    }

    private void ApplyTopmost()
    {
        _main!.Topmost = UiSettings.AlwaysOnTop;
        _mini!.Topmost = UiSettings.MiniPlayerAlwaysOnTop;
    }

    private void ApplyDialogTheme()
    {
        if (Application.Current is { } app)
        {
            app.RequestedThemeVariant = UiSettings.DialogTheme switch
            {
                DialogTheme.Dark => ThemeVariant.Dark,
                DialogTheme.Light => ThemeVariant.Light,
                _ => ThemeVariant.Default, // UI-09: follow the OS
            };
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Group != SettingsGroup.Interface)
        {
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            var scale = Scale;
            if (Math.Abs(_main!.Width - (_skins.Current.Window("main")!.Width * scale)) > 0.5)
            {
                BuildWindows(); // double size changed
            }
            ApplyTopmost();
            ApplyDialogTheme();
            _main.Refresh();
            _mini!.Refresh();
            _playlist!.Refresh();
        });
    }

    private static void Restore(Window window, WindowPlacement? placement)
    {
        if (placement is null)
        {
            return;
        }
        var point = new PixelPoint(placement.X, placement.Y);
        // Only restore if the saved position is still on a connected screen.
        if (window.Screens.All.Any(s => s.WorkingArea.Contains(point)))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = point;
        }
    }

    private void SavePositions()
    {
        var main = _main!.Position;
        var mini = _mini!.Position;
        var playlist = _playlist!.Position;
        var playlistSize = new WindowSize(_playlist.Width / Scale, _playlist.Height / Scale);
        var playlistVisible = _playlist.IsVisible;
        var mainKnown = _main.IsVisible || UiSettings.MainWindow is null;
        var miniKnown = _mini.IsVisible || UiSettings.MiniPlayerWindow is null;
        _settings.Update(SettingsGroup.Interface, s =>
        {
            if (mainKnown && _main.IsVisible)
            {
                s.Interface.MainWindow = new WindowPlacement(main.X, main.Y);
            }
            if (miniKnown && _mini.IsVisible)
            {
                s.Interface.MiniPlayerWindow = new WindowPlacement(mini.X, mini.Y);
            }
            if (playlistVisible)
            {
                s.Interface.PlaylistWindow = new WindowPlacement(playlist.X, playlist.Y);
                s.Interface.PlaylistSize = playlistSize;
            }
        });
    }

    private void Exit()
    {
        if (_exiting)
        {
            return;
        }
        _exiting = true;
        SavePositions();
        _settingsWindow?.Close();
        _playlists.Service.Flush();
        _playlist?.Close();
        _main?.Close();
        _mini?.Close();
        _desktop?.Shutdown();
    }

    // ---- start-up -------------------------------------------------------------------------------------

    private bool _firstShownDone;

    private void OnFirstShown()
    {
        if (_firstShownDone)
        {
            return;
        }
        _firstShownDone = true;
        var owner = _main!.IsVisible ? _main : _mini!;
        if (UiSettings.PlaylistWindow is null)
        {
            PlacePlaylistBelowMain();
        }
        if (UiSettings.PlaylistVisible && _main.IsVisible)
        {
            ShowPlaylist();
            _main.Refresh();
        }

        if (_skins.StartupErrors.Count > 0)
        {
            Player.ShowMessage("Skin could not be loaded; using the default skin.");
        }
        if (_crashReporter.GetPendingReport() is { } report)
        {
            Dispatcher.UIThread.Post(async () => await new CrashReportWindow(report, _crashReporter, _platform).ShowDialog(owner));
        }
        if (_options.CrashTest)
        {
            // TL-03 exit criterion: a forced exception must produce a crash report.
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException(
                $"Crash test requested with {StartupOptions.CrashTestSwitch}."));
        }
        if (_options.Files.Count > 0)
        {
            Run(() => _playlists.AddAsync(_options.Files, play: true)); // PL-04: added to the active playlist and played
        }
    }

    // ---- dialogs and menu -----------------------------------------------------------------------------

    private async Task OpenFilesAsync(Window owner)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open audio files",
            AllowMultiple = true,
            FileTypeFilter = [_playlists.OpenableFiles(), FilePickerFileTypes.All],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (paths.Length > 0)
        {
            Run(() => _playlists.AddAsync(paths, play: true)); // CO-02: Open adds the files and plays them
        }
    }

    /// <summary>FM-10: Open URL (Ctrl+L) adds an internet radio stream (or a remote .m3u/.pls) and plays it.</summary>
    private async Task OpenUrlAsync(Window owner)
    {
        var url = await InputWindow.ShowAsync(owner, "Open URL", "Internet radio or stream URL (http, https; .m3u and .pls links work too):",
            watermark: "https://");
        if (url is null)
        {
            return;
        }
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            await MessageWindow.ShowAsync(owner, "Open URL", "Only http:// and https:// addresses can be opened.", [url]);
            return;
        }
        var error = await _playlists.AddUrlAsync(uri, play: true);
        if (error is not null)
        {
            await MessageWindow.ShowAsync(owner, "Open URL", "The address could not be opened:", [error]);
        }
    }

    public async Task InstallSkinAsync(Window owner)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install skin",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("AudioTastic skin") { Patterns = ["*" + SkinManager.PackageExtension] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }
        try
        {
            var id = _skins.Install(path);
            _skins.Apply(id);
        }
        catch (SkinException ex)
        {
            await MessageWindow.ShowAsync(owner, "Skin not installed", "The skin is not valid:", ex.Errors);
        }
    }

    public void ApplySkin(string id, Window owner)
    {
        try
        {
            _skins.Apply(id);
        }
        catch (Exception ex) when (ex is SkinException or IOException)
        {
            var errors = ex is SkinException se ? se.Errors : [ex.Message];
            _ = MessageWindow.ShowAsync(owner, "Skin not changed", "The skin could not be loaded; the current skin is kept:", errors);
        }
    }

    public void OpenSkinsFolder()
    {
        Directory.CreateDirectory(_paths.SkinsFolder);
        _platform.OpenFolder(_paths.SkinsFolder);
    }

    private void ShowSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(this, _settings, _paths, _platform, _plugins, _loggerFactory.CreateLogger<SettingsWindow>());
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    /// <summary>PG-01: Windows menu entries for UI panel plugins (separator first when there are any).</summary>
    private List<object> PanelItems()
    {
        var items = new List<object>();
        foreach (var panel in _plugins.Instances<IUiPanelPlugin>())
        {
            if (_plugins.RecordOf(panel) is not { } record)
            {
                continue;
            }
            string title = record.Manifest.Name;
            _plugins.Guard(record, () => title = panel.Title);
            var item = new MenuItem { Header = title, ToggleType = MenuItemToggleType.CheckBox, IsChecked = _panels.ContainsKey(panel) };
            item.Click += (_, _) => TogglePanel(panel, record, title);
            items.Add(item);
        }
        if (items.Count > 0)
        {
            items.Insert(0, new Separator());
        }
        return items;
    }

    private readonly Dictionary<IUiPanelPlugin, Window> _panels = [];

    private void TogglePanel(IUiPanelPlugin panel, PluginRecord record, string title)
    {
        if (_panels.Remove(panel, out var open))
        {
            open.Close();
            return;
        }
        object? content = null;
        if (!_plugins.Guard(record, () => content = panel.CreatePanel()) || content is not Control control)
        {
            Player.ShowMessage($"Plugin '{record.Manifest.Name}' could not open its panel.");
            return;
        }
        var window = new Window
        {
            Title = title,
            Width = 360,
            Height = 300,
            Content = control,
            ShowInTaskbar = false,
            Icon = _main!.Icon,
        };
        window.Closed += (_, _) => _panels.Remove(panel);
        _plugins.PluginsChanged += CloseIfUnloaded;
        window.Closed += (_, _) => _plugins.PluginsChanged -= CloseIfUnloaded;
        _panels[panel] = window;
        window.Show(_main);

        void CloseIfUnloaded(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
        {
            if (record.Status != PluginStatus.Enabled)
            {
                window.Close();
            }
        });
    }

    private void ShowAbout()
    {
        var owner = _main!.IsVisible ? _main : _mini!;
        _ = new AboutWindow(_paths, _platform).ShowDialog(owner);
    }

    private void ShowMainMenu(Control source)
    {
        var interfaceSettings = UiSettings;
        var player = Player.Player;

        MenuItem Item(string header, string? action = null, bool enabled = true, bool? check = null)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (check is { } isChecked)
            {
                item.ToggleType = MenuItemToggleType.CheckBox;
                item.IsChecked = isChecked;
            }
            if (action is not null)
            {
                if (KeyMap.GestureFor(action) is { } gesture)
                {
                    item.InputGesture = KeyGesture.Parse(gesture);
                }
                item.Click += (_, _) => Execute(action, source);
            }
            return item;
        }

        MenuItem Repeat(string header, RepeatMode mode)
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = player.Repeat == mode };
            item.Click += (_, _) => player.Repeat = mode;
            return item;
        }

        var skins = new List<object>();
        foreach (var info in _skins.GetInstalledSkins())
        {
            var item = new MenuItem { Header = info.Name, ToggleType = MenuItemToggleType.Radio, IsChecked = info.Id == _skins.Current.Id };
            var id = info.Id;
            item.Click += (_, _) => ApplySkin(id, TopLevel.GetTopLevel(source) as Window ?? _main!);
            skins.Add(item);
        }
        skins.Add(new Separator());
        var install = new MenuItem { Header = "Install skin…" };
        install.Click += (_, _) => _ = InstallSkinAsync(TopLevel.GetTopLevel(source) as Window ?? _main!);
        skins.Add(install);
        var folder = new MenuItem { Header = "Open skins folder" };
        folder.Click += (_, _) => OpenSkinsFolder();
        skins.Add(folder);

        var alwaysOnTop = Item("Always on top", check: _mini!.IsVisible ? interfaceSettings.MiniPlayerAlwaysOnTop : interfaceSettings.AlwaysOnTop);
        alwaysOnTop.Click += (_, _) => _settings.Update(SettingsGroup.Interface, s =>
        {
            if (_mini.IsVisible)
            {
                s.Interface.MiniPlayerAlwaysOnTop = !s.Interface.MiniPlayerAlwaysOnTop;
            }
            else
            {
                s.Interface.AlwaysOnTop = !s.Interface.AlwaysOnTop;
            }
        });

        // Feature Specification 2.2 main menu; items of later phases are shown disabled.
        var menu = new ContextMenu
        {
            ItemsSource = new object[]
            {
                Item("Open file…", "open"),
                Item("Open folder…", enabled: false),
                Item("Open URL…", "openUrl"),
                Item("Play CD", enabled: false),
                new Separator(),
                new MenuItem
                {
                    Header = "Playback",
                    ItemsSource = new object[]
                    {
                        Item("Shuffle", "shuffle", check: player.Shuffle),
                        new MenuItem
                        {
                            Header = "Repeat",
                            ItemsSource = new object[] { Repeat("Off", RepeatMode.Off), Repeat("Playlist", RepeatMode.Playlist), Repeat("Track", RepeatMode.Track) },
                        },
                        Item("Speed…", enabled: false),
                        Item("A-B repeat", enabled: false),
                        Item("Stop after current", enabled: false),
                    },
                },
                Item("Sleep timer…", enabled: false),
                Item("Cast to…", enabled: false),
                Item("Visualisation", enabled: false),
                new MenuItem
                {
                    Header = "Windows",
                    ItemsSource = (object[])
                    [
                        Item("Equalizer", enabled: false),
                        Item("Playlist", enabled: false),
                        Item("Art & Lyrics", enabled: false),
                        Item("Mini player", "miniPlayer", check: _mini.IsVisible),
                        .. PanelItems(),
                    ],
                },
                new MenuItem { Header = "Skins", ItemsSource = skins },
                new MenuItem
                {
                    Header = "Options",
                    ItemsSource = new object[] { Item("Double size", "doubleSize", check: interfaceSettings.DoubleSize), alwaysOnTop },
                },
                new Separator(),
                Item("Settings…", "settings"),
                Item("About AudioTastic", "about"),
                Item("Exit", "exit"),
            },
        };
        menu.Open(source);
    }

    private async void Run(Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (ObjectDisposedException)
        {
        }
#pragma warning disable CA1031 // UI command: report instead of crashing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Command failed");
            Player.ShowMessage(ex.Message);
        }
    }

    public void Dispose()
    {
        _plugins.PluginDisabled -= OnPluginDisabled;
        _settings.Changed -= OnSettingsChanged;
        Player.Dispose();
        _fallbackSkin?.Dispose();
    }
}
