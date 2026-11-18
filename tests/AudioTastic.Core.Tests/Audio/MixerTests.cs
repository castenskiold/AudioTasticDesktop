using AudioTastic.Core.Audio;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.Core.Tests.Audio;

public sealed class SpscRingBufferTests
{
    [Fact]
    public void Preserves_order_across_wrap_around()
    {
        var ring = new SpscRingBuffer(8);
        var output = new List<float>();
        var buffer = new float[5];
        var next = 0f;
        for (var round = 0; round < 20; round++)
        {
            var data = Enumerable.Range(0, 5).Select(_ => next++).ToArray();
            Assert.Equal(5, ring.Write(data));
            Assert.Equal(5, ring.Read(buffer));
            output.AddRange(buffer);
        }
        Assert.Equal(Enumerable.Range(0, 100).Select(i => (float)i), output);
    }

    [Fact]
    public void Write_stops_when_full_and_read_when_empty()
    {
        var ring = new SpscRingBuffer(4);
        Assert.Equal(4, ring.Write(new float[10]));
        Assert.Equal(0, ring.AvailableToWrite);
        Assert.Equal(4, ring.Read(new float[10]));
        Assert.Equal(0, ring.Read(new float[10]));
    }

    [Fact]
    public async Task Concurrent_producer_and_consumer_transfer_every_sample_in_order()
    {
        const int total = 1_000_000;
        var ring = new SpscRingBuffer(1024);
        var producer = Task.Run(() =>
        {
            var chunk = new float[300];
            var value = 0;
            while (value < total)
            {
                var n = Math.Min(chunk.Length, total - value);
                for (var i = 0; i < n; i++)
                {
                    chunk[i] = value + i;
                }
                var written = 0;
                while (written < n)
                {
                    written += ring.Write(chunk.AsSpan(written, n - written));
                }
                value += n;
            }
        }, TestContext.Current.CancellationToken);
        var consumer = Task.Run(() =>
        {
            var chunk = new float[257];
            var expected = 0;
            while (expected < total)
            {
                var n = ring.Read(chunk);
                for (var i = 0; i < n; i++)
                {
                    if (chunk[i] != expected++)
                    {
                        return false;
                    }
                }
            }
            return true;
        }, TestContext.Current.CancellationToken);
        await producer;
        Assert.True(await consumer);
    }
}

public sealed class GainStageTests
{
    [Theory]
    [InlineData(0, 0f)]
    [InlineData(100, 1f)]
    [InlineData(50, 0.125f)]
    [InlineData(150, 1f)]
    [InlineData(-5, 0f)]
    public void Volume_curve_is_cubic(int volume, float gain) =>
        Assert.Equal(gain, GainStage.VolumeToGain(volume), 6);

    [Fact]
    public void Changes_ramp_over_ten_milliseconds_without_jumps()
    {
        var stage = new GainStage(1f, 48_000);
        stage.TargetGain = 0f;
        var samples = Enumerable.Repeat(1f, 1000 * 2).ToArray();

        stage.Process(samples, 2);

        var left = samples.Where((_, i) => i % 2 == 0).ToArray();
        Assert.True(left.Zip(left.Skip(1)).All(p => p.Second <= p.First), "ramp must be monotonic");
        Assert.True(left.Zip(left.Skip(1)).All(p => p.First - p.Second < 0.003f), "no steps larger than one ramp step");
        Assert.Equal(0f, left[480]);
        Assert.Equal(0f, stage.CurrentGain);
    }

    [Fact]
    public void Unity_gain_leaves_samples_untouched()
    {
        var stage = new GainStage();
        float[] samples = [0.1f, -0.5f, 0.99f];
        stage.Process(samples, 1);
        Assert.Equal([0.1f, -0.5f, 0.99f], samples);
    }
}

public sealed class ChannelMapperTests
{
    [Fact]
    public void Mono_is_copied_to_both_channels()
    {
        float[] mono = [0.1f, 0.2f];
        var stereo = new float[4];
        ChannelMapper.Map(mono, 1, stereo, 2, 2);
        Assert.Equal([0.1f, 0.1f, 0.2f, 0.2f], stereo);
    }

