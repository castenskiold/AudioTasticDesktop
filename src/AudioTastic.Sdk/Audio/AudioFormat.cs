namespace AudioTastic.Sdk.Audio;

/// <summary>
/// PG-02: format of a block of decoded audio. Samples are always interleaved 32-bit float in the range [-1, 1].
/// </summary>
public readonly record struct AudioFormat
{
    public AudioFormat(int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(channels, 1);
        SampleRate = sampleRate;
        Channels = channels;
    }

    public int SampleRate { get; }
    public int Channels { get; }

    /// <summary>Converts a frame count (one sample per channel) to a duration.</summary>
    public TimeSpan FramesToTime(long frames) => TimeSpan.FromSeconds((double)frames / SampleRate);

    /// <summary>Converts a duration to the nearest frame count.</summary>
    public long TimeToFrames(TimeSpan time) => (long)Math.Round(time.TotalSeconds * SampleRate);

    public override string ToString() => $"{SampleRate} Hz, {Channels} ch, float32";
}
