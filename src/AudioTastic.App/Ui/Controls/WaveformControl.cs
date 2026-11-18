using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AudioTastic.Analysis;
using AudioTastic.App.Skins;
using AudioTastic.Core.Audio;

namespace AudioTastic.App.Ui.Controls;

/// <summary>
/// CO-07 (MW7): the waveform seek bar. Draws the whole track as min/max peaks (all channels folded together,
/// scaled to the track's own peak), the played part in the skin's fill colour and the rest in the track colour,
/// with a position line. Click or drag to seek. While the waveform is being made, a thin centre line shows the
/// progress; streams show a scrolling live waveform. Skin properties: trackColor, fillColor, thumbColor.
/// </summary>
public sealed class WaveformControl : Control, IDisposable
{
    private const double ColumnWidth = 0.5;   // layout units; crisp at 150 % and double size
    private readonly IBrush _track;
    private readonly IBrush _fill;
    private readonly IBrush _thumb;
    private readonly DispatcherTimer _liveTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private readonly float[] _liveMin = new float[PeakTap.Capacity];
    private readonly float[] _liveMax = new float[PeakTap.Capacity];
    private PeakTap? _peaks;
    private Waveform? _waveform;
    private float _waveformPeak = 1;
    private double? _progress;
    private double _position;
    private double? _dragPosition;
    private bool _live;
    private bool _canSeek;
    private double? _loopA;   // 0-1
    private double? _loopB;
    private int _dragMarker;   // 0 = none, 1 = A, 2 = B

    public WaveformControl(Skin skin, SkinElement element)
    {
        _track = skin.Brush(element.TrackColor) ?? Brushes.Gray;
        _fill = skin.Brush(element.FillColor) ?? Brushes.White;
        _thumb = skin.Brush(element.ThumbColor) ?? _fill;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        Avalonia.Automation.AutomationProperties.SetName(this, "Waveform and seek bar");
        _liveTimer.Tick += (_, _) => InvalidateVisual();
    }

    /// <summary>Raised with the chosen position (0–1) when the user clicks or finishes dragging.</summary>
    public event EventHandler<double>? SeekRequested;

    /// <summary>PB-08: the A or B marker was dragged; (A, B) as fractions 0–1.</summary>
    public event EventHandler<(double A, double B)>? LoopChanged;

    /// <summary>PB-08: shows the A-B loop (fractions 0–1; B null while only A is set).</summary>
    public void SetLoop(double? a, double? b)
    {
        if (_dragMarker == 0 && (a != _loopA || b != _loopB))
        {
            _loopA = a;
            _loopB = b;
            InvalidateVisual();
        }
    }

    /// <summary>Updates what is shown; called on every player update (about 10 times a second).</summary>
    public void Update(Waveform? waveform, double? progress, double position, bool canSeek, bool live, PeakTap? peaks, bool playing)
    {
        if (!ReferenceEquals(waveform, _waveform))
        {
            _waveform = waveform;
            _waveformPeak = waveform is null ? 1 : Math.Max(0.1f, PeakOf(waveform));
        }
        _progress = progress;
        _position = Math.Clamp(position, 0, 1);
        _canSeek = canSeek;
        _live = live;
        _peaks = peaks;
        Cursor = new Cursor(canSeek ? StandardCursorType.Hand : StandardCursorType.Arrow);
        if (live && playing)
        {
            _liveTimer.Start();
        }
        else
        {
            _liveTimer.Stop();
        }
        InvalidateVisual();
    }

