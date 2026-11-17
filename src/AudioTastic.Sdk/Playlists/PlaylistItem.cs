namespace AudioTastic.Sdk.Playlists;

/// <summary>
/// PG-02 / PL-03: one entry as read from or written to a playlist file. Paths are absolute (or URLs) after reading;
/// formats resolve relative paths against the playlist file's folder.
/// </summary>
/// <param name="Path">Absolute file path or URL.</param>
/// <param name="Title">Display title stored in the playlist (#EXTINF, PLS TitleN, XSPF title), if any.</param>
/// <param name="Artist">Artist stored in the playlist (XSPF creator, CUE PERFORMER), if any.</param>
/// <param name="Duration">Length stored in the playlist, if any.</param>
/// <param name="Start">FM-09: start of a virtual track inside <paramref name="Path"/> (CUE INDEX 01).</param>
/// <param name="End">FM-09: end of a virtual track (next INDEX 01), or null for "to the end of the file".</param>
/// <param name="TrackNumber">CUE TRACK number, if any.</param>
/// <param name="Album">CUE album TITLE, if any.</param>
public sealed record PlaylistItem(
    string Path,
    string? Title = null,
    string? Artist = null,
    TimeSpan? Duration = null,
    TimeSpan? Start = null,
    TimeSpan? End = null,
    int? TrackNumber = null,
    string? Album = null)
{
    /// <summary>True for a CUE virtual track (part of a file).</summary>
    public bool IsVirtual => Start is not null;
}

/// <summary>PL-03: a playlist file format (M3U, PLS, XSPF, ...). Plugins can add formats (PG-01).</summary>
public interface IPlaylistFormat
{
    /// <summary>Display name, e.g. "M3U8 playlist".</summary>
    string Name { get; }

    /// <summary>Extensions, lower case with leading dot.</summary>
    IReadOnlyCollection<string> Extensions { get; }

    bool CanWrite { get; }

    /// <summary>Reads <paramref name="file"/>; relative paths are resolved against its folder.</summary>
    IReadOnlyList<PlaylistItem> Read(string file);

    /// <summary>Writes <paramref name="items"/>; with <paramref name="relativePaths"/> paths under the file's folder are written relative.</summary>
    void Write(string file, IReadOnlyList<PlaylistItem> items, bool relativePaths);
}
