using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Svg.Skia;
using Avalonia.Threading;
using AudioTastic.App.Skins;

namespace AudioTastic.App.Ui.Controls;

/// <summary>Draws a skin image: SVG (recoloured through currentColor) or PNG (with @2x when drawn at 150 % or more).</summary>
internal sealed class SkinGraphic : Decorator
{
    private static readonly Uri BaseUri = new("avares://AudioTastic/");
    private Skin? _skin;
    private string? _path;
    private Avalonia.Svg.Skia.Svg? _svg;
    private Image? _image;
    private double _extraScale = 1;

    public SkinGraphic()
    {
        IsHitTestVisible = false;
    }

    /// <summary>Shows <paramref name="path"/> from <paramref name="skin"/>; <paramref name="color"/> is the SVG's currentColor.</summary>
    public void Show(Skin skin, string? path, Color color, double extraScale = 1)
    {
        _skin = skin;
        _extraScale = extraScale;
        if (path is null)
        {
            Child = null;
            _path = null;
            return;
        }
        if (Skin.IsSvg(path))
        {
            _svg ??= new Avalonia.Svg.Skia.Svg(BaseUri) { Stretch = Stretch.Uniform };
            if (_path != path)
            {
                _svg.SvgSource = skin.Svg(path);
            }
            _svg.CurrentColor = color;
            Child = _svg;
        }
        else
        {
            _image ??= new Image { Stretch = Stretch.Uniform };
            Child = _image;
            if (_path != path || _image.Source is null)
            {
                _image.Source = skin.Bitmap(path, Scale());
            }
        }
        _path = path;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_skin is not null && _path is not null && !Skin.IsSvg(_path) && _image is not null)
        {
            _image.Source = _skin.Bitmap(_path, Scale()); // render scaling is known now
        }
    }

    private double Scale() => (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1) * _extraScale;
}

/// <summary>A skinned push button or toggle (states: normal, hover, pressed, active, alternate, disabled).</summary>
internal sealed class SkinButton : Panel, IAccessibleAction
{
    private readonly Skin _skin;
    private readonly SkinElement _element;
    private readonly Border _background = new();
    private readonly SkinGraphic _graphic = new();
    private readonly TextBlock? _text;
    private readonly double _scale;
    private bool _hover;
    private bool _pressed;
    private bool _active;
    private bool _alternate;
    private bool _available = true;

