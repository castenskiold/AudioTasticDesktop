using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AudioTastic.App.Skins;
using AudioTastic.Core.Playlists;

namespace AudioTastic.App.Ui.Playlist;

/// <summary>Colours and fonts of the track list, from the skin element (playlistList).</summary>
internal sealed record PlaylistStyle(
    Typeface Typeface, Typeface PlayingTypeface, double FontSize, double RowHeight,
    IBrush Text, IBrush Muted, IBrush Playing, IBrush Selection, IBrush Hover)
{
    public static PlaylistStyle From(Skin skin, SkinElement e)
    {
        var family = skin.FontFamily(e.Font);
        var text = skin.Brush(e.Color) ?? Brushes.White;
        var selection = skin.Color(e.SelectionColor, Color.Parse("#33FFFFFF"));
        return new PlaylistStyle(
            new Typeface(family, FontStyle.Normal, skin.FontWeight(e.Font)),
            new Typeface(family, FontStyle.Normal, FontWeight.SemiBold),
            skin.FontSize(e, 8),
            e.RowHeight ?? 13,
            text,
            skin.Brush(e.MutedColor) ?? Brushes.Gray,
            skin.Brush(e.PlayingColor) ?? text,
            new SolidColorBrush(selection),
            new SolidColorBrush(selection, 0.4));
    }
}

/// <summary>One row's data: the entry and its number in the playlist.</summary>
internal sealed record RowItem(PlaylistEntry Entry, int Number)
{
    /// <summary>TL-04: what a screen reader says for the row: "3. Artist – Title, 5:59".</summary>
    public override string ToString() =>
        $"{Number}. {Entry.DisplayName}" + (Entry.Duration is { } d ? ", " + DisplayText.Time(d) : string.Empty);
}

/// <summary>A custom-drawn row: number, "Artist – Title" (trimmed) and duration; the playing entry has a white bar.</summary>
internal sealed class PlaylistRow(PlaylistStyle style, PlaylistController controller) : Control
{
    public override void Render(DrawingContext context)
    {
        if (DataContext is not RowItem row)
        {
            return;
        }
        var w = Bounds.Width;
        var h = Bounds.Height;
        var entry = row.Entry;
        var playing = controller.PlayingEntry == entry;
        var missing = entry.IsMissing == true;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // whole row is clickable

        if (playing)
        {
            context.FillRectangle(style.Playing, new Rect(1, 2, 1.5, h - 4));
        }
        var number = Text(row.Number.ToString(CultureInfo.InvariantCulture) + ".", style.Typeface, style.Muted);
        const double numberColumn = 28;
        context.DrawText(number, new Point(numberColumn - number.WidthIncludingTrailingWhitespace, (h - number.Height) / 2));

        var duration = entry.Duration is { } d ? Text(DisplayText.Time(d), style.Typeface, style.Muted) : null;
        var durationWidth = duration?.WidthIncludingTrailingWhitespace ?? 0;
        if (duration is not null)
        {
            context.DrawText(duration, new Point(w - 4 - durationWidth, (h - duration.Height) / 2));
        }
        else if (entry.IsUrl)
        {
            durationWidth = 18; // FM-10: streams show a broadcast icon instead of a duration
            DrawStreamIcon(context, w - 4, h);
        }

        // PB-09: queue position(s) "[1]" and PB-07: a resume marker "»" before the duration.
        var queue = controller.QueuePositions(entry);
        var marks = (queue.Count > 0 ? "[" + string.Join(",", queue) + "] " : string.Empty) + (controller.HasResumePosition(entry) ? "» " : string.Empty);
        if (marks.Length > 0)
        {
            var mark = Text(marks, style.Typeface, style.Playing);
            durationWidth += mark.WidthIncludingTrailingWhitespace + 2;
            context.DrawText(mark, new Point(w - 4 - durationWidth, (h - mark.Height) / 2));
        }

        var title = Text(entry.DisplayName, playing ? style.PlayingTypeface : style.Typeface,
            missing ? style.Muted : playing ? style.Playing : style.Text);
        title.MaxTextWidth = Math.Max(1, w - numberColumn - 6 - durationWidth - 10);
        title.Trimming = TextTrimming.CharacterEllipsis;
        title.MaxLineCount = 1;
        context.DrawText(title, new Point(numberColumn + 6, (h - title.Height) / 2));
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        InvalidateVisual();
    }

