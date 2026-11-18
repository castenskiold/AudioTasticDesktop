using AudioTastic.Sdk.Audio;

namespace AudioTastic.Analysis;

/// <summary>
/// AN-01: one kind of analysis. The analysis service decodes a file once and feeds every requested analyser;
/// results are cached under <see cref="Kind"/> and <see cref="DataVersion"/> (bump it when the output changes).
/// </summary>
public interface IAnalyzer
{
    string Kind { get; }
    int DataVersion { get; }
    IAnalysisRun Start(AudioFormat format, long? lengthFrames);
}

/// <summary>One analysis of one file (not thread-safe; used by one worker).</summary>
public interface IAnalysisRun
{
    void Process(ReadOnlySpan<float> interleaved);
    byte[] Finish();
}

/// <summary>
/// CO-07: reduces the audio to min/max pairs. Collects small fixed blocks while decoding (the length may be unknown),
/// then folds them into <see cref="Buckets"/> buckets.
/// </summary>
public sealed class WaveformAnalyzer : IAnalyzer
{
    public const int Buckets = 2000;
    public string Kind => "waveform";
    public int DataVersion => 1;

    // Blocks of at most 256 frames; shorter for short tracks, so every track gets the full bucket count.
    public IAnalysisRun Start(AudioFormat format, long? lengthFrames) =>
        new Run(format, lengthFrames is > 0 ? (int)Math.Clamp(lengthFrames.Value / (Buckets * 4L), 1, 256) : 256);

    private sealed class Run(AudioFormat format, int blockFrames) : IAnalysisRun
    {
        private readonly int _channels = Math.Min(2, format.Channels);
        private readonly List<float>[] _min = [[], []];
        private readonly List<float>[] _max = [[], []];
        private readonly float[] _blockMin = [float.MaxValue, float.MaxValue];
        private readonly float[] _blockMax = [float.MinValue, float.MinValue];
        private int _inBlock;
        private long _frames;

        public void Process(ReadOnlySpan<float> interleaved)
        {
            var source = format.Channels;
            var frames = interleaved.Length / source;
            for (var f = 0; f < frames; f++)
            {
                for (var c = 0; c < source; c++)
                {
                    var v = interleaved[(f * source) + c];
                    var target = Math.Min(c, _channels - 1); // channels beyond the first two fold into the second
                    if (v < _blockMin[target])
                    {
                        _blockMin[target] = v;
                    }
                    if (v > _blockMax[target])
                    {
                        _blockMax[target] = v;
                    }
                }
                if (++_inBlock == blockFrames)
                {
                    FlushBlock();
                }
            }
            _frames += frames;
        }

        public byte[] Finish()
        {
            if (_inBlock > 0)
            {
                FlushBlock();
            }
            var blocks = _min[0].Count;
            var buckets = Math.Max(1, Math.Min(Buckets, blocks));
            var min = new float[_channels][];
            var max = new float[_channels][];
            for (var c = 0; c < _channels; c++)
            {
                min[c] = new float[buckets];
                max[c] = new float[buckets];
                for (var b = 0; b < buckets; b++)
                {
                    var from = (int)((long)b * blocks / buckets);
                    var to = Math.Max(from + 1, (int)((long)(b + 1) * blocks / buckets));
                    float lo = 0, hi = 0;
                    for (var i = from; i < to && i < blocks; i++)
                    {
                        lo = Math.Min(lo, _min[c][i]);
                        hi = Math.Max(hi, _max[c][i]);
                    }
                    min[c][b] = lo;
                    max[c][b] = hi;
                }
            }
            return new Waveform(_channels, min, max, format.FramesToTime(_frames)).Serialize();
        }

        private void FlushBlock()
        {
            for (var c = 0; c < _channels; c++)
            {
                _min[c].Add(_blockMin[c]);
                _max[c].Add(_blockMax[c]);
                _blockMin[c] = float.MaxValue;
                _blockMax[c] = float.MinValue;
            }
            _inBlock = 0;
        }
    }
}

/// <summary>PB-03: EBU R128 loudness (see <see cref="LoudnessMeter"/>).</summary>
public sealed class LoudnessAnalyzer : IAnalyzer
{
    public string Kind => "loudness";
    public int DataVersion => 1;

    public IAnalysisRun Start(AudioFormat format, long? lengthFrames) => new Run(new LoudnessMeter(format.SampleRate, format.Channels));

    private sealed class Run(LoudnessMeter meter) : IAnalysisRun
    {
        public void Process(ReadOnlySpan<float> interleaved) => meter.Process(interleaved);

        public byte[] Finish() => new LoudnessResult(meter.IntegratedLoudness, meter.SamplePeak, [.. meter.BlockEnergies]).Serialize();
    }
}
