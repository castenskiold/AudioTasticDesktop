namespace AudioTastic.Casting;

/// <summary>Album and cover for the track shown on the TV (read by the app; optional).</summary>
public sealed record CastExtras(string? Album, byte[]? Cover, string? CoverType);
