using AudioTastic.Sdk.Audio;

namespace AudioTastic.Input.Ffmpeg;

/// <summary>
/// FM-01: the default input plugin. Registered directly by the app until the plugin loader arrives (PG-01, P4).
/// Extensions grow as later format features (FM-02 ... FM-10) are verified.
/// </summary>
public sealed class FfmpegSourceFactory : IAudioSourceFactory
{
    private static readonly string[] CoreExtensions =
        [".wav", ".flac", ".mp3", ".aiff", ".aif", ".aifc", ".ape", ".aac", ".m4a", ".wv"];

    public string Name => "FFmpeg";

    public IReadOnlyCollection<string> Extensions => CoreExtensions;

    public bool CanOpen(string path) =>
        CoreExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public IAudioSource Open(string path) => FfmpegAudioSource.Open(path);
}