    public SkinButton(Skin skin, SkinElement element, double scale)
    {
        _skin = skin;
        _element = element;
        _scale = scale;
        Background = Brushes.Transparent; // receive pointer events everywhere in the bounds
        Cursor = new Cursor(StandardCursorType.Hand);
        _background.CornerRadius = new CornerRadius(element.CornerRadius ?? 0);
        _background.BorderThickness = new Thickness(element.StrokeWidth ?? 0);
        _background.IsHitTestVisible = false;
        _graphic.Margin = new Thickness(element.Padding);
        Children.Add(_background);
        Children.Add(_graphic);
        if (element.Text is not null)
        {
            _text = new TextBlock
            {
                FontFamily = skin.FontFamily(element.Font),
                FontWeight = skin.FontWeight(element.Font),
                FontSize = skin.FontSize(element, 6),
                LetterSpacing = element.LetterSpacing ?? 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            Children.Add(_text);
        }
        if (element.Tooltip is not null)
        {
            ToolTip.SetTip(this, element.Tooltip);
        }
        // TL-04: reachable with Tab, pressed with Enter or Space, read as a button (or toggle) with its shortcut key.
        Focusable = true;
        FocusAdorner = Accessible.FocusOutline(skin, element.CornerRadius ?? 3);
        Accessible.Describe(this, element);
        UpdateVisual();
    }

    public event EventHandler? Clicked;

    private bool IsToggle => _element.Type == "toggle";

    public bool CanInvoke => _available;

    public bool? IsOn => IsToggle ? _active : null;

    public void InvokeAction() => Clicked?.Invoke(this, EventArgs.Empty);

    protected override AutomationPeer OnCreateAutomationPeer() => IsToggle ? new TogglePeer(this, this) : new ActionPeer(this, this);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_available && e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            Clicked?.Invoke(this, EventArgs.Empty);
        }
        base.OnKeyDown(e);
    }

    public SkinState State =>
        !_available ? SkinState.Disabled
        : _pressed ? SkinState.Pressed
        : _alternate ? SkinState.Alternate
        : _active ? SkinState.Active
        : _hover ? SkinState.Hover
        : SkinState.Normal;

    /// <summary>Toggle state: <paramref name="active"/> on, <paramref name="alternate"/> second on-state (repeat track).</summary>
    public void SetToggle(bool active, bool alternate = false)
    {
        if (IsToggle && active != _active)
        {
            Accessible.Changed(this, TogglePatternIdentifiers.ToggleStateProperty, _active ? ToggleState.On : ToggleState.Off, active ? ToggleState.On : ToggleState.Off);
        }
        _active = active;
        _alternate = alternate;
        UpdateVisual();
    }

    /// <summary>False draws the disabled state and ignores clicks (feature not available yet).</summary>
    public void SetAvailable(bool available)
    {
        _available = available;
        Cursor = available ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        UpdateVisual();
    }

    protected override void OnPointerEntered(PointerEventArgs e)
    {
        base.OnPointerEntered(e);
        _hover = true;
        UpdateVisual();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hover = false;
        UpdateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_available && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _pressed = true;
            e.Pointer.Capture(this);
            e.Handled = true;
            UpdateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_pressed)
        {
            return;
        }
        _pressed = false;
        e.Pointer.Capture(null);
        UpdateVisual();
        var p = e.GetPosition(this);
        if (p.X >= 0 && p.Y >= 0 && p.X <= Bounds.Width && p.Y <= Bounds.Height)
        {
            e.Handled = true;
            Clicked?.Invoke(this, EventArgs.Empty);
        }
    }

    private void UpdateVisual()
    {
        var state = State;
        var e = _element;
        _background.Background = _skin.Brush(e.Fills?.Get(state) ?? e.Fill);
        _background.BorderBrush = _skin.Brush(e.Strokes?.Get(state) ?? e.Stroke);
        var color = _skin.Color(e.Colors?.Get(state) ?? e.Color, Colors.White);
        _graphic.Show(_skin, e.ImageFor(state), color, _scale);
        if (_text is not null)
        {
            _text.Foreground = new SolidColorBrush(color);
            _text.Text = state == SkinState.Alternate && e.Bind == "repeat" ? e.Text + " 1" : e.Text;
        }
    }
}

/// <summary>A skinned horizontal slider (volume, seek): track, filled part and round thumb, drawn directly.</summary>
internal sealed class SkinSlider : Control, IAccessibleRange
{
    private readonly IBrush _track;
    private readonly IBrush _fill;
    private readonly IBrush _thumb;
    private readonly double _thumbSize;
    private readonly double _thickness;
    private double _value;
    private bool _dragging;
    private bool _available = true;

    public SkinSlider(Skin skin, SkinElement element)
    {
        _track = skin.Brush(element.TrackColor) ?? Brushes.Gray;
        _fill = skin.Brush(element.FillColor) ?? Brushes.White;
        _thumb = skin.Brush(element.ThumbColor) ?? _fill;
        _thumbSize = element.ThumbSize ?? 5;
        _thickness = element.TrackThickness ?? 1;
        Cursor = new Cursor(StandardCursorType.Hand);
        if (element.Tooltip is not null)
        {
            ToolTip.SetTip(this, element.Tooltip);
        }
        // TL-04: arrow keys move it (Up/Right more, Down/Left less), Page Up/Down in big steps, Home/End to the ends.
        Focusable = true;
        FocusAdorner = Accessible.FocusOutline(skin);
        Accessible.Describe(this, element);
    }

    /// <summary>TL-04: one arrow key press, as a fraction (0–1).</summary>
    public double SmallStep { get; set; } = 0.02;

    /// <summary>TL-04: one Page Up/Down press, as a fraction (0–1).</summary>
    public double LargeStep { get; set; } = 0.1;

