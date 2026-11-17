using AudioTastic.Sdk.Metadata;
using AudioTastic.Sdk.Playlists;

namespace AudioTastic.Core.Playlists;

/// <summary>
/// PL-01: one entry in a playlist: a file (or URL), optionally a CUE virtual track inside it (FM-09), and the tags
/// shown for it. Entries are compared by identity: the same file can be in a playlist twice. Tags are filled in
/// lazily (MD-01) and may be replaced from any thread; everything else is immutable.
/// </summary>
public sealed class PlaylistEntry
{
    private TrackTags? _tags;
    private int _missing; // 0 unknown, 1 present, 2 missing

    public PlaylistEntry(string path, TimeSpan? start = null, TimeSpan? end = null, TrackTags? tags = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        Path = path;
        Start = start;
        End = end;
        _tags = tags;
    }

    /// <summary>Absolute file path, or a URL.</summary>
    public string Path { get; }

    /// <summary>FM-09: start of the virtual track, or null for a whole file.</summary>
    public TimeSpan? Start { get; }

    /// <summary>FM-09: end of the virtual track, or null for "to the end of the file".</summary>
    public TimeSpan? End { get; }

    public bool IsVirtual => Start is not null;

    /// <summary>Tags for display (from the playlist file, a CUE sheet or TagService). Null until known.</summary>
    public TrackTags? Tags
    {
        get => Volatile.Read(ref _tags);
        set => Volatile.Write(ref _tags, value);
    }

    /// <summary>True once TagService has read the file (playlist-file titles alone do not count).</summary>
    public bool TagsLoaded { get; set; }

    /// <summary>Length of the entry: the CUE range when known, otherwise the file's duration from the tags.</summary>
    public TimeSpan? Duration =>
        Start is { } s && End is { } e ? e - s
        : Tags?.Duration is { } d ? (Start is { } start ? d - start : d)
        : null;

    /// <summary>"Artist – Title", or the file name without extension.</summary>
    public string DisplayName => Tags?.DisplayName ?? FileName;

    public string FileName => System.IO.Path.GetFileNameWithoutExtension(Path);

    public bool IsUrl => Path.Contains("://", StringComparison.Ordinal);

    /// <summary>PL-06 dead entries: null until checked.</summary>
    public bool? IsMissing
    {
        get => Volatile.Read(ref _missing) switch { 1 => false, 2 => true, _ => null };
        set => Volatile.Write(ref _missing, value switch { false => 1, true => 2, null => 0 });
    }

    /// <summary>A new entry for the same file/range with the same tags (copying between tabs, duplicating tabs).</summary>
    public PlaylistEntry Clone() => new(Path, Start, End, Tags) { TagsLoaded = TagsLoaded };

    /// <summary>Same file and same CUE range (PL-06 duplicates).</summary>
    public bool SameTarget(PlaylistEntry other) =>
        string.Equals(Path, other.Path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
        && Start == other.Start && End == other.End;

    public static PlaylistEntry FromItem(PlaylistItem item)
    {
        var tags = item.Title is null && item.Artist is null && item.Duration is null && item.TrackNumber is null && item.Album is null
            ? null
            : new TrackTags(item.Title, item.Artist, item.Album, TrackNumber: item.TrackNumber,
                Duration: item.IsVirtual ? null : item.Duration);
        return new PlaylistEntry(item.Path, item.Start, item.End, tags);
    }

    public PlaylistItem ToItem() => new(Path, Tags?.Title, Tags?.Artist, Duration, Start, End, Tags?.TrackNumber, Tags?.Album);

    public override string ToString() => DisplayName;
}
