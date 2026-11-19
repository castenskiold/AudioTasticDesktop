namespace AudioTastic.Metadata;

/// <summary>MD-01: the text fields the tag editor shows and writes.</summary>
public enum TagField
{
    Title,
    Artist,
    AlbumArtist,
    Album,
    Track,
    TrackCount,
    Disc,
    DiscCount,
    Year,
    Genre,
    Composer,
    Comment,
    Lyrics,
}

/// <summary>MD-01: an embedded picture (front cover).</summary>
public sealed record TagPicture(byte[] Data, string MimeType);

/// <summary>
/// MD-01: a file's editable tags as text (numbers as decimal text; empty fields are absent), and its front cover.
/// </summary>
public sealed record EditableTags(IReadOnlyDictionary<TagField, string> Fields, TagPicture? Cover)
{
    public string? this[TagField field] => Fields.GetValueOrDefault(field);

    /// <summary>
    /// The value several files share for <paramref name="field"/>: the text when they all agree (null when all are
    /// empty), and <c>Multiple</c> when they differ (the editor shows "(multiple values)" and leaves them alone).
    /// </summary>
    public static (string? Value, bool Multiple) Common(IReadOnlyList<EditableTags> files, TagField field)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            return (null, false);
        }
        var first = files[0][field];
        return files.Skip(1).All(f => string.Equals(f[field], first, StringComparison.Ordinal)) ? (first, false) : (null, true);
    }
}

/// <summary>
/// MD-01: the changes to apply to one or more files. Only the fields present are changed (null or empty text clears
/// a field); <see cref="Cover"/> null leaves the cover alone, <see cref="CoverChange.Remove"/> removes it.
/// </summary>
public sealed record TagEdit(IReadOnlyDictionary<TagField, string?> Fields, CoverChange? Cover = null)
{
    public bool IsEmpty => Fields.Count == 0 && Cover is null;
}

/// <summary>MD-01: set or remove the front cover.</summary>
public sealed record CoverChange(TagPicture? Picture)
{
    public static CoverChange Remove { get; } = new((TagPicture?)null);
}