    /// <summary>A dot with two arcs on each side, "((·))", in the muted colour; <paramref name="right"/> is its right edge.</summary>
    private void DrawStreamIcon(DrawingContext context, double right, double h)
    {
        var pen = new Pen(style.Muted, 1.1, lineCap: PenLineCap.Round);
        var cx = right - 8;
        var cy = h / 2;
        context.DrawEllipse(style.Muted, null, new Point(cx, cy), 1.4, 1.4);
        const double halfAngle = 0.75; // radians
        foreach (var r in new[] { 3.8, 6.8 })
        {
            foreach (var side in new[] { -1, 1 })
            {
                var x = cx + (side * r * Math.Cos(halfAngle));
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    g.BeginFigure(new Point(x, cy - (r * Math.Sin(halfAngle))), isFilled: false);
                    g.ArcTo(new Point(x, cy + (r * Math.Sin(halfAngle))), new Size(r, r), 0, false,
                        side > 0 ? SweepDirection.Clockwise : SweepDirection.CounterClockwise);
                    g.EndFigure(isClosed: false);
                }
                context.DrawGeometry(null, pen, geometry);
            }
        }
    }

    private FormattedText Text(string text, Typeface typeface, IBrush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, style.FontSize, brush);
}

/// <summary>
/// PL-01 / PL-04: the track list (PW4). A virtualised ListBox (100,000+ entries) with skin colours, multi-select,
/// keyboard editing (Enter, Delete, Alt+arrows, Ctrl+A, Ctrl+Z/Y), drag to reorder or onto another tab, drops from
/// the OS at the pointer, and lazy tag requests for the rows on screen.
/// </summary>
internal sealed class PlaylistListView : Panel, IDisposable
{
    /// <summary>In-app drag of playlist entries (PL-04).</summary>
    public static readonly DataFormat<DraggedEntries> EntriesFormat = DataFormat.CreateInProcessFormat<DraggedEntries>("AudioTastic.PlaylistEntries");

    private readonly PlaylistController _controller;
    private readonly PlaylistStyle _style;
    private readonly ListBox _list;
    private readonly Border _insertLine;
    private readonly DispatcherTimer _tagTimer = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private PointerPressedEventArgs? _pendingPress;
    private Point _pressPoint;
    private ListBoxItem? _pendingItem;
    private bool _dragging;