    [Fact]
    public void Surround_is_downmixed_without_clipping()
    {
        // FL FR FC LFE BL BR, all at full scale.
        float[] frame = [1f, 1f, 1f, 1f, 1f, 1f];
        var stereo = new float[2];
        ChannelMapper.Map(frame, 6, stereo, 2, 1);
        Assert.Equal(1f, stereo[0], 5);
        Assert.Equal(1f, stereo[1], 5);
    }
}

public sealed class MixerGraphTests
{
    [Fact]
    public async Task Renders_the_deck_bit_exactly_at_unity_gain()
    {
        using var graph = new MixerGraph(new AudioFormat(44_100, 2));
        var source = new SyntheticSource(44_100, 2, 44_100);
        await graph.DeckA.LoadAsync(source);

        var rendered = new List<float>();
        var block = new float[333 * 2]; // odd size to cross ring boundaries
        while (!graph.DeckA.HasEnded)
        {
            await Wait.UntilAsync(() => graph.DeckA.BufferedSamples >= block.Length || graph.DeckA.SourceEnded);
            graph.Render(block);
            rendered.AddRange(block);
        }

        for (var f = 0; f < 44_100; f++)
        {
            Assert.Equal(SyntheticSource.ValueAt(f, 0), rendered[2 * f]);
            Assert.Equal(SyntheticSource.ValueAt(f, 1), rendered[(2 * f) + 1]);
        }
        Assert.Equal(0, graph.DeckA.Underruns);
        Assert.Equal(44_100, graph.DeckA.PositionFrames);
    }

    [Fact]
    public async Task Render_does_not_allocate()
    {
        using var graph = new MixerGraph(new AudioFormat(48_000, 2));
        await graph.DeckA.LoadAsync(new SyntheticSource(48_000, 2, 48_000 * 10));
        graph.Master.TargetGain = 0.5f; // exercise the ramp path too
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
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }

    [Fact]
    public async Task Paused_graph_outputs_silence_and_holds_position()
    {
        using var graph = new MixerGraph(new AudioFormat(44_100, 2));
        await graph.DeckA.LoadAsync(new SyntheticSource(44_100, 2, 44_100));
        graph.Paused = true;
        var block = new float[256 * 2];
        Array.Fill(block, 1f);

        graph.Render(block);

        Assert.All(block, s => Assert.Equal(0f, s));
        Assert.Equal(0, graph.DeckA.PositionFrames);
    }

    [Fact]
    public async Task Master_gain_scales_the_output()
    {
        using var graph = new MixerGraph(new AudioFormat(44_100, 2));
        await graph.DeckA.LoadAsync(new SyntheticSource(44_100, 2, 44_100));
        graph.Master.TargetGain = 0.5f;
        graph.Master.SnapToTarget();
        var block = new float[64 * 2];

        graph.Render(block);

        Assert.Equal(SyntheticSource.ValueAt(10, 0) * 0.5f, block[20], 6);
    }

    [Fact]
    public async Task Seek_restarts_the_deck_at_the_exact_frame()
    {
        using var graph = new MixerGraph(new AudioFormat(44_100, 2));
        await graph.DeckA.LoadAsync(new SyntheticSource(44_100, 2, 44_100));

        await graph.DeckA.SeekAsync(12_345);
        var block = new float[4 * 2];
        graph.Render(block);

        Assert.Equal(SyntheticSource.ValueAt(12_345, 0), block[0]);
        Assert.Equal(12_349, graph.DeckA.PositionFrames);
    }

    [Fact]
    public async Task Decoder_errors_end_the_deck_and_are_reported()
    {
        using var graph = new MixerGraph(new AudioFormat(44_100, 2));
        await graph.DeckA.LoadAsync(new SyntheticSource(44_100, 2, 44_100) { FailAfterFrames = 10_000 });
        var block = new float[1024];

        await Wait.UntilAsync(() =>
        {
            graph.Render(block);
            return graph.DeckA.HasEnded;
        });

        Assert.IsType<AudioSourceException>(graph.DeckA.Fault);
    }
}
