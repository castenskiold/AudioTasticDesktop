using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AudioTastic.App.Skins;
using AudioTastic.Metadata;

namespace AudioTastic.App.Ui.Controls;

/// <summary>
/// MD-04: skin element <c>lyrics</c> (AL2). Synced lyrics highlight the line being sung (<c>color</c>; other lines
/// <c>mutedColor</c>) and keep it in the middle; clicking a line seeks there. Plain lyrics scroll as text. The mouse
/// wheel scrolls (no visible scroll bar). Hidden when Settings › Metadata › Lyrics is off.
/// </summary>
public sealed class LyricsView : Border
{
    private readonly LyricsCoordinator _lyrics;
    private readonly IBrush _text;
    private readonly IBrush _muted;
    private readonly FontFamily _font;
    private readonly FontWeight _weight;
    private readonly double _size;
    private readonly StackPanel _lines = new() { Spacing = 2, Margin = new Thickness(2, 0) };
    private readonly ScrollViewer _scroller;
    private Lyrics? _shown;
    private int _highlighted = -1;

    public LyricsView(Skin skin, SkinElement element, LyricsCoordinator lyrics)
    {
        ArgumentNullException.ThrowIfNull(skin);
        ArgumentNullException.ThrowIfNull(element);
        _lyrics = lyrics;
        _text = skin.Brush(element.Color) ?? Brushes.White;
        _muted = skin.Brush(element.MutedColor) ?? Brushes.Gray;
        _font = skin.FontFamily(element.Font);
        _weight = skin.FontWeight(element.Font);
        _size = skin.FontSize(element, 7);
        Background = Brushes.Transparent;
        _scroller = new ScrollViewer
        {
            Content = _lines,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };
        Child = _scroller;
        Avalonia.Automation.AutomationProperties.SetName(this, "Lyrics");
        Show();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _lyrics.Changed += OnChanged;
        _lyrics.LineChanged += OnLineChanged;
        Show();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _lyrics.Changed -= OnChanged;
        _lyrics.LineChanged -= OnLineChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e) => Show();

    private void OnLineChanged(object? sender, EventArgs e) => Highlight(_lyrics.Line);

    private void Show()
    {
        IsVisible = _lyrics.Enabled;
        if (ReferenceEquals(_shown, _lyrics.Current) && _lines.Children.Count > 0)
        {
            Highlight(_lyrics.Line);
            return;
        }
        _shown = _lyrics.Current;
        _highlighted = -1;
        _lines.Children.Clear();
        if (_shown is null)
        {
            _lines.Children.Add(Line("No lyrics", _muted, TextAlignment.Center));
            ToolTip.SetTip(this, "No lyrics: add a .lrc file next to the track, or lyrics with Edit tags… (Ctrl+E)");
            return;
        }
        ToolTip.SetTip(this, _shown.Synced ? $"Synced lyrics ({_shown.Source}): click a line to jump there" : $"Lyrics ({_shown.Source})");
        foreach (var line in _shown.Lines)
        {
            var block = Line(line.Text.Length == 0 ? " " : line.Text, _shown.Synced ? _muted : _text, _shown.Synced ? TextAlignment.Center : TextAlignment.Left);
            if (_shown.Synced)
            {
                block.Cursor = new Cursor(StandardCursorType.Hand);
                block.PointerPressed += (_, e) =>
                {
                    if (e.GetCurrentPoint(block).Properties.IsLeftButtonPressed)
                    {
                        e.Handled = true;
                        _lyrics.Seek(line);
                    }
                };
            }
            _lines.Children.Add(block);
        }
        _scroller.Offset = default;
        Highlight(_lyrics.Line);
    }

    private TextBlock Line(string text, IBrush brush, TextAlignment alignment) => new()
    {
        Text = text,
        Foreground = brush,
        FontFamily = _font,
        FontWeight = _weight,
        FontSize = _size,
        TextWrapping = TextWrapping.Wrap,
        TextAlignment = alignment,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        Background = Brushes.Transparent, // the whole line is clickable
    };

    private void Highlight(int index)
    {
        if (_shown is not { Synced: true } || index == _highlighted)
        {
            return;
        }
        if (_highlighted >= 0 && _highlighted < _lines.Children.Count && _lines.Children[_highlighted] is TextBlock old)
        {
            old.Foreground = _muted;
        }
        _highlighted = index;
        if (index >= 0 && index < _lines.Children.Count && _lines.Children[index] is TextBlock current)
        {
            current.Foreground = _text;
            // Keep the sung line in the middle of the pane.
            var y = current.Bounds.Y + (current.Bounds.Height / 2) - (_scroller.Viewport.Height / 2);
            _scroller.Offset = new Vector(0, Math.Clamp(y, 0, Math.Max(0, _scroller.Extent.Height - _scroller.Viewport.Height)));
        }
    }
}