    public PlaylistListView(Skin skin, SkinElement element, PlaylistController controller)
    {
        _controller = controller;
        _style = PlaylistStyle.From(skin, element);
        ClipToBounds = true;

        _list = new ListBox
        {
            SelectionMode = SelectionMode.Multiple,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            ItemTemplate = new FuncDataTemplate<RowItem>((_, _) => new PlaylistRow(_style, controller) { Height = _style.RowHeight }),
            ItemContainerTheme = RowTheme(_style),
            // Under the player-size scale transform (UI-08) the virtualizing panel sees a viewport divided by the
            // scale and realised only the top rows (2/3 of the list at 150 %; Anders saw empty space below them).
            // The largest cache Avalonia allows (2 viewports each way) covers the scales in use.
            ItemsPanel = new FuncTemplate<Panel?>(() => new VirtualizingStackPanel { CacheLength = 2 }),
        };
        // A slim, skin-coloured scroll bar instead of Fluent's expanding one (Anders, P3).
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled);
        ScrollViewer.SetAllowAutoHide(_list, false);
        _list.Styles.Add(new Style(x => x.OfType<ScrollBar>()) { Setters = { new Setter(StyledElement.ThemeProperty, SlimScrollBar(_style)) } });
        Avalonia.Automation.AutomationProperties.SetName(_list, "Playlist");
        DragDrop.SetAllowDrop(_list, true);
        _list.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        _list.AddHandler(DragDrop.DragLeaveEvent, (_, _) => _insertLine!.IsVisible = false);
        _list.AddHandler(DragDrop.DropEvent, OnDrop);
        _list.AddHandler(PointerPressedEvent, OnPressTunnel, RoutingStrategies.Tunnel);
        _list.AddHandler(PointerMovedEvent, OnMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        _list.AddHandler(PointerReleasedEvent, OnReleased, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
        _list.AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        _list.ContextRequested += (_, e) =>
        {
            e.Handled = true;
            controller.ShowRowMenu(_list);
        };
        _list.SelectionChanged += (_, _) => controller.NotifySelectionChanged();
        _list.ContainerPrepared += (_, _) => _tagTimer.Start();
        _tagTimer.Tick += (_, _) =>
        {
            _tagTimer.Stop();
            RequestVisibleTags();
        };

        _insertLine = new Border { Height = 1, Background = _style.Text, IsVisible = false, IsHitTestVisible = false, VerticalAlignment = VerticalAlignment.Top };
        Children.Add(_list);
        Children.Add(_insertLine);

        controller.AttachList(this);
        controller.ViewChanged += OnViewChanged;
        controller.DetailsChanged += OnRepaint;
        controller.PlayingChanged += OnRepaint;
        Reload();
        // UI-08: under the scale transform the panel can keep a too-small viewport from an earlier layout pass until the
        // next scroll or resize (Anders: the rows filled only after resizing or scrolling). After each layout, if the
        // drawn rows stop short of the list while more entries exist, the list's height is changed by one pixel and
        // back, which makes the panel take its viewport again.
        _list.LayoutUpdated += (_, _) => CheckRowsFill();
    }

    private bool _fillCheckPending;
    private int _fillNudges;

    private void CheckRowsFill()
    {
        if (_fillCheckPending)
        {
            return;
        }
        _fillCheckPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _fillCheckPending = false;
            if (!RowsStopShort())
            {
                _fillNudges = 0;
                return;
            }
            if (_fillNudges++ >= 3)
            {
                return; // give up until something else changes; never loop
            }
            _list.Margin = new Thickness(0, 0, 0, 1);
            Dispatcher.UIThread.Post(() => _list.Margin = default, DispatcherPriority.Background);
        }, DispatcherPriority.Background);
    }

    /// <summary>True when the last realised row ends above the list's bottom although later entries exist.</summary>
    private bool RowsStopShort()
    {
        var height = _list.Bounds.Height;
        if (_list.ItemCount == 0 || height <= 0)
        {
            return false;
        }
        Control? last = null;
        var lastIndex = -1;
        foreach (var container in _list.GetRealizedContainers())
        {
            var index = _list.IndexFromContainer(container);
            if (index > lastIndex)
            {
                (last, lastIndex) = (container, index);
            }
        }
        if (last is null || lastIndex >= _list.ItemCount - 1)
        {
            return last is null;
        }
        var bottom = last.TranslatePoint(new Point(0, last.Bounds.Height), _list)?.Y ?? height;
        return bottom < height - 1;
    }

    public IReadOnlyList<PlaylistEntry> SelectedEntries =>
        _list.Selection.SelectedItems.OfType<RowItem>().OrderBy(r => r.Number).Select(r => r.Entry).ToArray();

    public void SelectAll() => _list.SelectAll();
    public void SelectNone() => _list.UnselectAll();

    public void InvertSelection()
    {
        var selected = _list.Selection.SelectedIndexes.ToHashSet();
        var count = _list.ItemCount;
        _list.Selection.Clear();
        for (var i = 0; i < count; i++)
        {
            if (!selected.Contains(i))
            {
                _list.Selection.Select(i);
            }
        }
    }

    public void FocusList() => _list.Focus();

    public void Dispose()
    {
        _tagTimer.Stop();
        _controller.ViewChanged -= OnViewChanged;
        _controller.DetailsChanged -= OnRepaint;
        _controller.PlayingChanged -= OnRepaint;
        _controller.DetachList(this);
    }

    private void OnViewChanged(object? sender, EventArgs e) => Reload();

    private void OnRepaint(object? sender, EventArgs e)
    {
        foreach (var container in _list.GetRealizedContainers())
        {
            container.FindDescendantOfType<PlaylistRow>()?.InvalidateVisual();
        }
    }

    /// <summary>Rebuilds the rows from the controller, keeping the selected entries selected.</summary>
    private void Reload()
    {
        var selected = SelectedEntries.ToHashSet(ReferenceEqualityComparer.Instance);
        var entries = _controller.Active.Entries;
        var visible = _controller.Visible;
        Dictionary<PlaylistEntry, int>? numbers = null;
        if (visible.Count != entries.Length)
        {
            numbers = new Dictionary<PlaylistEntry, int>(entries.Length, ReferenceEqualityComparer.Instance);
            for (var i = 0; i < entries.Length; i++)
            {
                numbers[entries[i]] = i + 1;
            }
        }
        var rows = new RowItem[visible.Count];
        for (var i = 0; i < rows.Length; i++)
        {
            rows[i] = new RowItem(visible[i], numbers?[visible[i]] ?? i + 1);
        }
        _list.ItemsSource = rows;
        if (selected.Count > 0)
        {
            for (var i = 0; i < rows.Length; i++)
            {
                if (selected.Contains(rows[i].Entry))
                {
                    _list.Selection.Select(i);
                }
            }
        }
        _tagTimer.Start();
    }

    private void RequestVisibleTags()
    {
        var entries = _list.GetRealizedContainers()
            .Select(c => (c as ListBoxItem)?.DataContext as RowItem)
            .OfType<RowItem>()
            .Select(r => r.Entry)
            .Where(e => !e.TagsLoaded)
            .ToArray();
        if (entries.Length > 0)
        {
            _controller.RequestTags(entries);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Enter when _list.SelectedItem is RowItem row:
                _controller.Play(row.Entry);
                break;
            case Key.Delete:
                _controller.RemoveSelected();
                break;
            case Key.Q when e.KeyModifiers == KeyModifiers.None:
                _controller.QueueSelection(); // PB-09
                break;
            case Key.Up when e.KeyModifiers == KeyModifiers.Alt:
                _controller.MoveSelection(-1);
                break;
            case Key.Down when e.KeyModifiers == KeyModifiers.Alt:
                _controller.MoveSelection(+1);
                break;
            case Key.A when ctrl:
                _list.SelectAll();
                break;
            case Key.Z when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift):
            case Key.Y when ctrl:
                _controller.Redo();
                break;
            case Key.Z when ctrl:
                _controller.Undo();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ---- pointer: double-click to play, drag to move -------------------------------------------------

    private void OnPressTunnel(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_list);
        if (!point.Properties.IsLeftButtonPressed || (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) is not { } item)
        {
            return;
        }
        if (e.ClickCount == 2 && item.DataContext is RowItem row)
        {
            _controller.Play(row.Entry);
            e.Handled = true;
            return;
        }
        if (item.IsSelected && e.KeyModifiers == KeyModifiers.None)
        {
            // Keep a multi-selection so it can be dragged; a click without dragging selects just this row (on release).
            _pendingPress = e;
            _pendingItem = item;
            _pressPoint = point.Position;
            e.Handled = true;
        }
        else
        {
            _pendingPress = e;
            _pendingItem = null;
            _pressPoint = point.Position;
        }
    }

    private async void OnMoved(object? sender, PointerEventArgs e)
    {
        if (_pendingPress is null || _dragging || !e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed)
        {
            return;
        }
        var delta = e.GetPosition(_list) - _pressPoint;
        if (Math.Abs(delta.X) + Math.Abs(delta.Y) < 5)
        {
            return;
        }
        var selection = SelectedEntries;
        if (selection.Count == 0)
        {
            return;
        }
        _dragging = true;
        var press = _pendingPress;
        _pendingPress = null;
        _pendingItem = null;
        try
        {
            var data = new DataTransfer();
            data.Add(DataTransferItem.Create(EntriesFormat, new DraggedEntries(_controller.Active, selection)));
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move | DragDropEffects.Copy);
        }
        finally
        {
            _dragging = false;
            _insertLine.IsVisible = false;
        }
    }

    private void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pendingItem is { } item && !_dragging)
        {
            _list.SelectedItems?.Clear();
            item.IsSelected = true;
        }
        _pendingPress = null;
        _pendingItem = null;
    }

    // ---- drop -------------------------------------------------------------------------------------------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var dragged = e.DataTransfer.Items.Select(i => i.TryGetRaw(EntriesFormat) as DraggedEntries).FirstOrDefault(d => d is not null);
        var files = e.DataTransfer.Formats.Contains(DataFormat.File);
        if (dragged is null && !files)
        {
            e.DragEffects = DragDropEffects.None;
            return;
        }
        e.DragEffects = dragged is not null && dragged.Source == _controller.Active ? DragDropEffects.Move
            : dragged is not null && e.KeyModifiers.HasFlag(KeyModifiers.Control) ? DragDropEffects.Move
            : DragDropEffects.Copy;
        var (_, y) = DropIndex(e);
        _insertLine.Margin = new Thickness(2, y, 2, 0);
        _insertLine.IsVisible = true;
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        _insertLine.IsVisible = false;
        var (visibleIndex, _) = DropIndex(e);
        var index = ToPlaylistIndex(visibleIndex);
        var target = _controller.Active;
        if (e.DataTransfer.Items.Select(i => i.TryGetRaw(EntriesFormat) as DraggedEntries).FirstOrDefault(d => d is not null) is { } dragged)
        {
            if (dragged.Source == target)
            {
                target.MoveTo(dragged.Entries, index);
            }
            else
            {
                target.Add(dragged.Entries.Select(x => x.Clone()), index);
                if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                {
                    dragged.Source.Remove(dragged.Entries);
                }
            }
        }
        else if (e.DataTransfer.TryGetFiles() is { } files)
        {
            _ = _controller.AddAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>(), index);
        }
        e.Handled = true;
    }

    /// <summary>Insert position in the shown rows and the y of the insertion line.</summary>
    private (int Index, double Y) DropIndex(DragEventArgs e)
    {
        var position = e.GetPosition(_list);
        foreach (var container in _list.GetRealizedContainers())
        {
            var bounds = container.Bounds;
            var top = container.TranslatePoint(new Point(0, 0), _list)?.Y ?? bounds.Y;
            if (position.Y >= top && position.Y < top + bounds.Height)
            {
                var index = _list.IndexFromContainer(container);
                return position.Y > top + (bounds.Height / 2) ? (index + 1, top + bounds.Height) : (index, top);
            }
        }
        var count = _list.ItemCount;
        var last = count > 0 ? _list.ContainerFromIndex(count - 1) : null;
        var y = last?.TranslatePoint(new Point(0, last.Bounds.Height), _list)?.Y ?? 0;
        return (count, Math.Min(y, Bounds.Height - 1));
    }

    /// <summary>Maps an insert position in the (possibly filtered) rows to one in the playlist.</summary>
    private int ToPlaylistIndex(int visibleIndex)
    {
        var visible = _controller.Visible;
        var playlist = _controller.Active;
        if (visibleIndex < visible.Count)
        {
            return Math.Max(0, playlist.IndexOf(visible[visibleIndex]));
        }
        return visible.Count == 0 ? playlist.Count : playlist.IndexOf(visible[^1]) + 1;
    }

    /// <summary>A thin scroll bar: a rounded thumb in the muted colour, brighter on hover/drag; no arrow buttons.</summary>
    private static ControlTheme SlimScrollBar(PlaylistStyle style)
    {
        var thumb = new ControlTheme(typeof(Thumb))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, style.Muted),
                new Setter(Visual.OpacityProperty, 0.55),
                new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<Thumb>((t, _) => new Border
                {
                    [!Border.BackgroundProperty] = t[!TemplatedControl.BackgroundProperty],
                    CornerRadius = new CornerRadius(2),
                })),
            },
        };
        thumb.Add(new Style(x => x.Nesting().Class(":pointerover")) { Setters = { new Setter(Visual.OpacityProperty, 0.9) } });
        thumb.Add(new Style(x => x.Nesting().Class(":pressed")) { Setters = { new Setter(TemplatedControl.BackgroundProperty, style.Text), new Setter(Visual.OpacityProperty, 1.0) } });

        return new ControlTheme(typeof(ScrollBar))
        {
            Setters =
            {
                new Setter(Layoutable.WidthProperty, 6.0),
                new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<ScrollBar>((bar, scope) => new Border
                {
                    Background = Brushes.Transparent,
                    Padding = new Thickness(1.5, 2),
                    Child = new Track
                    {
                        Name = "PART_Track",
                        [!Track.MinimumProperty] = bar[!RangeBase.MinimumProperty],
                        [!Track.MaximumProperty] = bar[!RangeBase.MaximumProperty],
                        [!!Track.ValueProperty] = bar[!!RangeBase.ValueProperty],
                        [!Track.ViewportSizeProperty] = bar[!ScrollBar.ViewportSizeProperty],
                        [!Track.OrientationProperty] = bar[!ScrollBar.OrientationProperty],
                        Thumb = new Thumb { Theme = thumb, MinHeight = 18 },
                    }.RegisterInNameScope(scope),
                })),
            },
        };
    }

    private static ControlTheme RowTheme(PlaylistStyle style)
    {
        var theme = new ControlTheme(typeof(ListBoxItem))
        {
            Setters =
            {
                new Setter(TemplatedControl.BackgroundProperty, Brushes.Transparent),
                new Setter(Layoutable.HeightProperty, style.RowHeight),
                new Setter(TemplatedControl.PaddingProperty, new Thickness(0)),
                new Setter(TemplatedControl.TemplateProperty, new FuncControlTemplate<ListBoxItem>((item, scope) => new Border
                {
                    [!Border.BackgroundProperty] = item[!TemplatedControl.BackgroundProperty],
                    CornerRadius = new CornerRadius(2),
                    Child = new ContentPresenter
                    {
                        Name = "PART_ContentPresenter",
                        [!ContentPresenter.ContentProperty] = item[!ContentControl.ContentProperty],
                        [!ContentPresenter.ContentTemplateProperty] = item[!ContentControl.ContentTemplateProperty],
                        HorizontalContentAlignment = HorizontalAlignment.Stretch,
                        VerticalContentAlignment = VerticalAlignment.Stretch,
                    }.RegisterInNameScope(scope),
                })),
            },
        };
        theme.Add(new Style(x => x.Nesting().Class(":pointerover")) { Setters = { new Setter(TemplatedControl.BackgroundProperty, style.Hover) } });
        theme.Add(new Style(x => x.Nesting().Class(":selected")) { Setters = { new Setter(TemplatedControl.BackgroundProperty, style.Selection) } });
        return theme;
    }
}

/// <summary>Entries being dragged, and the playlist they come from.</summary>
internal sealed record DraggedEntries(Core.Playlists.Playlist Source, IReadOnlyList<PlaylistEntry> Entries);
