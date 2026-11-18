using AudioTastic.Sdk.Audio;

namespace AudioTastic.Input.Ffmpeg;

/// <summary>
/// FM-01 … FM-06, FM-10: the default decoder. Opens every file extension FFmpeg handles for us, and http(s) stream URLs.
/// Other input plugins with a higher manifest priority win for the same extension.
/// </summary>
public sealed class FfmpegSourceFactory : IAudioSourceFactory, IDopSourceFactory
{
    private static readonly string[] FileExtensions =
    [
        // FM-01 core formats
        ".wav", ".flac", ".mp3", ".aiff", ".aif", ".aifc", ".ape", ".aac", ".m4a", ".wv",
        // FM-02 Ogg Vorbis and Opus
        ".ogg", ".oga", ".opus",
        // FM-03 ALAC / AAC in MP4
        ".mp4", ".m4b", ".alac",
        // FM-04 WMA
        ".wma", ".asf",
        // FM-06 Musepack, TTA, Shorten (OptimFROG is its own plugin)
        ".mpc", ".mp+", ".mpp", ".tta", ".shn",
        // FM-05 DSD (decoded to PCM; DoP through IDopSourceFactory)
        ".dsf", ".dff",
    ];

    public string Name => "FFmpeg";

    public IReadOnlyCollection<string> Extensions => FileExtensions;

    public bool CanOpen(string path) =>
        IsStreamUrl(path) || FileExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public IAudioSource Open(string path)
    {
        var source = FfmpegAudioSource.Open(path);
        // FM-10: live streams (no length) get buffering and automatic reconnects; HTTP files play like local files.
        return IsStreamUrl(path) && source.LengthFrames is null ? new LiveStreamSource(path, source) : source;
    }

    /// <summary>FM-05: DSF and DFF files can be played as DoP.</summary>
    public bool IsDsd(string path) =>
        System.IO.Path.GetExtension(path) is { } e && (e.Equals(".dsf", StringComparison.OrdinalIgnoreCase) || e.Equals(".dff", StringComparison.OrdinalIgnoreCase));

    public IAudioSource OpenDop(string path) => DopSource.Open(path);

    /// <summary>FM-10: internet radio and other HTTP streams.</summary>
    public static bool IsStreamUrl(string path) =>
        path.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || path.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
