using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using AudioTastic.App.Skins;
using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Data.Settings;

namespace AudioTastic.App.Ui.Equalizer;

/// <summary>EQ-01 / EQ-04: creates the equalizer window's own element types (eqSlider, eqCurve, eqParametric).</summary>
public static class EqualizerViews
{
    public static Control? Create(Skin skin, SkinElement element, EqualizerController controller) => element.Type switch
    {
        "eqSlider" => new EqSlider(skin, element, controller),
        "eqCurve" => new EqCurveView(skin, element, controller),
        "eqParametric" => new ParametricEditor(skin, element, controller),
        _ => null,
    };

    internal static string Hz(double f) => f >= 1000
        ? string.Create(CultureInfo.InvariantCulture, $"{f / 1000:0.##} kHz")
        : string.Create(CultureInfo.InvariantCulture, $"{f:0} Hz");

    internal static string Db(double db) => string.Create(CultureInfo.InvariantCulture, $"{db:+0.0;-0.0;0.0} dB");
}

/// <summary>
/// EQ-01 (EQ4): a vertical ±12 dB slider for the preamp ("preamp") or one band ("band0".."band9"). Drag or click to
/// set (snaps to 0 near the middle), wheel for 0.5 dB steps, double-click resets to 0 dB. Hidden in parametric mode.
/// </summary>
internal sealed class EqSlider : Control, IDisposable
{
    private const double Range = 12;
    private readonly EqualizerController _controller;
    private readonly IBrush _track;
    private readonly IBrush _fill;
    private readonly IBrush _thumb;
    private readonly double _thumbSize;
    private readonly int _band; // -1 = preamp
    private bool _dragging;

    public EqSlider(Skin skin, SkinElement element, EqualizerController controller)
    {
        _controller = controller;
        _track = skin.Brush(element.TrackColor) ?? Brushes.Gray;
        _fill = skin.Brush(element.FillColor) ?? Brushes.White;
        _thumb = skin.Brush(element.ThumbColor) ?? _fill;
        _thumbSize = element.ThumbSize ?? 5;
        _band = element.Bind == "preamp" ? -1 : int.Parse(element.Bind!.AsSpan(4), CultureInfo.InvariantCulture);
        Cursor = new Cursor(StandardCursorType.Hand);
        Avalonia.Automation.AutomationProperties.SetName(this, _band < 0 ? "Preamp" : $"{EqualizerViews.Hz(EqualizerSettings.GraphicFrequencies[_band])} band");
        controller.Changed += OnChanged;
        OnChanged(null, EventArgs.Empty);
    }

    private double Value => _band < 0 ? _controller.Current.PreampDb : _controller.Current.Bands.ElementAtOrDefault(_band);

    public void Dispose() => _controller.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e)
    {
        IsVisible = !_controller.IsParametric;
        ToolTip.SetTip(this, $"{(_band < 0 ? "Preamp" : EqualizerViews.Hz(EqualizerSettings.GraphicFrequencies[_band]))}: {EqualizerViews.Db(Value)}" +
            "\nDrag, wheel; double-click resets");
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var r = _thumbSize / 2;
        var x = Bounds.Width / 2;
        var top = r;
        var bottom = Math.Max(top, Bounds.Height - r);
        var mid = (top + bottom) / 2;
        var y = mid - ((bottom - top) / 2 * Math.Clamp(Value / Range, -1, 1));
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));
        context.DrawLine(new Pen(_track, 1, lineCap: PenLineCap.Round), new Point(x, top), new Point(x, bottom));
        context.DrawLine(new Pen(_track, 1), new Point(x - 2.5, mid), new Point(x + 2.5, mid));
        context.DrawLine(new Pen(_fill, 1.2, lineCap: PenLineCap.Round), new Point(x, mid), new Point(x, y));
        context.DrawEllipse(_thumb, null, new Point(x, y), r, r);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        e.Handled = true;
        if (e.ClickCount == 2)
        {
            _controller.Change(eq => Set(eq, 0));
            return;
        }
        _dragging = true;
        e.Pointer.Capture(this);
        Move(e.GetPosition(this).Y);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragging)
        {
            Move(e.GetPosition(this).Y);
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            _controller.Commit();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var step = e.Delta.Y > 0 ? 0.5 : -0.5;
        _controller.Change(eq => Set(eq, Math.Clamp(Math.Round((Get(eq) + step) * 2) / 2, -Range, Range)));
        e.Handled = true;
    }

    private void Move(double y)
    {
        var r = _thumbSize / 2;
        var height = Math.Max(1, Bounds.Height - (2 * r));
        var db = Math.Clamp((0.5 - ((y - r) / height)) * 2 * Range, -Range, Range);
        db = Math.Abs(db) < 0.4 ? 0 : Math.Round(db * 10) / 10; // snap to 0 near the middle
        _controller.Preview(eq => Set(eq, db));
    }

    private double Get(EqualizerSettings eq) => _band < 0 ? eq.PreampDb : eq.Bands[_band];

    private void Set(EqualizerSettings eq, double db)
    {
        if (_band < 0)
        {
            eq.PreampDb = db;
        }
        else
        {
            if (eq.Bands.Length < 10)
            {
                eq.Bands = [.. eq.Bands, .. new double[10 - eq.Bands.Length]];
            }
            eq.Bands[_band] = db;
        }
    }
}

