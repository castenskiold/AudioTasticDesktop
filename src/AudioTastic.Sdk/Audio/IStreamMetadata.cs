namespace AudioTastic.Sdk.Audio;

/// <summary>
/// FM-10: implemented by sources of live streams (internet radio) that carry a now-playing title (ICY metadata).
/// <see cref="StreamTitleChanged"/> is raised on the decoder thread.
/// </summary>
public interface IStreamMetadata
{
    /// <summary>The current title, e.g. "Artist - Title"; null when the stream sends none.</summary>
    string? StreamTitle { get; }

    /// <summary>The station name (icy-name), when known.</summary>
    string? StationName { get; }

    event EventHandler<string?>? StreamTitleChanged;
}

/// <summary>Implemented by wrappers around a source (guards, buffers) to expose the wrapped source's stream metadata.</summary>
public interface IStreamMetadataSource
{
    IStreamMetadata? StreamMetadata { get; }
}
