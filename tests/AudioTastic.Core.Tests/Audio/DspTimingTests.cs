using System.Diagnostics;
using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Data.Settings;

namespace AudioTastic.Core.Tests.Audio;

[CollectionDefinition(nameof(DspTimingTests), DisableParallelization = true)]
public sealed class DspTimingGroup;

/// <summary>P7 exit criterion: the DSP chain with every built-in effect uses under 5 % of one core at 96 kHz.</summary>
[Collection(nameof(DspTimingTests))]
public sealed class DspTimingTests
{
    [Fact]
    public void All_built_in_effects_use_under_5_percent_of_one_core_at_96_kHz()
    {
        const int rate = 96_000;
        const int seconds = 5;
        var chain = new DspChain();
        var eq = new EqualizerStage { Enabled = true };
        eq.Apply(new EqualizerSettings { PreampDb = -2, Bands = [4, 3, 2, 1, 0, -1, -2, 3, 4, 5] });
        var compressor = new CompressorStage { Enabled = true };
        compressor.Apply(new CompressorSettings());
        var widener = new WidenerStage { Enabled = true };
        widener.Apply(new WidenerSettings());
        var reverb = new ReverbStage { Enabled = true };
        reverb.Apply(new ReverbSettings());
        var limiter = new LimiterStage { Enabled = true };
        limiter.Apply(new LimiterSettings());
        chain.SetStages([eq, compressor, widener, reverb, limiter]);
        chain.Configure(rate, 2);
        var source = DspTests.Signals(440, rate, rate, 0.8f);
        var block = new float[512 * 2]; // a typical device period

        // Best of five (other test projects run at the same time).
        var best = double.MaxValue;
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var timer = Stopwatch.StartNew();
            for (var s = 0; s < seconds; s++)
            {
                for (var offset = 0; offset + block.Length <= source.Length; offset += block.Length)
                {
                    source.AsSpan(offset, block.Length).CopyTo(block);
                    chain.Process(block, 2);
                }
            }
            best = Math.Min(best, timer.Elapsed.TotalSeconds / seconds);
        }
        var perStage = string.Join(", ", chain.Stages.Select(s => $"{s.Name} {Measure(s, source, block):P1}"));
        // The criterion is for the shipped (optimised) build. Debug builds of Core run unoptimised JIT code, roughly
        // 3-4x slower, so they only guard against gross regressions.
        var optimised = IsOptimised(typeof(DspChain).Assembly);
        var limit = optimised ? 0.05 : 0.20;
        var message = $"The chain used {best:P1} of one core at 96 kHz ({perStage}; {(optimised ? "optimised" : "debug")} build, limit {limit:P0}).";
        TestContext.Current.TestOutputHelper?.WriteLine(message);
        Assert.True(best < limit, message);
    }

    private static bool IsOptimised(System.Reflection.Assembly assembly) =>
        assembly.GetCustomAttributes(typeof(DebuggableAttribute), false) is not [DebuggableAttribute { IsJITOptimizerDisabled: true }];

    private static double Measure(DspStage stage, float[] source, float[] block)
    {
        var timer = Stopwatch.StartNew();
        for (var offset = 0; offset + block.Length <= source.Length; offset += block.Length)
        {
            source.AsSpan(offset, block.Length).CopyTo(block);
            stage.Process(block, 2);
        }
        return timer.Elapsed.TotalSeconds; // one second of audio
    }
}