    /// <summary>TL-04: how a screen reader reads the value (e.g. "1:23 of 5:59"); null reads the percentage.</summary>
    public Func<double, string>? DescribeValue { get; set; }

    double IAccessibleRange.Minimum => 0;

    double IAccessibleRange.Maximum => 100;

    double IAccessibleRange.Value => Math.Round(_value * 100, 1);

    double IAccessibleRange.SmallChange => SmallStep * 100;

    double IAccessibleRange.LargeChange => LargeStep * 100;

    bool IAccessibleRange.IsReadOnly => !_available;

    string? IAccessibleRange.ValueText => DescribeValue?.Invoke(_value) ?? Accessible.Percent(_value);

    void IAccessibleRange.SetValueFromAutomation(double value) => Commit(value / 100);

    protected override AutomationPeer OnCreateAutomationPeer() => new RangePeer(this, this);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_available && e.KeyModifiers == KeyModifiers.None)
        {
            double? target = e.Key switch
            {
                Key.Right or Key.Up => _value + SmallStep,
                Key.Left or Key.Down => _value - SmallStep,
                Key.PageUp => _value + LargeStep,
                Key.PageDown => _value - LargeStep,
                Key.Home => 0,
                Key.End => 1,
                _ => null,
            };
            if (target is { } v)
            {
                e.Handled = true;
                Commit(v);
            }
        }
        base.OnKeyDown(e);
    }

    /// <summary>A value chosen with the keyboard or a screen reader: shown, then reported like a click.</summary>
    private void Commit(double value)
    {
        var old = _value;
        _value = Math.Clamp(value, 0, 1);
        InvalidateVisual();
        Accessible.Changed(this, RangeValuePatternIdentifiers.ValueProperty, Math.Round(old * 100, 1), Math.Round(_value * 100, 1));
        ValueChanging?.Invoke(this, _value);
        ValueCommitted?.Invoke(this, _value);
    }

    /// <summary>While dragging: the value under the pointer (0–1).</summary>
    public event EventHandler<double>? ValueChanging;

    /// <summary>On release (or click): the chosen value (0–1).</summary>
    public event EventHandler<double>? ValueCommitted;

    public bool IsDragging => _dragging;

    /// <summary>0–1; ignored while the user drags.</summary>
    public double Value
    {
        get => _value;
        set
        {
            if (_dragging)
            {
                return;
            }
            _value = Math.Clamp(value, 0, 1);
            InvalidateVisual();
        }
    }

    public void SetAvailable(bool available)
    {
        _available = available;
        Cursor = available ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var r = _thumbSize / 2;
        var y = Bounds.Height / 2;
        var x0 = r;
        var x1 = Math.Max(x0, Bounds.Width - r);
        var x = x0 + ((x1 - x0) * _value);
        // Transparent fill over the whole bounds: custom-drawn controls are hit-tested by what they draw, so without
        // it only the thin line and the thumb would react to clicks (click-to-position needs the full area).
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        context.DrawLine(new Pen(_track, _thickness, lineCap: PenLineCap.Round), new Point(x0, y), new Point(x1, y));
        if (_available)
        {
            context.DrawLine(new Pen(_fill, _thickness, lineCap: PenLineCap.Round), new Point(x0, y), new Point(x, y));
            if (_thumbSize > 0)
            {
                context.DrawEllipse(_thumb, null, new Point(x, y), r, r);
            }
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!_available || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        _dragging = true;
        e.Pointer.Capture(this);
        e.Handled = true;
        Move(e.GetPosition(this).X);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            Move(e.GetPosition(this).X);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_dragging)
        {
            return;
        }
        _dragging = false;
        e.Pointer.Capture(null);
        e.Handled = true;
        ValueCommitted?.Invoke(this, _value);
    }

    private void Move(double x)
    {
        var r = _thumbSize / 2;
        var width = Math.Max(1, Bounds.Width - (2 * r));
        _value = Math.Clamp((x - r) / width, 0, 1);
        InvalidateVisual();
        ValueChanging?.Invoke(this, _value);
    }
}

