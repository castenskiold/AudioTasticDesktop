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

    /// <summary>A 55 Hz kick (fast attack, 150 ms decay) every <paramref name="period"/> seconds over quiet hi-hat noise.</summary>
    private static float[] Kicks(double seconds, double period)
    {
        var random = new Random(1);
        var samples = new float[(int)(seconds * 44_100) * 2];
        for (var i = 0; i < samples.Length / 2; i++)
        {
            var t = i / 44_100.0;
            var since = t % period;
            var kick = 0.8 * Math.Sin(2 * Math.PI * 55 * since) * Math.Exp(-since / 0.15) * Math.Min(1, since / 0.002);
            var v = (float)(kick + (0.05 * (random.NextDouble() - 0.5)));
            samples[i * 2] = v;
            samples[(i * 2) + 1] = v;
        }
        return samples;
    }

    [Fact]
    public void Kick_flash_flashes_once_per_kick_and_fades_to_black()
    {
        var audio = Kicks(4, 60.0 / 150); // 150 BPM: 10 kicks in 4 s
        using var r = new VisualFrameRenderer(new KickFlashVisualisation(), 64, 36);
        var flashes = 0;
        var wasBright = false;
        var sawBlack = false;
        for (var n = 0; n < 4 * 30; n++)
        {
            var end = (int)Math.Round((n + 1) / 30.0 * 44_100);
            var start = Math.Max(0, end - 2048);
            r.Render(audio.AsSpan(start * 2, (end - start) * 2), Stereo, TimeSpan.FromSeconds(n / 30.0));
            var bright = r.Pixels[0] > 200;
            if (bright && !wasBright)
            {
                flashes++;
            }
            sawBlack |= r.Pixels[0] == 0;
            wasBright = bright;
            Assert.Equal(255, r.Pixels[3]); // opaque
        }
        Assert.InRange(flashes, 9, 11);
        Assert.True(sawBlack, "black between the kicks");
    }

    [Fact]
    public void Steady_bass_and_silence_are_not_kicks()
    {
        var steady = new float[2048];
        for (var i = 0; i < steady.Length; i++)
        {
            steady[i] = (float)(0.8 * Math.Sin(2 * Math.PI * 55 * i / 44_100));
        }
        Assert.False(KickFlashVisualisation.IsKick(steady, 44_100));
        Assert.False(KickFlashVisualisation.IsKick(new float[2048], 44_100));
    }

    /// <summary>Counts flashes on a real track when AUDIOTASTIC_KICK_TRACK names a 16-bit WAV (local check only).</summary>
    [Fact]
    public void Kick_flash_on_a_real_track_reports_its_rate()
    {
        if (Environment.GetEnvironmentVariable("AUDIOTASTIC_KICK_TRACK") is not { Length: > 0 } path)
        {
            return;
        }
        var (rate, channels, samples) = AudioTastic.TestSupport.ReferenceWav.Read(path);
        using var r = new VisualFrameRenderer(new KickFlashVisualisation(), 64, 36);
        var flashes = 0;
        var wasBright = false;
        var from = 120 * rate; // two minutes in: the groove is running
        var frames = 60 * 30;
        for (var n = 0; n < frames; n++)
        {
            var end = from + (int)Math.Round((n + 1) / 30.0 * rate);
            r.Render(samples.AsSpan((end - 2048) * channels, 2048 * channels), new AudioFormat(rate, channels), TimeSpan.FromSeconds(n / 30.0));
            var bright = r.Pixels[0] > 200;
            flashes += bright && !wasBright ? 1 : 0;
            wasBright = bright;
        }
        TestContext.Current.SendDiagnosticMessage($"{Path.GetFileName(path)}: {flashes} flashes in 60 s = {flashes} per minute");
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "audiotastic-kicks.txt"), $"{Path.GetFileName(path)}: {flashes} flashes in 60 s{Environment.NewLine}");
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
