namespace AudioTastic.Sdk.Metadata;

/// <summary>
/// MD-01: the tags the player displays and sorts by. All fields are optional. Read by TagService (TagLibSharp,
/// FFmpeg fallback) or taken from playlist files and CUE sheets.
/// </summary>
public sealed record TrackTags(
    string? Title = null,
    string? Artist = null,
    string? Album = null,
    string? AlbumArtist = null,
    int? TrackNumber = null,
    int? DiscNumber = null,
    int? Year = null,
    string? Genre = null,
    TimeSpan? Duration = null,
    ReplayGainInfo? ReplayGain = null)
{
    /// <summary>"Artist – Title", or the title alone; null when there is no title.</summary>
    public string? DisplayName =>
        string.IsNullOrWhiteSpace(Title) ? null
        : string.IsNullOrWhiteSpace(Artist) ? Title
        : $"{Artist} – {Title}";

    /// <summary>Fields of <paramref name="other"/> fill the gaps of this instance.</summary>
    public TrackTags Merge(TrackTags? other) => other is null ? this : new TrackTags(
        Title ?? other.Title,
        Artist ?? other.Artist,
        Album ?? other.Album,
        AlbumArtist ?? other.AlbumArtist,
        TrackNumber ?? other.TrackNumber,
        DiscNumber ?? other.DiscNumber,
        Year ?? other.Year,
        Genre ?? other.Genre,
        Duration ?? other.Duration,
        ReplayGain ?? other.ReplayGain);
}

/// <summary>
/// PB-03: ReplayGain values from tags (REPLAYGAIN_* in ID3/Vorbis/APE/MP4; Opus R128_* converted to the ReplayGain
/// reference of -18 LUFS). Gains in dB, peaks as linear sample values (1.0 = full scale).
/// </summary>
public sealed record ReplayGainInfo(double? TrackGainDb = null, double? TrackPeak = null, double? AlbumGainDb = null, double? AlbumPeak = null)
{
    public bool IsEmpty => TrackGainDb is null && AlbumGainDb is null;
}
