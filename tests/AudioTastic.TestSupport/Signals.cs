using System.Security.Cryptography;
using System.Runtime.InteropServices;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.TestSupport;

/// <summary>Helpers for reading whole sources and comparing decoded signals.</summary>
public static class Signals
{
    /// <summary>Reads the rest of <paramref name="source"/> in odd-sized chunks (to exercise buffer boundaries).</summary>
    public static float[] ReadAll(IAudioSource source, int chunkFrames = 1000)
    {
        var channels = source.Format.Channels;
        var result = new List<float>(1 << 20);
        var buffer = new float[chunkFrames * channels];
        int frames;
        while ((frames = source.Read(buffer)) > 0)
        {
            result.AddRange(buffer.AsSpan(0, frames * channels));
        }
        return [.. result];
    }

    /// <summary>Reads up to <paramref name="frames"/> frames.</summary>
    public static float[] ReadFrames(IAudioSource source, int frames)
    {
        var channels = source.Format.Channels;
        var buffer = new float[frames * channels];
        var total = 0;
        while (total < frames)
        {
            var n = source.Read(buffer.AsSpan(total * channels));
            if (n == 0)
            {
                break;
            }
            total += n;
        }
        return buffer[..(total * channels)];
    }

    public static string Sha256(ReadOnlySpan<float> samples) =>
        Convert.ToHexString(SHA256.HashData(MemoryMarshal.AsBytes(samples)));

    public static float MaxAbsDifference(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        var n = Math.Min(a.Length, b.Length);
        var max = 0f;
        for (var i = 0; i < n; i++)
        {
            max = Math.Max(max, Math.Abs(a[i] - b[i]));
        }
        return max;
    }

    /// <summary>
    /// The frame offset in [-maxLag, maxLag] at which <paramref name="candidate"/> best matches <paramref name="reference"/>
    /// (least squared error). 0 means the two are aligned.
    /// </summary>
    public static int BestLag(ReadOnlySpan<float> reference, ReadOnlySpan<float> candidate, int channels, int maxLag)
    {
        var bestLag = 0;
        var bestError = double.MaxValue;
        for (var lag = -maxLag; lag <= maxLag; lag++)
        {
            double error = 0;
            var count = 0;
            for (var i = maxLag * channels; i < candidate.Length - (maxLag * channels); i++)
            {
                var j = i + (lag * channels);
                if (j < 0 || j >= reference.Length)
                {
                    continue;
                }
                var d = reference[j] - candidate[i];
                error += d * d;
                count++;
            }
            if (count > 0 && error / count < bestError)
            {
                bestError = error / count;
                bestLag = lag;
            }
        }
        return bestLag;
    }
}
