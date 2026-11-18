using AudioTastic.Core.Audio;
using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;

namespace AudioTastic.Core.Tests.Audio;

/// <summary>P7: equalizer (EQ-01/EQ-04), built-in effects and the DSP chain (EQ-05), spectrum (UI-07).</summary>
public sealed class DspTests
{
    private static readonly double[] TestFrequencies = [30, 60, 100, 170, 310, 600, 1000, 2200, 3000, 6000, 9000, 12000, 14000, 16000, 19000];

    [Theory]
    [InlineData(44_100)]
    [InlineData(96_000)]
    public void Graphic_eq_response_matches_the_calculated_curve_within_0_1_dB(int rate)
    {
        // P7 exit criterion.
        var settings = new EqualizerSettings { PreampDb = 2, Bands = [6, -4, 3, -12, 9, 0, -6, 12, -3, 5] };
        AssertResponse(settings, rate);
    }

    [Theory]
    [InlineData(FilterType.Peak, 1000, 9, 2)]
    [InlineData(FilterType.LowShelf, 150, 8, 0.7)]
    [InlineData(FilterType.HighShelf, 6000, -10, 0.7)]
    [InlineData(FilterType.LowPass, 5000, 0, 0.707)]
    [InlineData(FilterType.HighPass, 200, 0, 0.707)]
    [InlineData(FilterType.Notch, 3000, 0, 4)]
    public void Parametric_band_response_matches_the_calculated_curve_within_0_1_dB(FilterType type, double frequency, double gain, double q)
    {
        var settings = new EqualizerSettings
        {
            Mode = EqualizerMode.Parametric,
            Parametric = [new ParametricBand(type, frequency, gain, q), new ParametricBand(FilterType.Peak, 80, -3, 1)],
        };
        AssertResponse(settings, 48_000, skipNear: type == FilterType.Notch ? frequency : null);
    }

    [Fact]
    public void Automatic_headroom_keeps_the_curve_at_or_below_0_dB()
    {
        var design = EqualizerDesign.Create(new EqualizerSettings { PreampDb = 6, Bands = [12, 12, 12, 12, 12, 12, 12, 12, 12, 12] }, 44_100);
        Assert.True(design.HeadroomDb > 12);
        Assert.All(EqualizerDesign.CurveFrequencies(44_100), f => Assert.True(design.ResponseDb(f) <= 1e-6));

        var cut = EqualizerDesign.Create(new EqualizerSettings { PreampDb = -3, Bands = new double[10] }, 44_100);
        Assert.Equal(0, cut.HeadroomDb);
        Assert.Equal(-3, cut.GainDb, 9);
    }

    [Fact]
    public void Flat_equalizer_is_bit_exact()
    {
        var stage = new EqualizerStage();
        stage.Configure(44_100, 2);
        stage.Apply(new EqualizerSettings());
        var signal = Signals(1000, 44_100, 4096, 0.5f);
        var copy = (float[])signal.Clone();
        stage.Process(copy, 2);
        Assert.Equal(signal, copy);
    }

    [Fact]
    public void Limiter_never_exceeds_the_ceiling_and_passes_quiet_audio_unchanged()
    {
        var stage = new LimiterStage();
        stage.Apply(new LimiterSettings { CeilingDb = -1, ReleaseMs = 50 });
        stage.Configure(48_000, 2);
        var ceiling = (float)Math.Pow(10, -1 / 20.0);
        var random = new Random(7);
        var loud = new float[48_000 * 2];
        for (var i = 0; i < loud.Length; i++)
        {
            loud[i] = (float)((random.NextDouble() * 4) - 2) * (float)Math.Sin(i / 900.0); // up to +6 dBFS
        }
        stage.Process(loud, 2);
        Assert.All(loud, v => Assert.True(Math.Abs(v) <= ceiling + 1e-6f, $"{v} > {ceiling}"));

        stage.Reset();
        var quiet = Signals(440, 48_000, 48_000, 0.3f);
        var output = (float[])quiet.Clone();
        stage.Process(output, 2);
        var latency = stage.LatencyFrames * 2;
        for (var i = latency; i < output.Length; i++)
        {
            Assert.Equal(quiet[i - latency], output[i]);
        }
    }

