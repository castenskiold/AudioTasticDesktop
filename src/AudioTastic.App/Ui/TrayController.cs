using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using AudioTastic.Core.Playback;

namespace AudioTastic.App.Ui;

/// <summary>
/// SI-04: the tray icon (menu bar icon on macOS) with Play/Pause, Stop, Previous, Next, a Sleep timer submenu,
/// Show/Hide AudioTastic and Exit. The tooltip shows the current title; clicking the icon shows or hides the player.
/// Uses Avalonia's TrayIcon and NativeMenu (on Linux it needs a desktop with StatusNotifierItem support).
/// Everything here runs on the UI thread.
/// </summary>
public sealed class TrayController : IDisposable
{
    /// <summary>Windows shows at most 127 characters of a tray tooltip.</summary>
    public const int MaxToolTipLength = 127;

    private static readonly (string Text, int Minutes)[] SleepChoices =
        [("15 minutes", 15), ("30 minutes", 30), ("45 minutes", 45), ("1 hour", 60), ("1½ hours", 90), ("2 hours", 120)];

    private readonly PlayerViewModel _player;
    private readonly SleepTimerService _sleep;
    private readonly Action<string> _execute;
    private readonly Func<bool> _playerShown;
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _playPause;
    private readonly NativeMenuItem _showHide;
    private readonly NativeMenuItem _sleepItem;
    private readonly NativeMenuItem _sleepOff;
    private readonly TrayIcons _icons;

    /// <param name="player">Title and state for the tooltip and the Play/Pause item.</param>
    /// <param name="sleep">The sleep timer (PB-10) the submenu starts and cancels.</param>
    /// <param name="execute">Runs a shell action by name (playPause, stop, previous, next, sleepTimer, trayToggle, exit).</param>
    /// <param name="playerShown">True when a player window is on screen (for the Show/Hide item's text).</param>
    public TrayController(PlayerViewModel player, SleepTimerService sleep, Action<string> execute, Func<bool> playerShown)
    {
        _player = player;
        _sleep = sleep;
        _execute = execute;
        _playerShown = playerShown;

        _playPause = Item("Play", "playPause");
        _showHide = Item("Hide AudioTastic", "trayToggle");
        _sleepOff = new NativeMenuItem("Off") { ToggleType = MenuItemToggleType.Radio };
        _sleepOff.Click += (_, _) => _sleep.Cancel();
        var sleepMenu = new NativeMenu();
        sleepMenu.Items.Add(_sleepOff);
        foreach (var (text, minutes) in SleepChoices)
        {
            var item = new NativeMenuItem(text);
            item.Click += (_, _) => _sleep.StartAfter(TimeSpan.FromMinutes(minutes));
            sleepMenu.Items.Add(item);
        }
        var endOfTrack = new NativeMenuItem("At the end of this track");
        endOfTrack.Click += (_, _) => _sleep.StartAtEndOfTrack();
        var endOfPlaylist = new NativeMenuItem("At the end of the playlist");
        endOfPlaylist.Click += (_, _) => _sleep.StartAtEndOfPlaylist();
        sleepMenu.Items.Add(endOfTrack);
        sleepMenu.Items.Add(endOfPlaylist);
        sleepMenu.Items.Add(new NativeMenuItemSeparator());
        sleepMenu.Items.Add(Item("Other…", "sleepTimer"));
        _sleepItem = new NativeMenuItem("Sleep timer") { Menu = sleepMenu };

        var menu = new NativeMenu();
        menu.Items.Add(_playPause);
        menu.Items.Add(Item("Stop", "stop"));
        menu.Items.Add(Item("Previous", "previous"));
        menu.Items.Add(Item("Next", "next"));
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_sleepItem);
        menu.Items.Add(new NativeMenuItemSeparator());
        menu.Items.Add(_showHide);
        menu.Items.Add(Item("Exit", "exit"));
        // The menu is about to open: refresh texts that depend on the window state and the sleep timer's time left.
        menu.Opening += (_, _) => Refresh();

        _icon = new TrayIcon
        {
            Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://AudioTastic/Assets/audiotastic.ico"))),
            Menu = menu,
            IsVisible = false,
        };
        _icon.Clicked += (_, _) => _execute("trayToggle");
        _icons = [_icon];
        if (Application.Current is { } app)
        {
            TrayIcon.SetIcons(app, _icons);
        }
        _player.PropertyChanged += OnPlayerChanged;
        _sleep.Changed += OnSleepChanged;
        Refresh();
    }

    /// <summary>Shows or hides the icon (Settings › System integration › Show tray icon).</summary>
    public bool IsVisible
    {
        get => _icon.IsVisible;
        set => _icon.IsVisible = value;
    }

    /// <summary>The tooltip: "AudioTastic" and the current title on the next line, cut to the OS limit.</summary>
    public static string ToolTipFor(string? title)
    {
        var text = string.IsNullOrWhiteSpace(title) ? "AudioTastic" : "AudioTastic\n" + title.Trim();
        return text.Length <= MaxToolTipLength ? text : text[..(MaxToolTipLength - 1)] + "…";
    }

    /// <summary>Updates the tooltip and the item texts.</summary>
    public void Refresh()
    {
        _icon.ToolTipText = ToolTipFor(_player.State == PlaybackState.Stopped && string.IsNullOrEmpty(_player.Title) ? null : _player.Title);
        _playPause.Header = _player.State == PlaybackState.Playing ? "Pause" : "Play";
        _showHide.Header = _playerShown() ? "Hide AudioTastic" : "Show AudioTastic";
        var remaining = _sleep.Remaining;
        _sleepItem.Header = _sleep.Mode switch
        {
            null => "Sleep timer",
            SleepMode.AfterTime when remaining is { } left => $"Sleep timer ({Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))} min left)",
            SleepMode.EndOfTrack => "Sleep timer (end of track)",
            SleepMode.EndOfPlaylist => "Sleep timer (end of playlist)",
            _ => "Sleep timer (on)",
        };
        _sleepOff.IsChecked = _sleep.Mode is null;
    }

    private NativeMenuItem Item(string header, string action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => _execute(action);
        return item;
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.Title) or nameof(PlayerViewModel.State))
        {
            Refresh();
        }
    }

    private void OnSleepChanged(object? sender, EventArgs e) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

    public void Dispose()
    {
        _player.PropertyChanged -= OnPlayerChanged;
        _sleep.Changed -= OnSleepChanged;
        _icon.IsVisible = false; // no ghost icon left in the notification area
        _icon.Dispose();
    }
}
