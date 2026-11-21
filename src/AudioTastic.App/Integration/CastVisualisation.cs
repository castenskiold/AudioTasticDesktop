using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;
using SkiaSharp;

namespace AudioTastic.App.Integration;

/// <summary>
/// CN-09: the built-in spectrum analyser or oscilloscope drawn off-screen for a TV, in the default skin's style: white
/// on black, thin rounded lines, bars with falling peak caps (the same motion as the main window, SpectrumMotion).
/// Drawn with SkiaSharp into a bitmap and copied to the caller's BGRA frame.
/// </summary>
public sealed class CastVisualisation : IVisualisation, IOffscreenVisualisation
{
    private readonly VisualisationType _type;
    private readonly double _peakFall;
    private readonly SpectrumAnalyzer _analyzer = new();
    private readonly SpectrumMotion _motion = new();
    private readonly float[] _window = new float[SpectrumAnalyzer.Size * 2]; // newest stereo frames, oldest first
    private readonly float[] _levels;
    private readonly SKPaint _fill = new() { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _line = new() { Color = SKColors.White, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeCap = SKStrokeCap.Round, StrokeJoin = SKStrokeJoin.Round };
    private readonly SKPaint _idle = new() { Color = new SKColor(255, 255, 255, 70), IsAntialias = true, Style = SKPaintStyle.Stroke };
    private SKBitmap? _bitmap;
    private SKPoint[] _points = [];
    private int _sampleRate = 44_100;
    private bool _fresh;
    private bool _heard;
    private TimeSpan? _lastTime;

    /// <param name="type">Spectrum or oscilloscope (Off draws an empty black frame).</param>
    /// <param name="bars">Number of spectrum bars (a TV has room for more than the main window).</param>
    /// <param name="peakFall">Peak cap fall speed (Settings › Interface).</param>
    public CastVisualisation(VisualisationType type, int bars = 48, double peakFall = 1)
    {
        _type = type;
        _peakFall = Math.Clamp(peakFall, 0.1, 5);
        _levels = new float[Math.Clamp(bars, 8, 128)];
        _motion.Resize(_levels.Length);
    }

    /// <summary>Keeps the newest <see cref="SpectrumAnalyzer.Size"/> frames as stereo.</summary>
    public void OnAudio(ReadOnlySpan<float> samples, AudioFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        var frames = samples.Length / channels;
        if (frames == 0)
        {
            return;
        }
        _sampleRate = format.SampleRate;
        var take = Math.Min(frames, SpectrumAnalyzer.Size);
        var keep = SpectrumAnalyzer.Size - take;
        _window.AsSpan(take * 2).CopyTo(_window); // shift older frames to the front (only the newest are kept)
        var first = frames - take;
        for (var f = 0; f < take; f++)
        {
            var i = (first + f) * channels;
            var left = samples[i];
            var right = channels > 1 ? samples[i + 1] : left;
            _window[(keep + f) * 2] = left;
            _window[((keep + f) * 2) + 1] = right;
        }
        _fresh = true;
        _heard = true;
    }

    public object CreateView() => throw new NotSupportedException("The cast visualisation draws off-screen only.");

    public void RenderFrame(Span<byte> pixels, int width, int height, TimeSpan time)
    {
        var seconds = _lastTime is { } last ? Math.Clamp((time - last).TotalSeconds, 0, 0.25) : 1.0 / 30;
        _lastTime = time;
        if (_bitmap is null || _bitmap.Width != width || _bitmap.Height != height)
        {
            _bitmap?.Dispose();
            _bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        }
        using (var canvas = new SKCanvas(_bitmap))
        {
            canvas.Clear(SKColors.Black);
            switch (_type)
            {
                case VisualisationType.Spectrum:
                    DrawSpectrum(canvas, width, height, seconds);
                    break;
                case VisualisationType.Oscilloscope:
                    DrawScope(canvas, width, height);
                    break;
                default:
                    break;
            }
        }
        _bitmap.GetPixelSpan().CopyTo(pixels);
        _fresh = false;
    }

    private void DrawSpectrum(SKCanvas canvas, int width, int height, double seconds)
    {
        if (_fresh)
        {
            _analyzer.Analyze(_window, _sampleRate, _levels);
        }
        else
        {
            Array.Clear(_levels); // no new audio (paused, stopped): the bars fall
        }
        _motion.Update(_levels, seconds, _peakFall);
        var left = width * 0.08f;
        var right = width * 0.92f;
        var baseline = height * 0.80f;
        var top = height * 0.20f;
        var slot = (right - left) / _levels.Length;
        var bar = slot * 0.72f;
        var radius = Math.Min(bar / 2, height / 270f);
        var cap = Math.Max(2f, height / 360f);
        _idle.StrokeWidth = Math.Max(1f, height / 540f);
        canvas.DrawLine(left, baseline + (cap * 2), right - (slot - bar), baseline + (cap * 2), _idle);
        var shown = _motion.Shown;
        var peaks = _motion.Peaks;
        for (var i = 0; i < shown.Length; i++)
        {
            var x = left + (i * slot);
            var h = shown[i] * (baseline - top);
            if (h > 0.5f)
            {
                canvas.DrawRoundRect(new SKRect(x, baseline - h, x + bar, baseline), radius, radius, _fill);
            }
            if (peaks[i] > 0.01f)
            {
                var y = baseline - (peaks[i] * (baseline - top)) - (cap * 2);
                canvas.DrawRoundRect(new SKRect(x, y, x + bar, y + cap), cap / 2, cap / 2, _fill);
            }
        }
    }

    private void DrawScope(SKCanvas canvas, int width, int height)
    {
        var mid = height / 2f;
        var amplitude = height * 0.36f;
        var left = width * 0.05f;
        var span = width * 0.90f;
        _line.StrokeWidth = Math.Max(1.5f, height / 300f);
        if (!_heard)
        {
            canvas.DrawLine(left, mid, left + span, mid, _idle);
            return;
        }
        const int frames = SpectrumAnalyzer.Size;
        var points = Math.Clamp((int)span, 2, frames);
        if (_points.Length != points)
        {
            _points = new SKPoint[points];
        }
        for (var i = 0; i < points; i++)
        {
            var f = i * (frames - 1) / (points - 1);
            var v = Math.Clamp((_window[f * 2] + _window[(f * 2) + 1]) * 0.5f, -1, 1);
            _points[i] = new SKPoint(left + (span * i / (points - 1)), mid - (v * amplitude));
        }
        canvas.DrawPoints(SKPointMode.Polygon, _points, _line); // one connected line
    }

    public void Dispose()
    {
        _bitmap?.Dispose();
        _fill.Dispose();
        _line.Dispose();
        _idle.Dispose();
    }
}

/// <summary>
/// CN-09: the calibration picture shown while Picture delay… is open: the whole screen flashes white on each kick and
/// fades to black within about 150 ms, so the delay can be set by eye against the kicks heard from the speakers.
/// The flash follows the track's beat grid (AN-02) when it has been analysed (<see cref="SinceBeat"/>); otherwise a
/// kick detector on the bass of the audio.
/// </summary>
public sealed class KickFlashVisualisation : IVisualisation, IOffscreenVisualisation
{
    private const double FlashSeconds = 0.15;
    private readonly float[] _window = new float[SpectrumAnalyzer.Size];
    private int _frames;
    private int _sampleRate = 44_100;
    private double _level;
    private TimeSpan? _lastTime;
    private TimeSpan? _lastKick;