/// <summary>EQ-01 (EQ3): the live response curve (dimmed while the equalizer is off), with the preset name.</summary>
internal sealed class EqCurveView : Control, IDisposable
{
    private readonly EqualizerController _controller;
    private readonly IBrush _line;
    private readonly IBrush _curve;
    private readonly IBrush _text;
    private readonly Typeface _typeface;
    private readonly double _fontSize;

    public EqCurveView(Skin skin, SkinElement element, EqualizerController controller)
    {
        _controller = controller;
        _line = skin.Brush(element.TrackColor) ?? Brushes.DimGray;
        _curve = skin.Brush(element.FillColor) ?? Brushes.White;
        _text = skin.Brush(element.Color) ?? Brushes.Gray;
        _typeface = new Typeface(skin.FontFamily(element.Font), FontStyle.Normal, skin.FontWeight(element.Font));
        _fontSize = skin.FontSize(element, 5.5);
        IsHitTestVisible = false;
        controller.Changed += OnChanged;
    }

    public void Dispose() => _controller.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e) => InvalidateVisual();

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        var range = _controller.IsParametric ? 24.0 : 12.0;
        context.DrawLine(new Pen(_line, 0.6), new Point(0, h / 2), new Point(w, h / 2));
        var design = _controller.Design;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var first = true;
            var steps = Math.Max(16, (int)w);
            for (var i = 0; i <= steps; i++)
            {
                var f = 20 * Math.Pow(1000, i / (double)steps);
                var db = f < 0.49 * design.SampleRate ? design.ResponseDb(f) : design.ResponseDb(0.49 * design.SampleRate);
                var y = (h / 2) - (Math.Clamp(db / range, -1, 1) * ((h / 2) - 1));
                var p = new Point(w * i / steps, y);
                if (first)
                {
                    g.BeginFigure(p, isFilled: false);
                    first = false;
                }
                else
                {
                    g.LineTo(p);
                }
            }
            g.EndFigure(isClosed: false);
        }
        using (context.PushOpacity(_controller.IsOn ? 1 : 0.35))
        {
            context.DrawGeometry(null, new Pen(_curve, 1, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
        var label = _controller.Current.Preset ?? (_controller.IsParametric ? "Parametric" : null);
        if (label is not null)
        {
            var text = new FormattedText(label.ToUpperInvariant(), CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, _fontSize, _text);
            context.DrawText(text, new Point(w - text.Width - 1, 0));
        }
    }
}

/// <summary>
/// EQ-04: parametric editor (shown with PARAM). Drag a point to change its frequency and gain, wheel for Q,
/// double-click empty space to add a band, right-click for type, on/off, delete and the band table.
/// </summary>
internal sealed class ParametricEditor : Control, IDisposable
{
    private const double Range = 24;
    private const double Radius = 3.2;
    private readonly EqualizerController _controller;
    private readonly IBrush _background;
    private readonly IBrush _grid;
    private readonly IBrush _curve;
    private readonly IBrush _point;
    private readonly IBrush _text;
    private readonly Typeface _typeface;
    private readonly double _fontSize;
    private int _selected = -1;
    private bool _dragging;

    public ParametricEditor(Skin skin, SkinElement element, EqualizerController controller)
    {
        _controller = controller;
        _background = skin.Brush(element.Fill) ?? Brushes.Black;
        _grid = skin.Brush(element.TrackColor) ?? Brushes.DimGray;
        _curve = skin.Brush(element.FillColor) ?? Brushes.White;
        _point = skin.Brush(element.ThumbColor) ?? Brushes.White;
        _text = skin.Brush(element.Color) ?? Brushes.Gray;
        _typeface = new Typeface(skin.FontFamily(element.Font), FontStyle.Normal, skin.FontWeight(element.Font));
        _fontSize = skin.FontSize(element, 5.5);
        ClipToBounds = true;
        Avalonia.Automation.AutomationProperties.SetName(this, "Parametric equalizer");
        controller.Changed += OnChanged;
        OnChanged(null, EventArgs.Empty);
    }

    private List<ParametricBand> Bands => _controller.Current.Parametric;

    /// <summary>The plot area (the bottom line holds the band details).</summary>
    private Rect Plot => new(4, 3, Math.Max(1, Bounds.Width - 8), Math.Max(1, Bounds.Height - 14));

    public void Dispose() => _controller.Changed -= OnChanged;

    private void OnChanged(object? sender, EventArgs e)
    {
        IsVisible = _controller.IsParametric;
        if (_selected >= Bands.Count)
        {
            _selected = Bands.Count - 1;
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(_background, new Rect(Bounds.Size));
        var plot = Plot;
        var gridPen = new Pen(_grid, 0.5);
        foreach (var f in new double[] { 100, 1000, 10000 })
        {
            var x = X(f, plot);
            context.DrawLine(gridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            Label(context, EqualizerViews.Hz(f).Replace(" ", string.Empty, StringComparison.Ordinal), new Point(x + 1, plot.Bottom - _fontSize - 1));
        }
        foreach (var db in new double[] { -12, 0, 12 })
        {
            var y = Y(db, plot);
            context.DrawLine(db == 0 ? new Pen(_grid, 0.8) : gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
        }
        // Curve of all enabled bands (without the automatic headroom, so the drawn points sit on it).
        var rate = _controller.Dsp.SampleRate;
        var filters = Bands.Where(b => b.Enabled).Select(b => Biquad.Design(b.Type, b.Frequency, b.GainDb, b.Q, rate)).ToList();
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            var steps = Math.Max(32, (int)plot.Width);
            for (var i = 0; i <= steps; i++)
            {
                var f = 20 * Math.Pow(1000, i / (double)steps);
                var db = _controller.Current.PreampDb + filters.Sum(q => q.MagnitudeDb(Math.Min(f, 0.49 * rate), rate));
                var p = new Point(plot.Left + (plot.Width * i / steps), Y(db, plot));
                if (i == 0)
                {
                    g.BeginFigure(p, isFilled: false);
                }
                else
                {
                    g.LineTo(p);
                }
            }
            g.EndFigure(isClosed: false);
        }
        using (context.PushOpacity(_controller.IsOn ? 1 : 0.4))
        {
            context.DrawGeometry(null, new Pen(_curve, 1, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
        for (var i = 0; i < Bands.Count; i++)
        {
            var band = Bands[i];
            var c = Center(band, plot);
            using (context.PushOpacity(band.Enabled ? 1 : 0.35))
            {
                if (i == _selected)
                {
                    context.DrawEllipse(_point, null, c, Radius + 0.6, Radius + 0.6);
                    Label(context, (i + 1).ToString(CultureInfo.InvariantCulture), new Point(c.X - 1.6, c.Y - (_fontSize / 2) - 0.6), _background);
                }
                else
                {
                    context.DrawEllipse(_background, new Pen(_point, 0.8), c, Radius, Radius);
                    Label(context, (i + 1).ToString(CultureInfo.InvariantCulture), new Point(c.X - 1.6, c.Y - (_fontSize / 2) - 0.6));
                }
            }
        }
        var info = _selected >= 0 && _selected < Bands.Count
            ? Describe(_selected, Bands[_selected])
            : "Drag points · wheel = Q · double-click adds a band · right-click for more";
        Label(context, info, new Point(plot.Left, Bounds.Height - _fontSize - 2));
    }

    internal static string Describe(int index, ParametricBand b) =>
        $"{index + 1}  {TypeName(b.Type)}  {EqualizerViews.Hz(b.Frequency)}" +
        (HasGain(b.Type) ? $"  {EqualizerViews.Db(b.GainDb)}" : string.Empty) +
        string.Create(CultureInfo.InvariantCulture, $"  Q {b.Q:0.00}") + (b.Enabled ? string.Empty : "  (off)");

    internal static string TypeName(FilterType type) => type switch
    {
        FilterType.LowShelf => "Low shelf",
        FilterType.HighShelf => "High shelf",
        FilterType.LowPass => "Low pass",
        FilterType.HighPass => "High pass",
        _ => type.ToString(),
    };

    internal static bool HasGain(FilterType type) => type is FilterType.Peak or FilterType.LowShelf or FilterType.HighShelf;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var point = e.GetCurrentPoint(this);
        var hit = HitTest(point.Position);
        e.Handled = true;
        if (point.Properties.IsRightButtonPressed)
        {
            _selected = hit;
            InvalidateVisual();
            ShowMenu(hit, point.Position);
            return;
        }
        if (!point.Properties.IsLeftButtonPressed)
        {
            return;
        }
        if (hit < 0)
        {
            if (e.ClickCount == 2)
            {
                AddBand(point.Position);
            }
            return;
        }
        _selected = hit;
        _dragging = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_dragging || _selected < 0)
        {
            return;
        }
        var plot = Plot;
        var p = e.GetPosition(this);
        var f = Math.Clamp(20 * Math.Pow(1000, (p.X - plot.Left) / plot.Width), 20, 20000);
        f = f >= 1000 ? Math.Round(f / 10) * 10 : Math.Round(f);
        var db = Math.Round(Math.Clamp((plot.Center.Y - p.Y) / (plot.Height / 2) * Range, -Range, Range) * 10) / 10;
        var index = _selected;
        _controller.Preview(eq => eq.Parametric[index] = eq.Parametric[index] with
        {
            Frequency = f,
            GainDb = HasGain(eq.Parametric[index].Type) ? db : eq.Parametric[index].GainDb,
        });
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragging)
        {
            _dragging = false;
            e.Pointer.Capture(null);
            _controller.Commit();
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var hit = HitTest(e.GetPosition(this));
        var index = hit >= 0 ? hit : _selected;
        if (index < 0)
        {
            return;
        }
        _selected = index;
        var factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
        _controller.Change(eq => eq.Parametric[index] = eq.Parametric[index] with
        {
            Q = Math.Round(Math.Clamp(eq.Parametric[index].Q * factor, 0.1, 10), 2),
        });
        e.Handled = true;
    }

    private void AddBand(Point p)
    {
        if (Bands.Count >= EqualizerDesign.MaxBands)
        {
            return;
        }
        var plot = Plot;
        var f = Math.Round(Math.Clamp(20 * Math.Pow(1000, (p.X - plot.Left) / plot.Width), 20, 20000));
        var db = Math.Round(Math.Clamp((plot.Center.Y - p.Y) / (plot.Height / 2) * Range, -Range, Range) * 10) / 10;
        _controller.Change(eq => eq.Parametric = [.. eq.Parametric, new ParametricBand(FilterType.Peak, f, db, 1)]);
        _selected = Bands.Count - 1;
    }

    private void ShowMenu(int index, Point p)
    {
        var items = new List<object>();
        if (index >= 0)
        {
            var band = Bands[index];
            items.Add(new MenuItem
            {
                Header = "Type",
                ItemsSource = Enum.GetValues<FilterType>().Select(t => Item(TypeName(t), () => Update(index, b => b with { Type = t }), t == band.Type)).ToList(),
            });
            items.Add(Item(band.Enabled ? "Switch off" : "Switch on", () => Update(index, b => b with { Enabled = !b.Enabled })));
            items.Add(Item("Delete band", () =>
            {
                _controller.Change(eq => eq.Parametric.RemoveAt(index));
                _selected = -1;
            }));
            items.Add(new Separator());
        }
        else
        {
            items.Add(Item("Add band here", () => AddBand(p), enabled: Bands.Count < EqualizerDesign.MaxBands));
        }
        items.Add(Item("Bands…", () =>
        {
            if (TopLevel.GetTopLevel(this) is Window owner)
            {
                _ = new ParametricBandsWindow(_controller).ShowDialog(owner);
            }
        }));
        items.Add(Item("Reset to default bands", () => _controller.Change(eq => eq.Parametric = ParametricBand.Defaults())));
        new ContextMenu { ItemsSource = items }.Open(this);

        static MenuItem Item(string header, Action action, bool? check = null, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled };
            if (check is { } c)
            {
                item.ToggleType = MenuItemToggleType.Radio;
                item.IsChecked = c;
            }
            item.Click += (_, _) => action();
            return item;
        }
    }

    private void Update(int index, Func<ParametricBand, ParametricBand> change) =>
        _controller.Change(eq => eq.Parametric[index] = change(eq.Parametric[index]));

    private int HitTest(Point p)
    {
        var plot = Plot;
        for (var i = Bands.Count - 1; i >= 0; i--)
        {
            var c = Center(Bands[i], plot);
            if (Math.Abs(c.X - p.X) <= Radius + 2 && Math.Abs(c.Y - p.Y) <= Radius + 2)
            {
                return i;
            }
        }
        return -1;
    }

    private static Point Center(ParametricBand b, Rect plot) => new(X(b.Frequency, plot), Y(HasGain(b.Type) ? b.GainDb : 0, plot));

    private static double X(double f, Rect plot) => plot.Left + (plot.Width * Math.Log(Math.Clamp(f, 20, 20000) / 20) / Math.Log(1000));

    private static double Y(double db, Rect plot) => plot.Center.Y - (Math.Clamp(db / Range, -1, 1) * plot.Height / 2);

    private void Label(DrawingContext context, string text, Point at, IBrush? brush = null) =>
        context.DrawText(new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, _typeface, _fontSize, brush ?? _text), at);
}