    public void Dispose() => _liveTimer.Stop();

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // the whole strip is clickable
        var mid = h / 2;
        if (_live && _peaks is not null)
        {
            RenderLive(context, w, h);
            return;
        }
        if (_waveform is null)
        {
            // No waveform (yet): a thin centre line; while it is being made, the analysed part is brighter.
            context.FillRectangle(_track, new Rect(0, mid - 0.5, w, 1));
            if (_progress is { } p)
            {
                context.FillRectangle(_fill, new Rect(0, mid - 0.5, w * p, 1));
            }
        }
        else
        {
            RenderWaveform(context, w, h);
        }
        if (_loopA is { } la)
        {
            // PB-08: the loop region, lightly shaded, with A and B markers (small flags at the top).
            var ax = la * w;
            var bx = (_loopB ?? la) * w;
            if (_loopB is not null)
            {
                using (context.PushOpacity(0.18))
                {
                    context.FillRectangle(_thumb, new Rect(ax, 0, Math.Max(1, bx - ax), h));
                }
            }
            foreach (var mx in _loopB is null ? new[] { ax } : [ax, bx])
            {
                context.FillRectangle(_thumb, new Rect(Math.Min(mx, w - 1), 0, 1, h));
                context.FillRectangle(_thumb, new Rect(Math.Min(mx, w - 3), 0, 3, 3));
            }
        }
        if (_canSeek)
        {
            var x = Math.Round((_dragPosition ?? _position) * w * 2) / 2;
            context.FillRectangle(_thumb, new Rect(Math.Min(x, w - 1), 0, 1, h));
        }
    }

    private void RenderWaveform(DrawingContext context, double w, double h)
    {
        var wf = _waveform!;
        var columns = Math.Max(1, (int)(w / ColumnWidth));
        var played = new StreamGeometry();
        var rest = new StreamGeometry();
        var split = (_dragPosition ?? _position) * columns;
        var scale = (h / 2) - 0.5;
        using (var p = played.Open())
        using (var r = rest.Open())
        {
            for (var col = 0; col < columns; col++)
            {
                var from = col * wf.Buckets / columns;
                var to = Math.Max(from + 1, (col + 1) * wf.Buckets / columns);
                float lo = 0, hi = 0;
                for (var b = from; b < to && b < wf.Buckets; b++)
                {
                    for (var c = 0; c < wf.Channels; c++)
                    {
                        lo = Math.Min(lo, wf.Min[c][b]);
                        hi = Math.Max(hi, wf.Max[c][b]);
                    }
                }
                var top = (h / 2) - (hi / _waveformPeak * scale);
                var bottom = (h / 2) - (lo / _waveformPeak * scale);
                if (bottom - top < 0.5)
                {
                    top -= 0.25; // silence still shows as a hairline
                    bottom += 0.25;
                }
                var target = col < split ? p : r;
                var x = col * ColumnWidth;
                target.BeginFigure(new Point(x, top), true);
                target.LineTo(new Point(x + ColumnWidth, top));
                target.LineTo(new Point(x + ColumnWidth, bottom));
                target.LineTo(new Point(x, bottom));
                target.EndFigure(true);
            }
        }
        context.DrawGeometry(_track, null, rest);
        context.DrawGeometry(_fill, null, played);
    }

    private void RenderLive(DrawingContext context, double w, double h)
    {
        // The newest 10 ms pairs scroll in from the right, one column each.
        var columns = Math.Min(PeakTap.Capacity, Math.Max(1, (int)(w / ColumnWidth)));
        var count = _peaks!.ReadLatest(_liveMin.AsSpan(0, columns), _liveMax.AsSpan(0, columns));
        var geometry = new StreamGeometry();
        var scale = (h / 2) - 0.5;
        using (var g = geometry.Open())
        {
            for (var i = 0; i < count; i++)
            {
                var x = w - ((count - i) * ColumnWidth);
                var top = (h / 2) - (Math.Min(1, _liveMax[i]) * scale);
                var bottom = (h / 2) - (Math.Max(-1, _liveMin[i]) * scale);
                if (bottom - top < 0.5)
                {
                    top -= 0.25;
                    bottom += 0.25;
                }
                g.BeginFigure(new Point(x, top), true);
                g.LineTo(new Point(x + ColumnWidth, top));
                g.LineTo(new Point(x + ColumnWidth, bottom));
                g.LineTo(new Point(x, bottom));
                g.EndFigure(true);
            }
        }
        context.FillRectangle(_track, new Rect(0, (h / 2) - 0.5, w, 1));
        context.DrawGeometry(_fill, null, geometry);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_canSeek && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            e.Pointer.Capture(this);
            // PB-08: a press within 4 units of an A/B marker drags that marker.
            var px = e.GetPosition(this).X;
            if (_loopA is { } la && _loopB is { } lb && Bounds.Width > 0)
            {
                _dragMarker = Math.Abs(px - (la * Bounds.Width)) <= 4 ? 1 : Math.Abs(px - (lb * Bounds.Width)) <= 4 ? 2 : 0;
                if (_dragMarker != 0)
                {
                    e.Handled = true;
                    return;
                }
            }
            _dragPosition = PositionAt(e.GetPosition(this));
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_dragMarker != 0)
        {
            var f = PositionAt(e.GetPosition(this));
            if (_dragMarker == 1)
            {
                _loopA = Math.Min(f, _loopB ?? 1);
            }
            else
            {
                _loopB = Math.Max(f, _loopA ?? 0);
            }
            InvalidateVisual();
            return;
        }
        if (_dragPosition is not null)
        {
            _dragPosition = PositionAt(e.GetPosition(this));
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_dragMarker != 0)
        {
            _dragMarker = 0;
            e.Pointer.Capture(null);
            if (_loopA is { } a && _loopB is { } b)
            {
                LoopChanged?.Invoke(this, (a, b));
            }
            e.Handled = true;
            return;
        }
        if (_dragPosition is { } position)
        {
            _dragPosition = null;
            e.Pointer.Capture(null);
            _position = position;
            SeekRequested?.Invoke(this, position);
            InvalidateVisual();
            e.Handled = true;
        }
    }

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        _dragPosition = null;
        _dragMarker = 0;
        InvalidateVisual();
    }

    private double PositionAt(Point p) => Bounds.Width <= 0 ? 0 : Math.Clamp(p.X / Bounds.Width, 0, 1);

    private static float PeakOf(Waveform wf)
    {
        float peak = 0;
        for (var c = 0; c < wf.Channels; c++)
        {
            for (var b = 0; b < wf.Buckets; b++)
            {
                peak = Math.Max(peak, Math.Max(wf.Max[c][b], -wf.Min[c][b]));
            }
        }
        return peak;
    }
}
