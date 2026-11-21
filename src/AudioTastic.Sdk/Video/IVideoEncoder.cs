using AudioTastic.Sdk.Audio;

namespace AudioTastic.Sdk.Video;

/// <summary>PG-02 (SDK 0.4.2, CN-09): containers for live video sent to cast devices.</summary>
public enum VideoContainer
{
    /// <summary>Fragmented MP4 (H.264 + AAC), played live by Chromecasts.</summary>
    FragmentedMp4,

    /// <summary>MPEG transport stream (H.264 + AAC), played live by DLNA TVs.</summary>
    MpegTs,
}

/// <summary>
/// PG-02 (SDK 0.4.2): what to encode. Frames are <paramref name="Width"/> × <paramref name="Height"/> premultiplied
/// BGRA at <paramref name="FramesPerSecond"/>; <paramref name="Audio"/> is the format of the audio written alongside,
/// or null for video only.
/// </summary>
public sealed record VideoEncodeSettings(
    VideoContainer Container, int Width, int Height, int FramesPerSecond, int VideoBitrate, AudioFormat? Audio = null, int AudioBitrate = 192_000);

/// <summary>
/// PG-02 (SDK 0.4.2): writes a live H.264 video stream (with AAC audio when asked) to a stream that may not be
/// seekable. Frames are spaced 1 / frames per second apart in the order written; audio is timed by its sample count.
/// The caller paces both in real time. All calls come from one thread.
/// </summary>
public interface IVideoEncoder : IDisposable
{
    /// <summary>The H.264 encoder in use (e.g. "h264_mf"), for logs.</summary>
    string EncoderName { get; }

    /// <summary>The next frame: Width × Height × 4 bytes of premultiplied BGRA, rows top to bottom.</summary>
    void WriteFrame(ReadOnlySpan<byte> bgra);

    /// <summary>Interleaved float audio in <see cref="VideoEncodeSettings.Audio"/>, in any amount.</summary>
    void WriteAudio(ReadOnlySpan<float> samples);

    /// <summary>Flushes the encoders and ends the stream.</summary>
    void Finish();
}

/// <summary>PG-02 (SDK 0.4.2): creates video encoders. Input plugins offer them through <c>IInputPlugin.VideoEncoderFactories</c>.</summary>
public interface IVideoEncoderFactory
{
    string Name { get; }

    /// <summary>The containers this machine can write (empty when it has no H.264 encoder).</summary>
    IReadOnlyCollection<VideoContainer> Containers { get; }

    /// <summary>Creates an encoder writing to <paramref name="output"/>; "cannot encode here" is an <see cref="AudioSourceException"/>.</summary>
    IVideoEncoder Create(VideoEncodeSettings settings, Stream output);
}
