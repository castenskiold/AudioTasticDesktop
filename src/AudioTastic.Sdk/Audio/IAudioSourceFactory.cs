namespace AudioTastic.Sdk.Audio;

/// <summary>
/// PG-02: an input/decoder plugin. The engine asks each registered factory, in priority order,
/// whether it can open a file and uses the first that can.
/// </summary>
public interface IAudioSourceFactory
{
    /// <summary>Display name, e.g. "FFmpeg".</summary>
    string Name { get; }

    /// <summary>File extensions this factory handles, lower case with leading dot (".flac").</summary>
    IReadOnlyCollection<string> Extensions { get; }

    /// <summary>Quick check based on the path (extension); must not open the file.</summary>
    bool CanOpen(string path);

    /// <summary>Opens the file. Throws <see cref="AudioSourceException"/> when the file cannot be decoded.</summary>
    IAudioSource Open(string path);
}
