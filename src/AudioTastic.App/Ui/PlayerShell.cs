using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using AudioTastic.App.Skins;
using AudioTastic.App.Ui.Controls;
using AudioTastic.App.Ui.Equalizer;
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
    private SkinnedWindow? _equalizer;
    private SkinnedWindow? _artWindow;
    private bool _playlistBeforeMini;
    private bool _equalizerBeforeMini;
    private bool _artBeforeMini;
    private readonly ArtCoordinator _art;
    private readonly LyricsCoordinator _lyrics;
    private readonly EqualizerController _eq;
    private bool _crossfadeOn;
    private readonly PlaylistController _playlists;
    private readonly PluginHost _plugins;
    private Skin? _fallbackSkin;
    private SettingsWindow? _settingsWindow;
    private readonly SleepTimerService _sleep;
    private bool _exiting;
    private readonly Integration.InstanceInbox _inbox;
    private readonly Integration.ShellIntegrationService _shellIntegration;
    private TrayController? _tray;
    private bool _inTray;
    private bool _mainBeforeTray;
    private bool _playlistBeforeTray;
    private bool _equalizerBeforeTray;
    private bool _artBeforeTray;
    private readonly List<Integration.InstanceCommand> _pendingCommands = [];
    private DispatcherTimer? _commandTimer;

    public PlayerShell(PlaybackEngine engine, SettingsStore settings, SkinManager skins, CrashReporter crashReporter,
        IPlatformServices platform, StartupOptions options, AppPaths paths, PlaylistController playlists, PluginHost plugins,
        AnalysisCoordinator analysis, SleepTimerService sleep, EqualizerController equalizer, ArtCoordinator art, LyricsCoordinator lyrics, Integration.InstanceInbox inbox,
        Integration.ShellIntegrationService shellIntegration, ILoggerFactory loggerFactory)
    {
        _inbox = inbox;
        _shellIntegration = shellIntegration;
        _art = art;
        _lyrics = lyrics;
        _sleep = sleep;
        _eq = equalizer;
        // PB-10: the timer stopped playback; close the app when Settings › Playback says so.
        sleep.Finished += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            if (sleep.CloseApp)
            {
                Exit();
            }
        });
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
        Player = new PlayerViewModel(engine, settings, analysis, sleep);
        // XF: the XF button and the IO/BM badge follow Settings › Crossfading.
        var xf = settings.Current.Crossfading;
        _crossfadeOn = xf.Enabled;
        Player.SetCrossfade(xf);
        settings.Changed += (_, e) =>
        {
            if (e.Group == SettingsGroup.Crossfading)
            {
                var c = e.Settings.Crossfading;
                Dispatcher.UIThread.Post(() =>
                {
                    _crossfadeOn = c.Enabled;
                    Player.SetCrossfade(c);
                    _main?.Refresh();
                    _mini?.Refresh();
                });
            }
        };
        // EQ toggles (main window EQ, equalizer ON/AUTO/PARAM) follow the equalizer state.
        equalizer.Changed += (_, _) =>
        {
            _main?.Refresh();
            _equalizer?.Refresh();
        };
        equalizer.Message += (_, text) => Player.ShowNotice(text);
        _plugins.PluginDisabled += OnPluginDisabled;
        _playlists.Message += (_, text) => Player.ShowNotice(text);
        _playlists.StopAfterCurrentRequested += (_, _) => Execute("stopAfterCurrent", _main!);
        sleep.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
            Player.ShowNotice(sleep.Mode is null ? "Sleep timer off" : $"Sleep timer: {SleepTimerWindow.Describe(sleep)}"));
    }

    /// <summary>PG-03: a plugin that kept failing was switched off; say so in the main window.</summary>
    private void OnPluginDisabled(object? sender, PluginDisabledEventArgs e) =>
        Dispatcher.UIThread.Post(() => Player.ShowMessage($"Plugin '{e.Plugin.Manifest.Name}' was disabled after repeated errors (see Settings › Plugins)."));

    public PlayerViewModel Player { get; }
    public InterfaceSettings UiSettings => _settings.Current.Interface;
    public SkinManager Skins => _skins;
    public PlaybackEngine Engine => _engine;

    /// <summary>EQ-01..EQ-05: the equalizer window's controller (also used by Settings › Equalizer &amp; DSP).</summary>
    public EqualizerController Equalizer => _eq;

    /// <summary>Shows the equalizer window (Settings › Equalizer &amp; DSP).</summary>
    public void ShowEqualizer() => SetEqualizerVisible(true);

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
        _equalizer = new SkinnedWindow("equalizer", this) { ShowInTaskbar = false };
        _artWindow = new SkinnedWindow("art", this) { ShowInTaskbar = false };
        _snap.Register(_main, isMain: true);
        _snap.Register(_mini);
        _snap.Register(_playlist);
        _snap.Register(_equalizer);
        _snap.Register(_artWindow);
        _snap.DragCompleted += (_, _) => SavePositions();
        BuildWindows();
        Restore(_main, UiSettings.MainWindow);
        Restore(_mini, UiSettings.MiniPlayerWindow);
        Restore(_playlist, UiSettings.PlaylistWindow);
        Restore(_equalizer, UiSettings.EqualizerWindow);
        Restore(_artWindow, UiSettings.ArtWindow);
        if (UiSettings.ArtWindowSize is { } artSize)
        {
            _artWindow.Width = artSize.Width * Scale;
            _artWindow.Height = artSize.Height * Scale;
        }
        _artWindow.Closing += (_, e) =>
        {
            if (!_exiting)
            {
                e.Cancel = true; // Alt+F4 on the Art & Lyrics window hides it
                SetArtVisible(false);
            }
        };
        _equalizer.Closing += (_, e) =>
        {
            if (!_exiting)
            {
                e.Cancel = true; // Alt+F4 on the equalizer hides it
                SetEqualizerVisible(false);
            }
        };
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
                    e.Cancel = true; // Alt+F4 on a player window exits the app cleanly (or hides it to the tray, SI-04)
                    Close();
                }
            };
            // SI-04: minimising hides the player to the tray when Settings › System integration says so.
            window.PropertyChanged += (_, e) =>
            {
                if (e.Property == Window.WindowStateProperty && window.WindowState == WindowState.Minimized
                    && _settings.Current.SystemIntegration.MinimiseToTray && TrayShown && !_exiting)
                {
                    Dispatcher.UIThread.Post(HideToTray);
                }
            };
        }
        // SI-04: the tray icon and its menu.
        _tray = new TrayController(Player, _sleep, ExecuteFromTray, () => PlayerShown)
        {
            IsVisible = _settings.Current.SystemIntegration.TrayIcon,
        };
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
            case "hide" when window == _equalizer: SetEqualizerVisible(false); break;
            case "hide" when window == _artWindow: SetArtVisible(false); break;
            case "artWindow": SetArtVisible(!_artWindow!.IsVisible); break; // MD-02: Alt+L, click on the MW2 art
            case "equalizer": SetEqualizerVisible(!_equalizer!.IsVisible); break; // EQ-01: Alt+G, EQ toggle
            case "crossfade": // XF (MW10)
                _settings.Update(SettingsGroup.Crossfading, s => s.Crossfading.Enabled = !s.Crossfading.Enabled);
                break;
            case "eqOn": _eq.ToggleOn(); break;
            case "eqAuto": _eq.ToggleAuto(); break;
            case "eqParam": _eq.ToggleParametric(); break;
            case "eqPresets": _eq.ShowPresetsMenu(source, window); break;
            case "eqDsp": _eq.OpenDspChain(window); break;
            case "visPlugin": ToggleVisualisationPlugin(); break; // UI-07: Ctrl+Shift+K
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
            case "close": Close(); break;
            case "exit": Exit(); break;
            case "miniPlayer": case "expand": case "titleDoubleClick": ToggleMiniPlayer(); break;
            case "doubleSize": _settings.Update(SettingsGroup.Interface, s => s.Interface.DoubleSize = !s.Interface.DoubleSize); break;
            case "settings": ShowSettings(); break;
            case "abRepeat":
                // PB-08: [ sets A, then B, then clears.
                Player.ShowNotice(player.AbLoop is null ? "A-B repeat: A set, press [ again for B"
                    : player.AbLoop.B is null ? "A-B repeat: looping A to B (drag the markers on the waveform; [ clears)" : "A-B repeat off");
                Run(player.AbRepeatAsync);
                break;
            case "stopAfterCurrent":
                // PB-09: Shift+V. The setter runs on the engine's queue, so the new value is the opposite of the old one.
                var stopAfter = !player.StopAfterCurrent;
                player.StopAfterCurrent = stopAfter;
                Player.ShowNotice(stopAfter ? "Stop after current: on (playback stops when this track ends)" : "Stop after current: off");
                break;
            case "queue": _playlists.QueueSelection(); break; // PB-09: Q
            case "editTags": _playlists.EditTags(source); break; // MD-01: Ctrl+E
            case "fileInfo": _playlists.ShowFileInfo(source, current: window != _playlist); break; // MD-05: Alt+3, MW5 double-click
            case "clearQueue": player.Queue.Clear(); break;
            case "speed": ShowDialog(new SpeedWindow(player)); break; // PB-06
            case "sleepTimer": ShowDialog(new SleepTimerWindow(_sleep)); break; // PB-10
            case "about": ShowAbout(); break;
            default:
                // equalizer, crossfade, cast: windows/features arrive in later phases.
                _logger.LogDebug("Action {Action} is not available yet", action);
                break;
        }
    }

    public void BeginDrag(Control source, PointerPressedEventArgs e) => _snap.BeginDrag(source, e);

    public void BeginResize(Control source, PointerPressedEventArgs e) => _snap.BeginResize(source, e);

    public bool IsAvailable(string bind) => bind is "shuffle" or "repeat" or "mute" or "playlist" or "equalizer" or "eqOn" or "eqAuto" or "eqParam" or "crossfade";

    private void ShowDialog(Window dialog)
    {
        var owner = _main!.IsVisible ? _main : _mini!;
        _ = dialog.ShowDialog(owner);
    }

    public Control? CreateElement(Skin skin, SkinElement element) => element.Type switch
    {
        "visualisation" => new VisualisationView(skin, element, _engine.Graph.Visualisation, _settings), // UI-07
        "eqSlider" or "eqCurve" or "eqParametric" => EqualizerViews.Create(skin, element, _eq), // EQ-01, EQ-04
        "art" => CreateArt(element), // MD-02
        "lyrics" => new LyricsView(skin, element, _lyrics), // MD-04
        _ => PlaylistViews.Create(skin, element, _playlists),
    };

    /// <summary>MD-02: album art; outside the Art &amp; Lyrics window a click opens that window.</summary>
    private ArtView CreateArt(SkinElement element)
    {
        ArtView? view = null;
        view = new ArtView(element, _art, SkinView.Logo.Value, () =>
        {
            if (TopLevel.GetTopLevel(view) != _artWindow)
            {
                SetArtVisible(true);
                _artWindow!.Activate();
            }
        });
        return view;
    }

    /// <summary>Files dropped on a player window: added to the active playlist; on the main window or mini player they play (PL-04).</summary>
    public void DropFiles(IReadOnlyList<string> paths, Window window) =>
        _ = _playlists.AddAsync(paths, play: window != _playlist);

    public bool IsToggleOn(string bind) => bind switch
    {
        "playlist" => _playlist?.IsVisible == true,
        "equalizer" => _equalizer?.IsVisible == true,
        "eqOn" => _eq.IsOn,
        "eqAuto" => _eq.IsAuto,
        "eqParam" => _eq.IsParametric,
        "crossfade" => _crossfadeOn,
        _ => false,
    };

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
        if (skin.Window("equalizer") is { } equalizer)
        {
            _equalizer!.Build(skin, equalizer, scale);
        }
        else
        {
            // A skin without an equalizer window uses the default skin's (skin-format.md).
            _fallbackSkin ??= _skins.Load(SkinManager.DefaultSkinId);
            _equalizer!.Build(_fallbackSkin, _fallbackSkin.Window("equalizer")!, scale);
        }
        if (skin.Window("art") is { } art)
        {
            _artWindow!.Build(skin, art, scale);
        }
        else
        {
            // A skin without an Art & Lyrics window uses the default skin's (skin-format.md).
            _fallbackSkin ??= _skins.Load(SkinManager.DefaultSkinId);
            _artWindow!.Build(_fallbackSkin, _fallbackSkin.Window("art")!, scale);
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

    /// <summary>EQ-01: shows or hides the equalizer window (EQ toggle, Alt+G, Windows menu); the choice is saved.</summary>
    private void SetEqualizerVisible(bool visible)
    {
        if (visible == _equalizer!.IsVisible)
        {
            return;
        }
        if (visible)
        {
            if (UiSettings.EqualizerWindow is null)
            {
                PlaceEqualizerBelowMain();
            }
            _equalizer.Show(_main!);
        }
        else
        {
            SavePositions();
            _equalizer.Hide();
        }
        _settings.Update(SettingsGroup.Interface, s => s.Interface.EqualizerVisible = visible);
        _main!.Refresh();
        _mini!.Refresh();
    }

    /// <summary>MD-02: shows or hides the Art &amp; Lyrics window (Alt+L, the MW2 art, Windows menu); the choice is saved.</summary>
    private void SetArtVisible(bool visible)
    {
        if (visible == _artWindow!.IsVisible)
        {
            return;
        }
        if (visible)
        {
            if (UiSettings.ArtWindow is null)
            {
                // First time: dock it to the right of the main window, top edges aligned.
                var main = WindowSnapManager.Bounds(_main!);
                _artWindow.WindowStartupLocation = WindowStartupLocation.Manual;
                _artWindow.Position = new PixelPoint(main.Right, main.Y);
            }
            _artWindow.Show(_main!);
        }
        else
        {
            SavePositions();
            _artWindow.Hide();
        }
        _settings.Update(SettingsGroup.Interface, s => s.Interface.ArtVisible = visible);
        _main!.Refresh();
        _mini!.Refresh();
    }

    /// <summary>
    /// First time: dock the equalizer below the main window (Winamp's order); a playlist docked there moves down
    /// to make room.
    /// </summary>
    private void PlaceEqualizerBelowMain()
    {
        var main = WindowSnapManager.Bounds(_main!);
        _equalizer!.WindowStartupLocation = WindowStartupLocation.Manual;
        _equalizer.Position = new PixelPoint(main.X, main.Bottom);
        if (_playlist!.IsVisible && Math.Abs(_playlist.Position.Y - main.Bottom) <= 2 && Math.Abs(_playlist.Position.X - main.X) <= 2)
        {
            var height = (int)Math.Round((_skins.Current.Window("equalizer")?.Height ?? 116) * Scale * _main!.DesktopScaling);
            _playlist.Position = new PixelPoint(_playlist.Position.X, main.Bottom + height);
        }
    }

    /// <summary>UI-07: Ctrl+Shift+K opens the first visualisation plugin (or closes the open one).</summary>
    private void ToggleVisualisationPlugin()
    {
        if (VisualisationPluginWindow.Current is { } open)
        {
            open.Close();
            return;
        }
        if (_plugins.Instances<IVisualisationPlugin>() is [var plugin, ..])
        {
            VisualisationPluginWindow.Open(_main!.IsVisible ? _main : _mini!, _plugins, plugin, _engine.Graph.Visualisation);
        }
        else
        {
            Player.ShowNotice("No visualisation plugin is installed (Settings › Plugins).");
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
        if (toMini)
        {
            // Recorded before the main window hides: the docked windows are owned by it, so hiding it hides them too
            // and they would never come back when expanding.
            _playlistBeforeMini = _playlist!.IsVisible;
            _equalizerBeforeMini = _equalizer!.IsVisible;
            _artBeforeMini = _artWindow!.IsVisible;
        }
        SavePositions();
        show.Show();
        show.Activate();
        hide.Hide();
        // UI-05: other windows are hidden in the mini player and come back when expanding.
        if (toMini)
        {
            _playlist!.Hide();
            _equalizer!.Hide();
            _artWindow!.Hide();
        }
        else
        {
            if (_artBeforeMini)
            {
                _artWindow!.Show(_main!);
            }
            if (_equalizerBeforeMini)
            {
                _equalizer!.Show(_main!);
            }
            if (_playlistBeforeMini)
            {
                ShowPlaylist();
            }
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
        if (e.Group == SettingsGroup.SystemIntegration)
        {
            var trayIcon = e.Settings.SystemIntegration.TrayIcon;
            Dispatcher.UIThread.Post(() =>
            {
                if (_tray is not null)
                {
                    _tray.IsVisible = trayIcon;
                }
                if (!trayIcon && _inTray)
                {
                    ShowFromTray(); // the icon was the only way back
                }
            });
            return;
        }
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
            _equalizer!.Refresh();
            _artWindow!.Refresh();
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
        var equalizer = _equalizer!.Position;
        var equalizerVisible = _equalizer.IsVisible;
        var art = _artWindow!.Position;
        var artSize = new WindowSize(_artWindow.Width / Scale, _artWindow.Height / Scale);
        var artVisible = _artWindow.IsVisible;
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
            if (equalizerVisible)
            {
                s.Interface.EqualizerWindow = new WindowPlacement(equalizer.X, equalizer.Y);
            }
            if (artVisible)
            {
                s.Interface.ArtWindow = new WindowPlacement(art.X, art.Y);
                s.Interface.ArtWindowSize = artSize;
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
        _tray?.Dispose(); // no ghost icon in the notification area
        _tray = null;
        SavePositions();
        _settingsWindow?.Close();
        _playlists.Service.Flush();
        _playlist?.Close();
        _equalizer?.Close();
        _artWindow?.Close();
        VisualisationPluginWindow.Current?.Close();
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
        if (UiSettings.EqualizerVisible && _main.IsVisible)
        {
            _equalizer!.Show(_main);
        }
        if (UiSettings.ArtVisible && _main.IsVisible)
        {
            _artWindow!.Show(_main);
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
        else if (_shellIntegration.ShouldAskAboutFileTypes)
        {
            Dispatcher.UIThread.Post(async () => await AskAboutFileTypesAsync(owner)); // SI-05: once, on the first start
        }
        if (_options.CrashTest)
        {
            // TL-03 exit criterion: a forced exception must produce a crash report.
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException(
                $"Crash test requested with {StartupOptions.CrashTestSwitch}."));
        }
        // SI-06: the start-up files plus whatever later starts sent while this window was opening, as one batch.
        var early = _inbox.Attach(OnInstanceCommand);
        var batch = Integration.InstanceBatch.Merge([Integration.InstanceCommand.From(_options), .. early],
            _settings.Current.SystemIntegration.OpenFiles);
        if (batch.Play.Count == 0)
        {
            ResumeSession();
        }
        ApplyBatch(batch, activate: early.Count > 0);
    }

    /// <summary>SI-06: a later start handed over its files (pipe thread). Commands within 300 ms form one batch.</summary>
    private void OnInstanceCommand(Integration.InstanceCommand command) => Dispatcher.UIThread.Post(() =>
    {
        _pendingCommands.Add(command);
        if (_commandTimer is null)
        {
            _commandTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _commandTimer.Tick += (_, _) =>
            {
                _commandTimer.Stop();
                var commands = _pendingCommands.ToArray();
                _pendingCommands.Clear();
                ApplyBatch(Integration.InstanceBatch.Merge(commands, _settings.Current.SystemIntegration.OpenFiles), activate: true);
            };
        }
        _commandTimer.Stop();
        _commandTimer.Start();
    });

    /// <summary>
    /// SI-05 / SI-06 / PL-04: files to play are added to the active playlist and the first one starts; enqueued files
    /// are only added (after them). Opening brings the player to the front.
    /// </summary>
    private void ApplyBatch(Integration.InstanceBatch batch, bool activate)
    {
        if (batch.Play.Count > 0 || batch.Enqueue.Count > 0 || batch.Controls.Count > 0)
        {
            _logger.LogInformation("Opening {Play} and enqueueing {Enqueue} files; commands: {Commands}", batch.Play.Count, batch.Enqueue.Count,
                string.Join(' ', batch.Controls.Select(c => c.Value is { } v ? $"{c.Name} {v.ToString(CultureInfo.InvariantCulture)}" : c.Name)));
            Run(async () =>
            {
                if (batch.Play.Count > 0)
                {
                    await _playlists.AddAsync(batch.Play, play: true);
                }
                if (batch.Enqueue.Count > 0)
                {
                    await _playlists.AddAsync(batch.Enqueue);
                }
                foreach (var control in batch.Controls) // SI-08: after the files, in the order given
                {
                    RunControl(control);
                }
            });
        }
        if (batch.Enqueue.Count > 0 && batch.Play.Count == 0)
        {
            Player.ShowNotice(batch.Enqueue.Count == 1
                ? $"Enqueued {System.IO.Path.GetFileName(batch.Enqueue[0])}"
                : $"Enqueued {batch.Enqueue.Count} items");
        }
        if (activate && batch.Activate)
        {
            BringToFront();
        }
    }

    /// <summary>
    /// SI-05 (Anders, 19 Nov 2026): the first start asks whether to use AudioTastic for audio files. The answer is kept
    /// (Settings › System integration changes it); Yes registers the file types and opens Windows' Default apps page.
    /// </summary>
    private async Task AskAboutFileTypesAsync(Window owner)
    {
        var yes = await QuestionWindow.AskAsync(owner, "Use AudioTastic for audio files?",
            "AudioTastic can be the player for your audio files and playlists (FLAC, MP3, WAV, M3U and the other types it plays), " +
            "so double-clicking one opens it here.\n\nWindows lets you choose the default yourself: Yes registers AudioTastic and opens " +
            "Settings › Default apps, where you pick it for the file types you want. You can change this later in Settings › System integration.",
            "Yes, set up", "Not now");
        _settings.Update(SettingsGroup.SystemIntegration, s => s.SystemIntegration.FileAssociations = yes);
        if (yes)
        {
            await ChooseDefaultAppsAsync();
        }
    }

    /// <summary>SI-05: registers the file types (if not yet) and opens Windows' Default apps page for AudioTastic.</summary>
    public async Task ChooseDefaultAppsAsync()
    {
        if (_settings.Current.SystemIntegration.FileAssociations != true)
        {
            _settings.Update(SettingsGroup.SystemIntegration, s => s.SystemIntegration.FileAssociations = true);
        }
        await _shellIntegration.Refresh(); // the registration must exist before Windows shows the page
        _shellIntegration.OpenDefaultApps();
    }

    /// <summary>SI-08: a player command from the command line (the same actions as the buttons and keys).</summary>
    private void RunControl(ControlCommand control)
    {
        switch (control.Name)
        {
            case "play" or "pause" or "playPause" or "stop" or "next" or "previous":
                Execute(control.Name, _main!);
                break;
            case "volume" when control.Value is { } volume:
                Player.Volume = (int)Math.Round(Math.Clamp(volume, 0, 100));
                break;
            case "seek" when control.Value is { } seconds:
                Run(() => Player.Player.SeekAsync(TimeSpan.FromSeconds(Math.Max(0, seconds))));
                break;
            case "quit":
                Exit();
                break;
        }
    }

    // ---- tray (SI-04) ----------------------------------------------------------------------------------

    private bool TrayShown => _tray is { IsVisible: true };

    /// <summary>True when a player window is on screen (not hidden to the tray, not minimised).</summary>
    private bool PlayerShown => !_inTray
        && ((_main!.IsVisible && _main.WindowState != WindowState.Minimized) || (_mini!.IsVisible && _mini.WindowState != WindowState.Minimized));

    /// <summary>The close button and Alt+F4: exit, or hide to the tray when Settings › System integration says so.</summary>
    private void Close()
    {
        if (_settings.Current.SystemIntegration.CloseToTray && TrayShown)
        {
            HideToTray();
        }
        else
        {
            Exit();
        }
    }

    private void ExecuteFromTray(string action)
    {
        switch (action)
        {
            case "trayToggle":
                if (PlayerShown)
                {
                    HideToTray();
                }
                else
                {
                    ShowFromTray();
                }
                break;
            case "exit":
                Exit();
                break;
            case "sleepTimer": // a dialog needs a visible owner
                ShowFromTray();
                Execute("sleepTimer", _mini!.IsVisible ? _mini : _main!);
                break;
            default:
                Execute(action, _main!);
                break;
        }
    }

    /// <summary>SI-04: hides every player window; the tray icon brings them back as they were.</summary>
    private void HideToTray()
    {
        if (_inTray || _exiting)
        {
            return;
        }
        if (!TrayShown)
        {
            (_mini!.IsVisible ? _mini : _main!).WindowState = WindowState.Minimized; // never hide without a way back
            return;
        }
        _mainBeforeTray = _main!.IsVisible || !_mini!.IsVisible;
        if (_main.IsVisible)
        {
            _playlistBeforeTray = _playlist!.IsVisible;
            _equalizerBeforeTray = _equalizer!.IsVisible;
            _artBeforeTray = _artWindow!.IsVisible;
        }
        SavePositions();
        _inTray = true;
        _playlist!.Hide();
        _equalizer!.Hide();
        _artWindow!.Hide();
        _main.Hide();
        _mini!.Hide();
        _tray?.Refresh();
    }

    /// <summary>SI-04: shows the player again as it was before it went to the tray.</summary>
    private void ShowFromTray()
    {
        if (!_inTray)
        {
            var visible = _mini!.IsVisible ? _mini : _main!;
            if (visible.WindowState == WindowState.Minimized)
            {
                visible.WindowState = WindowState.Normal;
            }
            visible.Activate();
            return;
        }
        _inTray = false;
        var window = _mainBeforeTray ? _main! : _mini!;
        window.WindowState = WindowState.Normal;
        window.Show();
        if (_mainBeforeTray)
        {
            if (_artBeforeTray)
            {
                _artWindow!.Show(_main!);
            }
            if (_equalizerBeforeTray)
            {
                _equalizer!.Show(_main!);
            }
            if (_playlistBeforeTray)
            {
                ShowPlaylist();
            }
        }
        window.Activate();
        _tray?.Refresh();
    }

    /// <summary>SI-06: shows the player (main window or mini player) in front of other programs.</summary>
    private void BringToFront()
    {
        if (_inTray)
        {
            ShowFromTray();
            return;
        }
        var window = _mini!.IsVisible ? _mini : _main!;
        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }
        window.Show();
        window.Activate();
    }

    /// <summary>PB-07: reopens the last track at its position (paused or playing, Settings › Playback).</summary>
    private void ResumeSession()
    {
        var playback = _settings.Current.Playback;
        if (playback.ResumeSession == ResumeMode.Off || playback.LastSession is not { } session)
        {
            return;
        }
        var playlists = _playlists.Service.Playlists;
        var playlist = playlists.FirstOrDefault(p => p.Id.ToString() == session.PlaylistId) ?? _playlists.Active;
        var entries = playlist.Entries;
        var entry = session.Index < entries.Length && PathEquals(entries[session.Index].Path, session.Path)
            ? entries[session.Index]
            : entries.FirstOrDefault(e => PathEquals(e.Path, session.Path));
        if (entry is null)
        {
            _logger.LogInformation("Last session's track {Path} is no longer in its playlist", session.Path);
            return;
        }
        var play = playback.ResumeSession == ResumeMode.Playing && session.WasPlaying;
        Run(() => _engine.ResumeSessionAsync(playlist, entry, TimeSpan.FromSeconds(session.PositionSeconds), play));
    }

    private static bool PathEquals(string a, string b) => string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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
                        Item("Speed…", "speed"),
                        Item(player.AbLoop is null ? "A-B repeat: set A  [" : player.AbLoop.B is null ? "A-B repeat: set B  [" : "A-B repeat: clear  [", "abRepeat",
                            check: player.AbLoop?.B is not null),
                        Item("Stop after current  Shift+V", "stopAfterCurrent", check: player.StopAfterCurrent),
                        Item("Clear queue", "clearQueue", enabled: player.Queue.Count > 0),
                    },
                },
                Item(_sleep.Mode is null ? "Sleep timer…" : $"Sleep timer… ({SleepTimerWindow.Describe(_sleep)})", "sleepTimer", check: _sleep.Mode is not null ? true : null),
                Item("Cast to…", enabled: false),
                new MenuItem { Header = "Visualisation", ItemsSource = VisualisationItems() },
                new MenuItem
                {
                    Header = "Windows",
                    ItemsSource = (object[])
                    [
                        Item("Equalizer", "equalizer", check: _equalizer!.IsVisible),
                        Item("Playlist", "playlist", check: _playlist!.IsVisible),
                        Item("Art & Lyrics", "artWindow", check: _artWindow!.IsVisible),
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

    /// <summary>UI-07: Main menu › Visualisation: the built-in MW6 view and the visualisation plugins.</summary>
    private List<object> VisualisationItems()
    {
        var current = UiSettings.Visualisation;
        MenuItem Builtin(string header, VisualisationType type)
        {
            var item = new MenuItem { Header = header, ToggleType = MenuItemToggleType.Radio, IsChecked = current == type };
            item.Click += (_, _) => _settings.Update(SettingsGroup.Interface, s => s.Interface.Visualisation = type);
            return item;
        }
        var items = new List<object> { Builtin("Spectrum", VisualisationType.Spectrum), Builtin("Oscilloscope", VisualisationType.Oscilloscope), Builtin("Off", VisualisationType.Off) };
        var plugins = _plugins.Instances<IVisualisationPlugin>();
        items.Add(new Separator());
        if (plugins.Count == 0)
        {
            items.Add(new MenuItem { Header = "No visualisation plugins installed", IsEnabled = false });
        }
        foreach (var plugin in plugins)
        {
            var name = plugin.GetType().Name;
            _plugins.Guard(_plugins.RecordOf(plugin)!, () => name = plugin.Name);
            var item = new MenuItem { Header = $"Open {name}" };
            item.Click += (_, _) => VisualisationPluginWindow.Open(_main!.IsVisible ? _main : _mini!, _plugins, plugin, _engine.Graph.Visualisation);
            items.Add(item);
        }
        if (VisualisationPluginWindow.Current is not null)
        {
            var close = new MenuItem { Header = "Close visualisation", InputGesture = KeyGesture.Parse("Ctrl+Shift+K") };
            close.Click += (_, _) => VisualisationPluginWindow.Current?.Close();
            items.Add(close);
        }
        return items;
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