/// <summary>CO-06: time display. Click toggles elapsed/remaining; blinks while paused.</summary>
internal sealed class TimeDisplay : Panel, IAccessibleAction
{
    private readonly TextBlock _text;
    private readonly DispatcherTimer _blink = new() { Interval = TimeSpan.FromMilliseconds(500) };

    public TimeDisplay(Skin skin, SkinElement element)
    {
        Background = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Hand);
        _text = new TextBlock
        {
            FontFamily = skin.FontFamily(element.Font),
            FontWeight = skin.FontWeight(element.Font),
            FontSize = skin.FontSize(element, 20),
            Foreground = skin.Brush(element.Color) ?? Brushes.White,
            LetterSpacing = element.LetterSpacing ?? 0,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = Align(element.Align),
            IsHitTestVisible = false,
        };
        Children.Add(_text);
        _blink.Tick += (_, _) => _text.Opacity = _text.Opacity > 0.5 ? 0.15 : 1;
        ToolTip.SetTip(this, "Elapsed / remaining time (click)");
        AutomationProperties.SetName(this, "Time display");
        FocusAdorner = Accessible.FocusOutline(skin);
    }

    public event EventHandler? Clicked;

    /// <summary>TL-04: a clickable time display (elapsed/remaining) takes keyboard focus and acts as a button.</summary>
    public bool IsClickable
    {
        get => Focusable;
        set
        {
            Focusable = value;
            AutomationProperties.SetHelpText(this, value ? "Press to switch between elapsed and remaining time" : string.Empty);
        }
    }

    /// <summary>TL-04: what the time is ("Elapsed time", "Track length").</summary>
    public string Label { get; set; } = "Time";

    public bool CanInvoke => IsClickable;

    public bool? IsOn => null;

    public void InvokeAction() => Clicked?.Invoke(this, EventArgs.Empty);

    protected override AutomationPeer OnCreateAutomationPeer() =>
        IsClickable ? new ActionPeer(this, this) : new TextPeer(this);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (IsClickable && e.Key is Key.Enter or Key.Space && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            Clicked?.Invoke(this, EventArgs.Empty);
        }
        base.OnKeyDown(e);
    }

    public string Text
    {
        get => _text.Text ?? string.Empty;
        set
        {
            _text.Text = value;
            AutomationProperties.SetName(this, string.IsNullOrEmpty(value) ? Label : $"{Label} {value}");
        }
    }

    public void SetBlinking(bool blinking)
    {
        if (blinking)
        {
            _blink.Start();
        }
        else
        {
            _blink.Stop();
            _text.Opacity = 1;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            e.Handled = true;
            Clicked?.Invoke(this, EventArgs.Empty);
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _blink.Stop();
    }

    internal static TextAlignment Align(string? align) => align switch
    {
        "center" => TextAlignment.Center,
        "right" => TextAlignment.Right,
        _ => TextAlignment.Left,
    };
}

/// <summary>UI-06: scrolling title. Scrolls smoothly when the text does not fit, pausing briefly at the start.</summary>
internal sealed class MarqueeText : Control
{
    private const double PauseSeconds = 1.5;
    private const string Gap = "     ·     ";
    private readonly Typeface _typeface;
    private readonly double _fontSize;
    private readonly IBrush _brush;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private FormattedText? _formatted;
    private FormattedText? _gap;
    private string _text = string.Empty;
    private double _offset;
    private double _pause = PauseSeconds;
    private long _lastTick;
    private bool _attached;

    public MarqueeText(Skin skin, SkinElement element)
    {
        _typeface = new Typeface(skin.FontFamily(element.Font), FontStyle.Normal, skin.FontWeight(element.Font));
        _fontSize = skin.FontSize(element, 8);
        _brush = skin.Brush(element.Color) ?? Brushes.White;
        Speed = element.Speed ?? 25;
        ClipToBounds = true;
        IsHitTestVisible = false;
        _timer.Tick += (_, _) => Advance();
        // TL-04: screen readers announce a new title (and the short notices shown in its place) when it changes.
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Polite);
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TextPeer(this);

    /// <summary>Pixels per second.</summary>
    public double Speed { get; set; }

