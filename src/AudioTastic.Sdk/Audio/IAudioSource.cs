namespace AudioTastic.Sdk.Audio;

/// <summary>
/// PG-02 / FM-01: a decoded audio stream (input plugin output). Not thread-safe: the engine calls it from one
/// decoder thread at a time, never from the audio thread.
/// </summary>
public interface IAudioSource : IDisposable
{
    /// <summary>Source stream description (codec, rate, bit depth, ...).</summary>
    AudioStreamInfo Info { get; }

    /// <summary>Format of the samples returned by <see cref="Read"/>.</summary>
    AudioFormat Format { get; }

    /// <summary>Length in frames, or null when unknown (e.g. live streams).</summary>
    long? LengthFrames { get; }

    /// <summary>Frame index of the next frame <see cref="Read"/> returns.</summary>
    long PositionFrames { get; }

    /// <summary>True when <see cref="Seek"/> is supported.</summary>
    bool CanSeek { get; }

    /// <summary>Raw tags from the container/stream (key → value); keys as stored, compared case-insensitively.</summary>
    IReadOnlyDictionary<string, string> Tags { get; }

    /// <summary>
    /// Fills <paramref name="buffer"/> with interleaved float samples. <paramref name="buffer"/> length must be a
    /// multiple of <see cref="AudioFormat.Channels"/>. Returns the number of frames written; 0 means end of stream.
    /// May return fewer frames than requested before the end.
    /// </summary>
    int Read(Span<float> buffer);

    /// <summary>Moves to <paramref name="frame"/> sample-accurately; the next <see cref="Read"/> starts exactly there.</summary>
    void Seek(long frame);
}
