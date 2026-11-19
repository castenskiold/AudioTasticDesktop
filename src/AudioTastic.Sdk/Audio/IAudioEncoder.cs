namespace AudioTastic.Sdk.Audio;

/// <summary>PG-02 (SDK 0.4.1): formats an encoder plugin can write.</summary>
public enum EncodeFormat
{
    /// <summary>FLAC (lossless), in a .flac stream.</summary>
    Flac,

    /// <summary>MP3, in a .mp3 stream.</summary>
    Mp3,
}

/// <summary>What to encode to.</summary>
/// <param name="Format">The output format.</param>
/// <param name="BitsPerSample">Lossless formats: 16 or 24.</param>
/// <param name="BitrateKbps">Lossy formats: the bit rate, e.g. 320.</param>
public sealed record EncodeSettings(EncodeFormat Format, int BitsPerSample = 16, int BitrateKbps = 320);

/// <summary>
/// PG-02 (SDK 0.4.1): writes encoded audio to a stream. Used for cast transcoding (CN-04) and conversion (TL-01).
/// Not thread-safe; the caller writes from one thread. The output stream may be non-seekable (a network response).
/// </summary>
public interface IAudioEncoder : IDisposable
{
    /// <summary>Encodes interleaved float samples (any count of whole frames) in the format given at creation.</summary>
    void Write(ReadOnlySpan<float> samples);

    /// <summary>Encodes what is left and writes the end of the stream; call once, after the last <see cref="Write"/>.</summary>
    void Finish();
}

/// <summary>
/// PG-02 (SDK 0.4.1): creates encoders. Input plugins offer them through <c>IInputPlugin.EncoderFactories</c>.
/// <see cref="Create"/> throws <see cref="AudioSourceException"/> when the format cannot be written here.
/// </summary>
public interface IAudioEncoderFactory
{
    /// <summary>Display name, e.g. "FFmpeg".</summary>
    string Name { get; }

    /// <summary>The formats this machine can write (an FFmpeg build without an MP3 encoder leaves out MP3).</summary>
    IReadOnlyCollection<EncodeFormat> Formats { get; }

    /// <summary>A new encoder writing <paramref name="settings"/>' format for input in <paramref name="format"/> to <paramref name="output"/>.</summary>
    IAudioEncoder Create(EncodeSettings settings, AudioFormat format, Stream output);
}