    [Fact]
    public void Compressor_reduces_a_loud_signal_by_the_ratio()
    {
        var stage = new CompressorStage();
        stage.Apply(new CompressorSettings { ThresholdDb = -20, Ratio = 4, AttackMs = 1, ReleaseMs = 50, MakeupDb = 0 });
        stage.Configure(44_100, 2);
        var amplitude = (float)Math.Pow(10, -10 / 20.0); // peaks at -10 dBFS: 10 dB over, 7.5 dB reduction
        var signal = Signals(1000, 44_100, 44_100, amplitude);
        stage.Process(signal, 2);
        var peak = signal.Skip(signal.Length / 2).Max(Math.Abs);
        Assert.InRange(20 * Math.Log10(peak), -18.5, -16.5);
    }

    [Fact]
    public void Widener_at_zero_is_mono_and_at_one_is_unchanged()
    {
        var stage = new WidenerStage();
        float[] stereo = [0.5f, -0.25f, 0.1f, 0.3f];
        stage.Apply(new WidenerSettings { Width = 1 });
        var same = (float[])stereo.Clone();
        stage.Process(same, 2);
        Assert.Equal(stereo, same);
        stage.Apply(new WidenerSettings { Width = 0 });
        stage.Process(stereo, 2);
        Assert.Equal(stereo[0], stereo[1]);
        Assert.Equal(stereo[2], stereo[3]);
    }

    [Fact]
    public void Reverb_adds_a_tail_and_mix_zero_leaves_the_sound_dry()
    {
        var stage = new ReverbStage();
        stage.Apply(new ReverbSettings { Mix = 0.3 });
        stage.Configure(44_100, 2);
        var impulse = new float[44_100 * 2];
        impulse[0] = impulse[1] = 1;
        stage.Process(impulse, 2);
        Assert.True(impulse.Skip(4410 * 2).Take(4410 * 2).Max(Math.Abs) > 1e-3, "no reverb tail");

        stage.Apply(new ReverbSettings { Mix = 0 });
        stage.Reset();
        var dry = Signals(500, 44_100, 2048, 0.4f);
        var copy = (float[])dry.Clone();
        stage.Process(copy, 2);
        Assert.Equal(dry, copy);
    }

    [Fact]
    public async Task Render_with_every_effect_on_does_not_allocate()
    {
        using var graph = new MixerGraph(new AudioFormat(48_000, 2));
        var dir = Directory.CreateTempSubdirectory("audiotastic-dsp-").FullName;
        var settings = new SettingsStore(AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated());
        settings.Update(SettingsGroup.EqualizerDsp, s =>
        {
            s.EqualizerDsp.Chain = [.. DspChainEntry.Defaults().Select(e => e with { Enabled = true })];
            s.EqualizerDsp.Equalizer.Bands = [3, 2, 1, 0, -1, -2, 1, 2, 3, 4];
        });
        using var dsp = new DspService(graph, settings);
        await graph.DeckA.LoadAsync(new SyntheticSource(48_000, 2, 48_000 * 10));
        var block = new float[256 * 2];
        for (var i = 0; i < 500; i++)
        {
            graph.Render(block); // warm up: past the JIT's tier-up, which can allocate when it is late under load
        }

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 200; i++)
        {
            graph.Render(block);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(5, graph.Dsp.Stages.Count(s => s.Enabled));
    }

