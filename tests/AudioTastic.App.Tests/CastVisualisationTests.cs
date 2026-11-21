using AudioTastic.App.Integration;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Audio;
using SkiaSharp;

namespace AudioTastic.App.Tests;

/// <summary>CN-09 step 1: the built-in visualisation drawn off-screen for casting.</summary>
public sealed class CastVisualisationTests
{
    private static readonly AudioFormat Stereo = new(44_100, 2);

    /// <summary>Two tones (110 Hz loud, 3 kHz softer), interleaved stereo, starting at <paramref name="frame"/>.</summary>
    private static float[] Tones(long frame, int frames)
    {
        var samples = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var t = (frame + i) / 44_100.0;
            var v = (float)((0.6 * Math.Sin(2 * Math.PI * 110 * t)) + (0.2 * Math.Sin(2 * Math.PI * 3000 * t)));
            samples[i * 2] = v;
            samples[(i * 2) + 1] = v;
        }
        return samples;
    }

    /// <summary>Renders a second of 30 fps frames and returns the last one.</summary>
    private static VisualFrameRenderer Run(VisualisationType type, int width, int height, bool silentAtEnd = false)
    {
        var renderer = new VisualFrameRenderer(new CastVisualisation(type), width, height);
        for (var n = 0; n < 30; n++)
        {
            var audio = silentAtEnd && n > 10 ? [] : Tones(n * 1470L, 1470);
            renderer.Render(audio, Stereo, TimeSpan.FromSeconds(n / 30.0));
        }
        Save(renderer, $"cast-{type}-{width}x{height}{(silentAtEnd ? "-silent" : "")}.png");
        return renderer;
    }

    /// <summary>Writes the frame as PNG when AUDIOTASTIC_FRAME_DIR is set (for looking at it).</summary>
    private static void Save(VisualFrameRenderer renderer, string name)
    {
        if (Environment.GetEnvironmentVariable("AUDIOTASTIC_FRAME_DIR") is not { Length: > 0 } dir)
        {
            return;
        }
        using var image = SKImage.FromPixelCopy(new SKImageInfo(renderer.Width, renderer.Height, SKColorType.Bgra8888, SKAlphaType.Premul), renderer.Pixels);
        using var png = image.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(Path.Combine(dir, name), png.ToArray());
    }

    private static (int R, int G, int B, int A) Pixel(VisualFrameRenderer r, int x, int y)
    {
        var i = ((y * r.Width) + x) * 4;
        return (r.Pixels[i + 2], r.Pixels[i + 1], r.Pixels[i], r.Pixels[i + 3]);
    }

    private static int BrightPixels(VisualFrameRenderer r, int top, int bottom)
    {
        var count = 0;
        for (var y = top; y < bottom; y++)
        {
            for (var x = 0; x < r.Width; x++)
            {
                if (r.Pixels[(((y * r.Width) + x) * 4) + 1] > 200)
                {
                    count++;
                }
            }
        }
        return count;
    }

    [Fact]
    public void Spectrum_frame_is_white_bars_on_black_with_the_bass_tallest()
    {
        using var r = Run(VisualisationType.Spectrum, 1920, 1080);
        Assert.Equal((0, 0, 0, 255), Pixel(r, 5, 5)); // opaque black background
        Assert.Equal(0, BrightPixels(r, 0, 100)); // nothing at the very top
        Assert.True(BrightPixels(r, 700, 860) > 10_000, "bars near the baseline");

        // The 110 Hz bar (left part) reaches higher than the 3 kHz one (further right) and the empty top band.
        static int Highest(VisualFrameRenderer r, int x0, int x1)
        {
            for (var y = 0; y < r.Height; y++)
            {
                for (var x = x0; x < x1; x++)
                {
                    if (r.Pixels[(((y * r.Width) + x) * 4) + 1] > 200)
                    {
                        return y;
                    }
                }
            }
            return r.Height;
        }
        var bass = Highest(r, (int)(1920 * 0.08), 1920 / 3);
        var treble = Highest(r, 1920 * 2 / 3, (int)(1920 * 0.92));
        Assert.True(bass < treble, $"bass top {bass} should be above treble top {treble}");
    }

    [Fact]
    public void Spectrum_bars_fall_when_the_audio_stops()
    {
        using var playing = Run(VisualisationType.Spectrum, 640, 360);
        using var stopped = Run(VisualisationType.Spectrum, 640, 360, silentAtEnd: true);
        Assert.True(BrightPixels(stopped, 0, 280) < BrightPixels(playing, 0, 280) / 4, "bars have fallen (only caps and the baseline remain)");
    }

    [Fact]
    public void Oscilloscope_frame_draws_one_line_across_the_middle()
    {
        using var r = Run(VisualisationType.Oscilloscope, 1280, 720);
        Assert.True(BrightPixels(r, 0, 720) > 1280, "a line across the width");
        Assert.Equal(0, BrightPixels(r, 0, 80)); // within the amplitude band
        Assert.Equal(0, BrightPixels(r, 640, 720));
    }

    [Fact]
    public void Frames_need_even_sizes_for_the_video_encoder()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new VisualFrameRenderer(new CastVisualisation(VisualisationType.Spectrum), 1919, 1080));
        Assert.Throws<ArgumentException>(() => new VisualFrameRenderer(new NotOffscreen(), 640, 360));
    }

    private sealed class NotOffscreen : AudioTastic.Sdk.Plugins.IVisualisation
    {
        public void OnAudio(ReadOnlySpan<float> samples, AudioFormat format)
        {
        }

        public object CreateView() => new object();

        public void Dispose()
        {
        }
    }
}
