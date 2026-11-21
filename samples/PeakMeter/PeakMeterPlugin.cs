using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace PeakMeter;

/// <summary>
/// A visualisation plugin: each instance gets post-DSP audio and draws it in its own control, or (SDK 0.4.2) into a
/// frame buffer when the player casts it to a TV.
/// </summary>
public sealed class PeakMeterPlugin : IVisualisationPlugin
{
    public string Name => "Peak meter";

    public bool CanRenderOffscreen => true;

    public void Initialize(IPluginContext context)
    {
    }

    public IVisualisation Create() => new PeakMeterVisualisation();

    public void Dispose()
    {
    }
}

internal sealed class PeakMeterVisualisation : IVisualisation, IOffscreenVisualisation
{
    private MeterView? _view; // created by CreateView (UI thread); a cast instance never gets one
    private float _left;
    private float _right;

    public void OnAudio(ReadOnlySpan<float> samples, AudioFormat format)
    {
        float left = 0, right = 0;
        for (var i = 0; i + 1 < samples.Length; i += format.Channels)
        {
            left = Math.Max(left, Math.Abs(samples[i]));
            right = Math.Max(right, Math.Abs(samples[format.Channels > 1 ? i + 1 : i]));
        }
        _left = Math.Max(left, _left * 0.85f);
        _right = Math.Max(right, _right * 0.85f);
        _view?.Set(left, right);
    }

    public object CreateView() => _view ??= new MeterView();

    /// <summary>Off-screen (cast): two white bars on black, drawn straight into the BGRA pixels.</summary>
    public void RenderFrame(Span<byte> pixels, int width, int height, TimeSpan time)
    {
        pixels.Clear(); // black, opaque below
        var barHeight = height / 5;
        Bar(pixels, width, height, (height / 2) - barHeight - (barHeight / 4), barHeight, _left);
        Bar(pixels, width, height, (height / 2) + (barHeight / 4), barHeight, _right);
        for (var i = 3; i < pixels.Length; i += 4)
        {
            pixels[i] = 255;
        }
    }

    private static void Bar(Span<byte> pixels, int width, int height, int top, int barHeight, float level)
    {
        var margin = width / 10;
        var length = (int)((width - (2 * margin)) * Math.Clamp(level, 0, 1));
        for (var y = Math.Max(0, top); y < Math.Min(height, top + barHeight); y++)
        {
            pixels.Slice(((y * width) + margin) * 4, length * 4).Fill(255);
        }
    }

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
