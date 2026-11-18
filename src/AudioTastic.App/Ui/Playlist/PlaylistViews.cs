using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AudioTastic.App.Skins;
using AudioTastic.Core.Playlists;

namespace AudioTastic.App.Ui.Playlist;

/// <summary>Creates the playlist window's skin elements (playlistTabs, playlistSearch, playlistList, playlistInfo).</summary>
internal static class PlaylistViews
{
    public static Control? Create(Skin skin, SkinElement element, PlaylistController controller) => element.Type switch
    {
        "playlistList" => new PlaylistListView(skin, element, controller),
        "playlistTabs" => new PlaylistTabsView(skin, element, controller),
        "playlistSearch" => new PlaylistSearchBox(skin, element, controller),
        "playlistInfo" => new PlaylistInfoView(skin, element, controller),
        _ => null,
    };
}

/// <summary>
/// PL-02: tab strip (PW2). Click switches, double-click renames, right-click: rename, duplicate, save as, move, close;
/// "+" adds a tab. Entries or files dropped on a tab are added to it (Ctrl moves entries). The playing tab is marked ▸.
/// </summary>
internal sealed class PlaylistTabsView : Panel, IDisposable
{
    private readonly Skin _skin;
    private readonly SkinElement _element;
    private readonly PlaylistController _controller;
    private readonly StackPanel _tabs = new() { Orientation = Orientation.Horizontal };

    public PlaylistTabsView(Skin skin, SkinElement element, PlaylistController controller)
    {
        _skin = skin;
        _element = element;
        _controller = controller;
        _tabs.Spacing = element.Spacing ?? 3;
        Children.Add(new ScrollViewer
        {
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
            Content = _tabs,
        });
        controller.TabsChanged += OnChanged;
        controller.PlayingChanged += OnChanged;
        Rebuild();
    }

    public void Dispose()
    {
        _controller.TabsChanged -= OnChanged;
        _controller.PlayingChanged -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e) => Rebuild();

    private void Rebuild()
    {
        _tabs.Children.Clear();
        foreach (var playlist in _controller.Service.Playlists)
        {
            _tabs.Children.Add(Tab(playlist));
        }
        var add = Pill("+", active: false);
        ToolTip.SetTip(add, "New playlist");
        add.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                _controller.Service.New();
            }
        };
        _tabs.Children.Add(add);
    }

    private Border Tab(Core.Playlists.Playlist playlist)
    {
        var active = playlist == _controller.Active;
        var label = (_controller.IsPlayingPlaylist(playlist) ? "▸ " : string.Empty) + playlist.Name;
        var pill = Pill(label, active);
        Avalonia.Automation.AutomationProperties.SetName(pill, $"Playlist tab {playlist.Name}");
        pill.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(pill).Properties.IsLeftButtonPressed)
            {
                if (e.ClickCount == 2)
                {
                    Rename(pill, playlist);
                }
                else
                {
                    _controller.Service.Active = playlist;
                }
                e.Handled = true;
            }
        };
        pill.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            _controller.ShowTabMenu(pill, playlist, () => Rename(pill, playlist));
        };
        DragDrop.SetAllowDrop(pill, true);
        pill.AddHandler(DragDrop.DragOverEvent, (_, e) =>
        {
            e.DragEffects = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? DragDropEffects.Move : DragDropEffects.Copy;
            e.Handled = true;
        });
        pill.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            if (e.DataTransfer.Items.Select(i => i.TryGetRaw(PlaylistListView.EntriesFormat) as DraggedEntries).FirstOrDefault(d => d is not null) is { } dragged)
            {
                if (dragged.Source != playlist)
                {
                    playlist.Add(dragged.Entries.Select(x => x.Clone()));
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                    {
                        dragged.Source.Remove(dragged.Entries);
                    }
                }
            }
            else if (e.DataTransfer.TryGetFiles() is { } files)
            {
                _ = _controller.AddAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>(), target: playlist);
            }
            e.Handled = true;
        });
        return pill;
    }

    private void Rename(Border pill, Core.Playlists.Playlist playlist)
    {
        var box = new TextBox
        {
            Text = playlist.Name,
            FontSize = _skin.FontSize(_element, 6),
            MinWidth = 60,
            Padding = new Thickness(2, 0),
            MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        var done = false;
        void Commit(bool keep)
        {
            if (done)
            {
                return;
            }
            done = true;
            if (keep && !string.IsNullOrWhiteSpace(box.Text))
            {
                playlist.Name = box.Text.Trim();
            }
            Rebuild();
        }
        box.KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Escape)
            {
                Commit(e.Key == Key.Enter);
                e.Handled = true;
            }
        };
        box.LostFocus += (_, _) => Commit(true);
        pill.Child = box;
        box.Focus();
        box.SelectAll();
    }

    private Border Pill(string text, bool active)
    {
        var state = active ? SkinState.Active : SkinState.Normal;
        var e = _element;
        return new Border
        {
            Height = e.Height,
            CornerRadius = new CornerRadius(e.Height / 2),
            BorderThickness = new Thickness(e.StrokeWidth ?? 0.8),
            BorderBrush = _skin.Brush(e.Strokes?.Get(state) ?? e.Stroke),
            Background = _skin.Brush(e.Fills?.Get(state) ?? e.Fill) ?? Brushes.Transparent,
            Padding = new Thickness(e.Height / 2, 0),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new TextBlock
            {
                Text = text,
                FontFamily = _skin.FontFamily(e.Font),
                FontWeight = _skin.FontWeight(e.Font),
                FontSize = _skin.FontSize(e, 6),
                Foreground = _skin.Brush(e.Colors?.Get(state) ?? e.Color) ?? Brushes.White,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }
}

/// <summary>PL-05: the search field (PW3). J focuses it; typing filters; Enter plays; Down moves to the list; Esc clears.</summary>
internal sealed class PlaylistSearchBox : Border, IDisposable
{
    private readonly PlaylistController _controller;
    private readonly TextBox _box;
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public PlaylistSearchBox(Skin skin, SkinElement element, PlaylistController controller)
    {
        _controller = controller;
        CornerRadius = new CornerRadius(element.CornerRadius ?? element.Height / 2);
        BorderThickness = new Thickness(element.StrokeWidth ?? 0.8);
        BorderBrush = skin.Brush(element.Stroke);
        Background = skin.Brush(element.Fill) ?? Brushes.Transparent;
        _box = new TextBox
        {
            Text = controller.Search,
            PlaceholderText = element.Text ?? "Search playlist (J)",
            FontFamily = skin.FontFamily(element.Font),
            FontSize = skin.FontSize(element, 7),
            Foreground = skin.Brush(element.Color) ?? Brushes.White,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(element.Height / 2, 0),
            MinHeight = 0,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Avalonia.Automation.AutomationProperties.SetName(_box, "Search playlist");
        Child = _box;
        _box.TextChanged += (_, _) =>
        {
            _debounce.Stop();
            _debounce.Start();
        };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            _controller.Search = _box.Text ?? string.Empty;
        };
        _box.KeyDown += OnKeyDown;
        controller.SearchRequested += OnSearchRequested;
    }

    public void Dispose()
    {
        _debounce.Stop();
        _controller.SearchRequested -= OnSearchRequested;
    }

    private void OnSearchRequested(object? sender, EventArgs e)
    {
        _box.Focus();
        _box.SelectAll();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                _box.Text = string.Empty;
                _controller.Search = string.Empty;
                break;
            case Key.Enter:
                _controller.Search = _box.Text ?? string.Empty;
                var selection = _controller.Selection;
                var visible = _controller.Visible;
                var entry = selection.Count > 0 ? selection[0] : visible.Count > 0 ? visible[0] : null;
                if (entry is not null && e.KeyModifiers.HasFlag(KeyModifiers.Shift))
                {
                    _controller.Queue([entry]); // PB-09: Shift+Enter queues the result
                }
                else if (entry is not null)
                {
                    _controller.Play(entry);
                }
                break;
            default:
                return;
        }
        e.Handled = true;
    }
}

