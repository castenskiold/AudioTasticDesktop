using System.Buffers.Binary;
using AudioTastic.Core.Audio;
using AudioTastic.Core.Audio.Output;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.Core.Tests.Audio;

/// <summary>PB-05 sample conversion (bit-perfect integer output, dither) and PB-01 resampling.</summary>
public sealed class ConversionTests
{
    [Fact]
    public void Int16_round_trip_is_exact()
    {
        var ints = Enumerable.Range(0, 65536).Select(i => (short)(i - 32768)).ToArray();
        var floats = ints.Select(v => v / 32768f).ToArray();
        var bytes = new byte[ints.Length * 2];
        var state = new DitherState(1);

        SampleConverter.Convert(floats, bytes, OutputSampleFormat.S16, dither: false, ref state);

        for (var i = 0; i < ints.Length; i++)
        {
            Assert.Equal(ints[i], BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2)));
        }
    }

    [Fact]
    public void Int24_and_int32_round_trips_are_exact_for_24_bit_sources()
    {
        var random = new Random(7);
        var ints = Enumerable.Range(0, 100_000).Select(_ => random.Next(-8388608, 8388608)).Concat([-8388608, 8388607, 0]).ToArray();
        var floats = ints.Select(v => v / 8388608f).ToArray();
        var b24 = new byte[ints.Length * 3];
        var b32 = new byte[ints.Length * 4];
        var state = new DitherState(1);

        SampleConverter.Convert(floats, b24, OutputSampleFormat.S24, dither: false, ref state);
        SampleConverter.Convert(floats, b32, OutputSampleFormat.S32, dither: true, ref state); // 32-bit never dithers

        for (var i = 0; i < ints.Length; i++)
        {
            var v24 = (b24[i * 3] | (b24[(i * 3) + 1] << 8) | (b24[(i * 3) + 2] << 16)) << 8 >> 8;
            Assert.Equal(ints[i], v24);
            Assert.Equal(ints[i] * 256L, BinaryPrimitives.ReadInt32LittleEndian(b32.AsSpan(i * 4)));
        }
    }

    [Fact]
    public void Dither_stays_within_one_lsb_and_averages_out_and_values_clip()
    {
        var floats = Enumerable.Repeat(1000.25f / 32768f, 20_000).Concat([1.5f, -1.5f]).ToArray();
        var bytes = new byte[floats.Length * 2];
        var state = new DitherState(42);

        SampleConverter.Convert(floats, bytes, OutputSampleFormat.S16, dither: true, ref state);

        var values = Enumerable.Range(0, 20_000).Select(i => (int)BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i * 2))).ToArray();
        Assert.All(values, v => Assert.InRange(v, 999, 1002));
        Assert.InRange(values.Average(), 1000.15, 1000.35); // TPDF keeps the mean: 1000.25
        Assert.Equal(32767, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(20_000 * 2)));
        Assert.Equal(-32768, BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(20_001 * 2)));
    }

    [Theory]
    [InlineData(44_100, 48_000)]
    [InlineData(48_000, 44_100)]
    [InlineData(96_000, 44_100)]
    [InlineData(44_100, 88_200)]
    public void Resampled_sine_matches_the_ideal_sine(int from, int to)
    {
        const double hz = 1000, seconds = 2;
        var source = new SineSource(from, hz, seconds);
        using var resampler = new ResamplingSource(source, to);

        Assert.Equal(new AudioFormat(to, 2), resampler.Format);
        Assert.Equal((long)Math.Floor(from * seconds / ((double)from / to)), resampler.LengthFrames);
        var output = new float[(int)resampler.LengthFrames!.Value * 2];
        var total = 0;
        int n;
        while ((n = resampler.Read(output.AsSpan(total * 2))) > 0)
        {
            total += n;
        }
        Assert.Equal(resampler.LengthFrames, total);

        // Away from the edges the output equals the ideal sine at the new rate (no delay): error below -80 dB.
        var maxError = 0.0;
        for (var f = to / 10; f < total - (to / 10); f++)
        {
            var ideal = 0.5 * Math.Sin(2 * Math.PI * hz * f / to);
            maxError = Math.Max(maxError, Math.Abs(output[f * 2] - ideal));
        }
        Assert.True(maxError < 0.5e-4, $"max error {maxError:E2}");
    }

    [Fact]
    public void Resampler_seeks_to_the_exact_output_frame()
    {
        using var resampler = new ResamplingSource(new SineSource(44_100, 440, 3), 48_000);
        var all = new float[(int)resampler.LengthFrames!.Value * 2];
        var total = 0;
        int n;
        while ((n = resampler.Read(all.AsSpan(total * 2))) > 0)
        {
            total += n;
        }

        resampler.Seek(50_000);
        Assert.Equal(50_000, resampler.PositionFrames);
        var part = new float[2000];
        Assert.Equal(1000, resampler.Read(part));
        for (var i = 0; i < part.Length; i++)
        {
            Assert.Equal(all[(50_000 * 2) + i], part[i], 4);
        }
    }

    /// <summary>A seekable stereo sine (amplitude 0.5).</summary>
    private sealed class SineSource(int rate, double hz, double seconds) : IAudioSource
    {
        public AudioStreamInfo Info { get; } = new("sine", rate, 2, 32, 0, true);
        public AudioFormat Format { get; } = new(rate, 2);
        public long? LengthFrames { get; } = (long)(rate * seconds);
        public long PositionFrames { get; private set; }
        public bool CanSeek => true;
        public IReadOnlyDictionary<string, string> Tags { get; } = new Dictionary<string, string>();

        public int Read(Span<float> buffer)
        {
            var frames = (int)Math.Min(buffer.Length / 2, LengthFrames!.Value - PositionFrames);
            for (var f = 0; f < frames; f++)
            {
                var v = (float)(0.5 * Math.Sin(2 * Math.PI * hz * (PositionFrames + f) / rate));
                buffer[2 * f] = v;
                buffer[(2 * f) + 1] = v;
            }
            PositionFrames += frames;
            return frames;
        }

        public void Seek(long frame) => PositionFrames = Math.Clamp(frame, 0, LengthFrames!.Value);

        public void Dispose()
        {
        }
    }
}