    /// <summary>False keeps the title static (Settings › Interface: scroll title).</summary>
    public bool Scroll { get; set; } = true;

    public string Text
    {
        get => _text;
        set
        {
            if (value == _text)
            {
                return;
            }
            _text = value;
            _formatted = null;
            _offset = 0;
            _pause = PauseSeconds;
            AutomationProperties.SetName(this, value);
            InvalidateVisual();
            UpdateTimer();
        }
    }

    public override void Render(DrawingContext context)
    {
        _formatted ??= Format(_text);
        _gap ??= Format(Gap);
        var y = (Bounds.Height - _formatted.Height) / 2;
        if (!Overflows())
        {
            context.DrawText(_formatted, new Point(0, y));
            return;
        }
        var cycle = _formatted.WidthIncludingTrailingWhitespace + _gap.WidthIncludingTrailingWhitespace;
        context.DrawText(_formatted, new Point(-_offset, y));
        context.DrawText(_gap, new Point(-_offset + _formatted.WidthIncludingTrailingWhitespace, y));
        context.DrawText(_formatted, new Point(-_offset + cycle, y));
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        UpdateTimer();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        _timer.Stop();
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateTimer();
    }

    private bool Overflows()
    {
        _formatted ??= Format(_text);
        return Scroll && _formatted.WidthIncludingTrailingWhitespace > Bounds.Width + 0.5;
    }

    private void UpdateTimer()
    {
        if (Overflows() && Speed > 0 && _attached)
        {
            _lastTick = Environment.TickCount64;
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            _offset = 0;
        }
    }

    private void Advance()
    {
        var now = Environment.TickCount64;
        var dt = Math.Min(0.2, (now - _lastTick) / 1000.0);
        _lastTick = now;
        if (_pause > 0)
        {
            _pause -= dt;
            return;
        }
        _formatted ??= Format(_text);
        _gap ??= Format(Gap);
        var cycle = _formatted.WidthIncludingTrailingWhitespace + _gap.WidthIncludingTrailingWhitespace;
        _offset += Speed * dt;
        if (_offset >= cycle)
        {
            _offset = 0;
            _pause = PauseSeconds;
        }
        InvalidateVisual();
    }

    private FormattedText Format(string text) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, _fontSize, _brush);
}

/// <summary>
/// UI-06: format indicators (codec, bit depth, kbps, kHz, channels) as outlined pills. When a row of pills is wider
/// than the element, the whole row is drawn a little smaller so that every pill stays whole (it was cut off at the
/// right edge before).
/// </summary>
internal sealed class IndicatorBar : Decorator
{
    private readonly Skin _skin;
    private readonly SkinElement _element;
    private readonly StackPanel _row;

    public IndicatorBar(Skin skin, SkinElement element)
    {
        _skin = skin;
        _element = element;
        _row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = element.Spacing ?? 3 };
        Child = new Viewbox
        {
            Child = _row,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ClipToBounds = true;
        IsHitTestVisible = false;
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new TextPeer(this);

    public void SetItems(IReadOnlyList<string> items)
    {
        var children = _row.Children;
        children.Clear();
        foreach (var item in items)
        {
            children.Add(new Border
            {
                BorderBrush = _skin.Brush(_element.Stroke),
                BorderThickness = new Thickness(_element.Stroke is null ? 0 : 0.8),
                CornerRadius = new CornerRadius(_element.Height / 2),
                Padding = new Thickness(_element.Height / 2.5, 0),
                Height = _element.Height,
                Child = new TextBlock
                {
                    Text = item,
                    FontFamily = _skin.FontFamily(_element.Font),
                    FontWeight = _skin.FontWeight(_element.Font),
                    FontSize = _skin.FontSize(_element, 5.5),
                    Foreground = _skin.Brush(_element.Color) ?? Brushes.White,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            });
        }
        AutomationProperties.SetName(this, string.Join(", ", items).ToLowerInvariant().Replace("kbps", "kilobits per second", StringComparison.Ordinal).Replace("khz", "kilohertz", StringComparison.Ordinal));
    }
}
