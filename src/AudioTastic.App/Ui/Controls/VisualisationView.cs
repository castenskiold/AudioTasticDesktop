using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AudioTastic.App.Skins;
using AudioTastic.Core.Audio;
using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Data.Settings;

namespace AudioTastic.App.Ui.Controls;

/// <summary>
/// UI-07 (MW6): spectrum analyser (log-spaced bars with falling peak caps) or oscilloscope of the post-DSP audio.
/// Clicking cycles spectrum → oscilloscope → off. Draws at Settings › Interface › frame rate while attached; when no
/// new audio arrives (paused, stopped) the bars fall to zero.
/// Skin properties: fillColor (bars, scope line), thumbColor (peak caps), trackColor (idle baseline).
/// </summary>
internal sealed class VisualisationView : Control, IDisposable
{
    private readonly VisualisationFeed _feed;
    private readonly SettingsStore _settings;
    private readonly IBrush _bar;
    private readonly IBrush _peak;
    private readonly IBrush _idle;
    private readonly DispatcherTimer _timer = new();
    private readonly SpectrumAnalyzer _analyzer = new();
    private readonly float[] _samples = new float[SpectrumAnalyzer.Size * 2];
    private float[] _bars = [];
    private float[] _peaks = [];
    private float[] _shown = [];
    private VisualisationType _type;
    private int _barCount;
    private double _peakFall;
    private long _lastWritten = -1;
    private long _lastTick;

    public VisualisationView(Skin skin, SkinElement element, VisualisationFeed feed, SettingsStore settings)
    {
        _feed = feed;
        _settings = settings;
        _bar = skin.Brush(element.FillColor ?? element.Color) ?? Brushes.White;
        _peak = skin.Brush(element.ThumbColor ?? element.FillColor ?? element.Color) ?? Brushes.White;
        _idle = skin.Brush(element.TrackColor) ?? new SolidColorBrush(Color.FromArgb(80, 255, 255, 255));
        Cursor = new Cursor(StandardCursorType.Hand);
        ToolTip.SetTip(this, element.Tooltip ?? "Click: spectrum, oscilloscope, off");
        Avalonia.Automation.AutomationProperties.SetName(this, "Visualisation");
        _timer.Tick += (_, _) => Tick();
        settings.Changed += OnSettingsChanged;
        ApplySettings(settings.Current.Interface);
    }

    public void Dispose()
    {
        _timer.Stop();
        _settings.Changed -= OnSettingsChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _timer.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _timer.Stop();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }
        e.Handled = true;
        var next = (VisualisationType)(((int)_type + 1) % 3);
        _settings.Update(SettingsGroup.Interface, s => s.Interface.Visualisation = next);
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Group == SettingsGroup.Interface)
        {
            var ui = e.Settings.Interface;
            Dispatcher.UIThread.Post(() => ApplySettings(ui));
        }
    }

    private void ApplySettings(InterfaceSettings ui)
    {
        _type = ui.Visualisation;
        _barCount = Math.Clamp(ui.SpectrumBars, 8, 64);
        _peakFall = Math.Clamp(ui.PeakFallSpeed, 0.1, 5);
        _timer.Interval = TimeSpan.FromMilliseconds(1000.0 / Math.Clamp(ui.VisualisationFps, 15, 60));
        if (_bars.Length != _barCount)
        {
            _bars = new float[_barCount];
            _peaks = new float[_barCount];
            _shown = new float[_barCount];
        }
        InvalidateVisual();
    }

    private void Tick()
    {
        var now = Environment.TickCount64;
        var dt = _lastTick == 0 ? 0.016 : Math.Min(0.1, (now - _lastTick) / 1000.0);
        _lastTick = now;
        if (_type == VisualisationType.Off)
        {
            return;
        }
        var written = _feed.WrittenFrames;
        var fresh = written != _lastWritten;
        _lastWritten = written;
        if (fresh)
        {
            _feed.ReadLatest(_samples);
            if (_type == VisualisationType.Spectrum)
            {
                _analyzer.Analyze(_samples, _feed.SampleRate, _bars);
            }
        }
        else
        {
            Array.Clear(_bars);
        }
        if (_type == VisualisationType.Spectrum)
        {
            // Bars rise at once and fall smoothly; peak caps hold and fall at the set speed.
            var fall = (float)(dt * 3);
            var capFall = (float)(dt * _peakFall);
            for (var i = 0; i < _bars.Length; i++)
            {
                _shown[i] = Math.Max(_bars[i], _shown[i] - fall);
                _peaks[i] = Math.Max(_shown[i], _peaks[i] - capFall);
            }
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size)); // clickable everywhere
        switch (_type)
        {
            case VisualisationType.Spectrum:
            {
                var slot = w / _shown.Length;
                var barWidth = Math.Max(0.5, slot - Math.Max(0.5, slot * 0.25));
                for (var i = 0; i < _shown.Length; i++)
                {
                    var x = i * slot;
                    var bh = _shown[i] * (h - 1.5);
                    if (bh > 0.2)
                    {
                        context.FillRectangle(_bar, new Rect(x, h - bh, barWidth, bh));
                    }
                    if (_peaks[i] > 0.01)
                    {
                        var py = h - (_peaks[i] * (h - 1.5)) - 1;
                        context.FillRectangle(_peak, new Rect(x, Math.Max(0, py), barWidth, 0.6));
                    }
                    else
                    {
                        context.FillRectangle(_idle, new Rect(x, h - 0.5, barWidth, 0.5));
                    }
                }
                break;
            }
            case VisualisationType.Oscilloscope:
            {
                var mid = h / 2;
                if (_lastWritten <= 0)
                {
                    context.DrawLine(new Pen(_idle, 0.6), new Point(0, mid), new Point(w, mid));
                    break;
                }
                // The newest ~23 ms (1024 frames at 44.1 kHz) across the width.
                const int frames = 1024;
                var start = SpectrumAnalyzer.Size - frames;
                var geometry = new StreamGeometry();
                using (var g = geometry.Open())
                {
                    var points = Math.Max(2, (int)(w * 2));
                    for (var i = 0; i < points; i++)
                    {
                        var f = start + (i * (frames - 1) / (points - 1));
                        var v = (_samples[f * 2] + _samples[(f * 2) + 1]) * 0.5f;
                        var p = new Point(w * i / (points - 1), mid - (Math.Clamp(v, -1, 1) * (mid - 0.5)));
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
                context.DrawGeometry(null, new Pen(_bar, 0.7, lineJoin: PenLineJoin.Round), geometry);
                break;
            }
        }
    }
}
