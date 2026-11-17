using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PeakMeter;

/// <summary>A visualisation plugin: each instance gets post-DSP audio and draws it in its own control.</summary>
public sealed class PeakMeterPlugin : IVisualisationPlugin
{
    public string Name => "Peak meter";

    public void Initialize(IPluginContext context)
    {
    }

    public IVisualisation Create() => new PeakMeterVisualisation();

    public void Dispose()
    {
    }
}

internal sealed class PeakMeterVisualisation : IVisualisation
{
    private readonly MeterView _view = new();

    public void OnAudio(ReadOnlySpan<float> samples, AudioFormat format)
    {
        float left = 0, right = 0;
        for (var i = 0; i + 1 < samples.Length; i += format.Channels)
        {
            left = Math.Max(left, Math.Abs(samples[i]));
            right = Math.Max(right, Math.Abs(samples[format.Channels > 1 ? i + 1 : i]));
        }
        _view.Set(left, right);
    }

    public object CreateView() => _view;

    public void Dispose()
    {
    }
}

internal sealed class MeterView : Control
{
    private float _left;
    private float _right;

    public void Set(float left, float right)
    {
        // Simple fall-off so the bars do not flicker.
        _left = Math.Max(left, _left * 0.85f);
        _right = Math.Max(right, _right * 0.85f);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height / 2 - 1;
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        context.FillRectangle(Brushes.White, new Rect(0, 0, w * Math.Min(1, _left), h));
        context.FillRectangle(Brushes.White, new Rect(0, h + 2, w * Math.Min(1, _right), h));
    }
}
