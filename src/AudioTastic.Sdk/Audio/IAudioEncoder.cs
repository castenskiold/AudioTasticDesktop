namespace AudioTastic.Sdk.Audio;

/// <summary>PG-02 (SDK 0.4.1; 0.4.3 added Opus, AAC, ALAC, Vorbis and WAV): formats an encoder plugin can write.</summary>
public enum EncodeFormat
{
    /// <summary>FLAC (lossless), in a .flac stream.</summary>
    Flac,

    /// <summary>MP3, in a .mp3 stream.</summary>
    Mp3,

    /// <summary>SDK 0.4.3: Opus in Ogg (.opus).</summary>
    Opus,

    /// <summary>SDK 0.4.3: AAC in MP4 (.m4a). The output stream must be seekable.</summary>
    Aac,

    /// <summary>SDK 0.4.3: Apple Lossless in MP4 (.m4a). The output stream must be seekable.</summary>
    Alac,

    /// <summary>SDK 0.4.3: Vorbis in Ogg (.ogg).</summary>
    Vorbis,

    /// <summary>SDK 0.4.3: PCM in a WAV file (16 or 24 bit). Sizes in the header need a seekable stream.</summary>
    Wav,
}

/// <summary>What to encode to.</summary>
/// <param name="Format">The output format.</param>
/// <param name="BitsPerSample">Lossless formats: 16 or 24.</param>
/// <param name="BitrateKbps">Lossy formats: the bit rate, e.g. 320 (an average for Vorbis).</param>
public sealed record EncodeSettings(EncodeFormat Format, int BitsPerSample = 16, int BitrateKbps = 320)
{
    /// <summary>SDK 0.4.3: MP3 with a variable bit rate (<see cref="VbrQuality"/>) instead of <see cref="BitrateKbps"/>.</summary>
    public bool Vbr { get; init; }

    /// <summary>SDK 0.4.3: VBR quality like LAME's -V: 0 (best, about 245 kbps) to 9 (smallest). Default 2 (about 190 kbps).</summary>
    public int VbrQuality { get; init; } = 2;
}

/// <summary>
/// PG-02 (SDK 0.4.1): writes encoded audio to a stream. Used for cast transcoding (CN-04) and conversion (TL-01).
/// Not thread-safe; the caller writes from one thread. The output stream may be non-seekable (a network response);
/// when it is seekable the encoder may go back to finish headers (MP4, WAV, the MP3 gapless header).
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

    /// <summary>
    /// SDK 0.4.3: the input format an encoder for <paramref name="settings"/> accepts, closest to <paramref name="input"/>
    /// (Opus needs 48 kHz, MP3 at most 48 kHz and two channels). The caller converts to it before
    /// <see cref="Create"/>. Default: <paramref name="input"/> unchanged.
    /// </summary>
    AudioFormat InputFormatFor(EncodeSettings settings, AudioFormat input) => input;
}
