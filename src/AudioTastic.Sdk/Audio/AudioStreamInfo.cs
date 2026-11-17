namespace AudioTastic.Sdk.Audio;

/// <summary>
/// PG-02: describes an audio stream as stored in the source (what the user sees in MW5 and the file info dialog).
/// Decoded audio is always interleaved 32-bit float at <see cref="SampleRate"/> and <see cref="Channels"/>.
/// </summary>
/// <param name="Codec">Short codec name, e.g. "flac", "mp3", "pcm_s24le".</param>
/// <param name="SampleRate">Samples per second per channel.</param>
/// <param name="Channels">Number of channels.</param>
/// <param name="BitsPerSample">Bit depth of the source samples; 0 for lossy codecs where it has no meaning.</param>
/// <param name="Bitrate">Average bitrate in bits per second; 0 when unknown.</param>
/// <param name="IsLossless">True for lossless and PCM codecs.</param>
public sealed record AudioStreamInfo(
    string Codec,
    int SampleRate,
    int Channels,
    int BitsPerSample,
    long Bitrate,
    bool IsLossless)
{
    /// <summary>Format of the decoded output of this stream.</summary>
    public AudioFormat OutputFormat => new(SampleRate, Channels);
}