    [Fact]
    public void Bypass_leaves_the_samples_untouched()
    {
        var chain = new DspChain();
        var eq = new EqualizerStage { Enabled = true };
        eq.Apply(new EqualizerSettings { Bands = [12, 12, 12, 12, 12, 12, 12, 12, 12, 12] });
        chain.SetStages([eq]);
        chain.Configure(44_100, 2);
        chain.Bypass = true;
        var signal = Signals(1000, 44_100, 1024, 0.5f);
        var copy = (float[])signal.Clone();
        chain.Process(copy, 2);
        Assert.Equal(signal, copy);
    }

    [Fact]
    public async Task Plugin_effect_that_throws_is_bypassed_and_reported()
    {
        var faults = new List<Exception>();
        var stage = new PluginEffectStage(new PluginEffect("p", "Bad", _ => new ThrowingEffect(), ex =>
        {
            lock (faults)
            {
                faults.Add(ex);
            }
        })) { Enabled = true };
        stage.Configure(44_100, 2);
        var block = new float[512];
        Array.Fill(block, 0.5f);
        stage.Process(block, 2);
        stage.Process(block, 2); // bypassed now
        await Wait.UntilAsync(() =>
        {
            lock (faults)
            {
                return faults.Count == 1;
            }
        }, 5_000);
        Assert.All(block, v => Assert.Equal(0.5f, v));
    }

    [Fact]
    public async Task Plugin_effect_over_its_time_budget_is_reported()
    {
        var faults = new List<Exception>();
        var stage = new PluginEffectStage(new PluginEffect("p", "Slow", _ => new SlowEffect(), ex =>
        {
            lock (faults)
            {
                faults.Add(ex);
            }
        }));
        stage.Configure(8_000, 2);
        var block = new float[800 * 2]; // 100 ms of audio; the effect takes 20 ms per call = 20 % of real time
        for (var i = 0; i < 10; i++)
        {
            stage.Process(block, 2);
        }
        await Wait.UntilAsync(() =>
        {
            lock (faults)
            {
                return faults.Count > 0;
            }
        }, 5_000);
        lock (faults)
        {
            Assert.IsType<TimeoutException>(faults[0]);
        }
    }

    [Fact]
    public void Dsp_service_follows_the_settings_order_and_appends_plugin_effects_switched_off()
    {
        using var graph = new MixerGraph(new AudioFormat(44_100, 2));
        var dir = Directory.CreateTempSubdirectory("audiotastic-dsp-").FullName;
        var settings = new SettingsStore(AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated());
        settings.Update(SettingsGroup.EqualizerDsp, s => s.EqualizerDsp.Chain = [new(DspChainEntry.Limiter, true), new(DspChainEntry.Equalizer, true)]);
        using var dsp = new DspService(graph, settings);
        var disposed = new List<string>();
        dsp.SetPluginEffects(() => [new PluginEffect("gain.plugin", "Gain", _ => new CountingEffect(disposed), _ => { })]);

        Assert.Equal(["limiter", "equalizer", "compressor", "widener", "reverb", "plugin:gain.plugin:Gain"], dsp.Items.Select(i => i.Id));
        Assert.Equal([true, true, false, false, false, false], dsp.Items.Select(i => i.Enabled));
        Assert.True(dsp.Items[^1].IsPlugin);

        dsp.RemovePlugin("gain.plugin");
        Assert.DoesNotContain(dsp.Items, i => i.IsPlugin);
        Assert.Equal(["disposed"], disposed);
    }

    [Fact]
    public void Spectrum_shows_a_1_kHz_sine_in_the_right_bar()
    {
        var analyzer = new SpectrumAnalyzer();
        var signal = Signals(1000, 44_100, SpectrumAnalyzer.Size, 1f);
        var bars = new float[20];
        analyzer.Analyze(signal, 44_100, bars);
        var loudest = Array.IndexOf(bars, bars.Max());
        // Bar b covers 40 Hz × (16000/40)^(b/20) .. ^((b+1)/20): 1 kHz is in bar 10.
        Assert.Equal(10, loudest);
        Assert.InRange(bars[loudest], 0.95f, 1.01f);
        Assert.True(bars[2] < 0.3f);
    }