/// <summary>PL-07: "selection / total" duration (PW6); '+' when some lengths are not known yet.</summary>
internal sealed class PlaylistInfoView : TextBlock, IDisposable
{
    private readonly PlaylistController _controller;
    private readonly DispatcherTimer _throttle = new() { Interval = TimeSpan.FromMilliseconds(150) };

    public PlaylistInfoView(Skin skin, SkinElement element, PlaylistController controller)
    {
        _controller = controller;
        FontFamily = skin.FontFamily(element.Font);
        FontWeight = skin.FontWeight(element.Font);
        FontSize = skin.FontSize(element, 7);
        Foreground = skin.Brush(element.Color) ?? Brushes.Gray;
        TextAlignment = Ui.Controls.TimeDisplay.Align(element.Align ?? "right");
        VerticalAlignment = VerticalAlignment.Center;
        _throttle.Tick += (_, _) =>
        {
            _throttle.Stop();
            Update();
        };
        controller.ViewChanged += OnChanged;
        controller.DetailsChanged += OnChanged;
        controller.SelectionChanged += OnChanged;
        Update();
    }

    public void Dispose()
    {
        _throttle.Stop();
        _controller.ViewChanged -= OnChanged;
        _controller.DetailsChanged -= OnChanged;
        _controller.SelectionChanged -= OnChanged;
    }

    private void OnChanged(object? sender, EventArgs e)
    {
        if (!_throttle.IsEnabled)
        {
            _throttle.Start();
        }
    }

    private void Update()
    {
        var (selected, total, unknown, count) = _controller.Totals();
        Text = $"{DisplayText.Time(selected)} / {DisplayText.Time(total)}{(unknown ? "+" : string.Empty)}";
        if (_controller.AnalysisStatus is { } analysing)
        {
            Text = $"{analysing} · {Text}"; // AN-01 status text
        }
        var tip = $"{count} {(count == 1 ? "entry" : "entries")}; selection / total duration" + (unknown ? " (some lengths not known yet)" : string.Empty);
        ToolTip.SetTip(this, tip);
        Avalonia.Automation.AutomationProperties.SetName(this, Text + ", " + tip);
    }
}