    /// <summary>Seconds since the last beat of the grid at this frame's audio, or null to detect kicks in the audio.</summary>
    public double? SinceBeat { get; set; }

    public void OnAudio(ReadOnlySpan<float> samples, AudioFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        _sampleRate = format.SampleRate;
        _frames = Math.Min(samples.Length / channels, _window.Length);
        var first = (samples.Length / channels) - _frames;
        for (var i = 0; i < _frames; i++)
        {
            var at = (first + i) * channels;
            _window[i] = channels > 1 ? (samples[at] + samples[at + 1]) * 0.5f : samples[at];
        }
    }

    public object CreateView() => throw new NotSupportedException("The kick flash draws off-screen only.");

    /// <summary>
    /// True when the bass (below ~90 Hz) of the newest <paramref name="newSeconds"/> of <paramref name="mono"/> jumps:
    /// a 5 ms block at least 3 × the blocks 10–20 ms before it and among the loudest of the window (public for tests).
    /// </summary>
    public static bool IsKick(ReadOnlySpan<float> mono, int sampleRate, double newSeconds = 1.0 / 30)
    {
        var block = Math.Max(1, sampleRate / 200);
        var blocks = mono.Length / block;
        if (blocks < 8)
        {
            return false;
        }
        // Bass below ~90 Hz, then its energy smoothed to an envelope (~25 Hz), so a block's value does not depend on
        // where in a slow bass wave the block falls; sampled at the end of each 5 ms block.
        var alpha = 1 - Math.Exp(-2 * Math.PI * 90 / sampleRate);
        var beta = 1 - Math.Exp(-2 * Math.PI * 25 / sampleRate);
        Span<double> energy = stackalloc double[blocks];
        double low = 0, envelope = 0;
        for (var b = 0; b < blocks; b++)
        {
            for (var i = b * block; i < (b + 1) * block; i++)
            {
                low += alpha * (mono[i] - low);
                envelope += beta * ((low * low) - envelope);
            }
            energy[b] = envelope;
        }
        double loudest = 0;
        foreach (var e in energy)
        {
            loudest = Math.Max(loudest, e);
        }
        var fresh = Math.Max(1, (int)Math.Ceiling(newSeconds * 200));
        for (var b = Math.Max(4, blocks - fresh); b < blocks; b++)
        {
            var before = (energy[b - 4] + energy[b - 3] + energy[b - 2]) / 3;
            if (energy[b] > 1e-4 && energy[b] > before * 4 && energy[b] > loudest * 0.6)
            {
                return true;
            }
        }
        return false;
    }