    [Fact]
    public void Fft_matches_a_direct_dft()
    {
        var n = 64;
        var re = new double[n];
        var im = new double[n];
        var random = new Random(3);
        for (var i = 0; i < n; i++)
        {
            re[i] = random.NextDouble() - 0.5;
        }
        var input = (double[])re.Clone();
        Fft.Forward(re, im);
        for (var k = 0; k < n; k++)
        {
            double sr = 0, si = 0;
            for (var t = 0; t < n; t++)
            {
                sr += input[t] * Math.Cos(-2 * Math.PI * k * t / n);
                si += input[t] * Math.Sin(-2 * Math.PI * k * t / n);
            }
            Assert.Equal(sr, re[k], 9);
            Assert.Equal(si, im[k], 9);
        }
    }

    [Fact]
    public void Visualisation_feed_returns_the_newest_frames()
    {
        var feed = new VisualisationFeed();
        var block = new float[1000 * 2];
        for (var f = 0; f < 1000; f++)
        {
            block[f * 2] = f;
            block[(f * 2) + 1] = -f;
        }
        for (var i = 0; i < 10; i++)
        {
            feed.Write(block, 2);
        }
        var latest = new float[100 * 2];
        Assert.Equal(100, feed.ReadLatest(latest));
        Assert.Equal(999, latest[^2]);
        Assert.Equal(-999, latest[^1]);
        Assert.Equal(900, latest[0]);
    }

    // ---- helpers ----

    private static void AssertResponse(EqualizerSettings settings, int rate, double? skipNear = null)
    {
        var design = EqualizerDesign.Create(settings, rate);
        foreach (var f in TestFrequencies.Where(f => f < 0.45 * rate))
        {
            if (skipNear is { } notch && Math.Abs(Math.Log2(f / notch)) < 0.2)
            {
                continue; // the notch itself goes to -inf: compare elsewhere
            }
            var stage = new EqualizerStage();
            stage.Configure(rate, 2);
            stage.Apply(settings);
            var frames = rate; // 1 s; measure the last half (filters settled)
            var signal = Signals(f, rate, frames, 0.1f);
            var input = Rms(signal, frames / 2);
            stage.Process(signal, 2);
            var measured = 20 * Math.Log10(Rms(signal, frames / 2) / input);
            var expected = design.ResponseDb(f);
            Assert.True(Math.Abs(measured - expected) <= 0.1, $"{f} Hz at {rate} Hz: measured {measured:F3} dB, calculated {expected:F3} dB");
        }
    }

    internal static float[] Signals(double frequency, int rate, int frames, float amplitude)
    {
        var data = new float[frames * 2];
        for (var i = 0; i < frames; i++)
        {
            var v = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * i / rate));
            data[i * 2] = v;
            data[(i * 2) + 1] = v;
        }
        return data;
    }

    private static double Rms(float[] stereo, int fromFrame)
    {
        double sum = 0;
        var count = 0;
        for (var i = fromFrame * 2; i < stereo.Length; i += 2)
        {
            sum += stereo[i] * (double)stereo[i];
            count++;
        }
        return Math.Sqrt(sum / count);
    }

    private sealed class ThrowingEffect : IDspEffect
    {
        public void Process(Span<float> samples, int channels) => throw new InvalidOperationException("effect failed on purpose");

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class SlowEffect : IDspEffect
    {
        public void Process(Span<float> samples, int channels) => Thread.Sleep(20);

        public void Reset()
        {
        }

        public void Dispose()
        {
        }
    }

    private sealed class CountingEffect(List<string> disposed) : IDspEffect
    {
        public void Process(Span<float> samples, int channels)
        {
        }

        public void Reset()
        {
        }

        public void Dispose() => disposed.Add("disposed");
    }
}