    public void RenderFrame(Span<byte> pixels, int width, int height, TimeSpan time)
    {
        var seconds = _lastTime is { } last ? Math.Clamp((time - last).TotalSeconds, 0, 0.25) : 0;
        _lastTime = time;
        if (SinceBeat is { } since)
        {
            _level = since is >= 0 and < FlashSeconds ? 1 - (since / FlashSeconds) : 0;
        }
        else if (_frames > 0 && (_lastKick is not { } kick || time - kick > TimeSpan.FromSeconds(0.25)) && IsKick(_window.AsSpan(0, _frames), _sampleRate))
        {
            _level = 1;
            _lastKick = time;
        }
        else
        {
            _level = Math.Max(0, _level - (seconds / FlashSeconds));
        }
        _frames = 0;
        var grey = (byte)Math.Round(255 * _level);
        System.Runtime.InteropServices.MemoryMarshal.Cast<byte, uint>(pixels).Fill(0xFF000000u | ((uint)grey << 16) | ((uint)grey << 8) | grey);
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// CN-09: drives an off-screen visualisation (built-in or a plugin's) frame by frame: hands it the audio for the
/// frame, then lets it draw into a reusable BGRA buffer of the cast's size.
/// </summary>
public sealed class VisualFrameRenderer : IDisposable
{
    private readonly IVisualisation _visualisation;
    private readonly IOffscreenVisualisation _offscreen;

    public VisualFrameRenderer(IVisualisation visualisation, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(visualisation);
        _offscreen = visualisation as IOffscreenVisualisation
            ?? throw new ArgumentException("The visualisation cannot draw off-screen.", nameof(visualisation));
        if (width < 16 || height < 16 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), "Frames need even sizes of at least 16 pixels (video encoders need even sizes).");
        }
        _visualisation = visualisation;
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    public int Width { get; }

    public int Height { get; }

    /// <summary>The latest frame: premultiplied BGRA, rows top to bottom.</summary>
    public byte[] Pixels { get; }

    /// <summary>Feeds <paramref name="audio"/> (may be empty when nothing new played) and draws the frame for <paramref name="time"/>.</summary>
    public void Render(ReadOnlySpan<float> audio, AudioFormat format, TimeSpan time)
    {
        if (!audio.IsEmpty)
        {
            _visualisation.OnAudio(audio, format);
        }
        _offscreen.RenderFrame(Pixels, Width, Height, time);
    }

    public void Dispose() => _visualisation.Dispose();
}
