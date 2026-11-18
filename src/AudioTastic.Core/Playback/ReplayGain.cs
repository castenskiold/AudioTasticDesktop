using AudioTastic.Core.Playlists;

namespace AudioTastic.Core.Playback;

/// <summary>PB-03: where an applied gain came from (shown as "RG" in MW5; details in the file info dialog later).</summary>
public enum ReplayGainSource
{
    TrackTag,
    AlbumTag,
    TrackAnalysis,
    AlbumAnalysis,
    /// <summary>No loudness known: only the pre-amp for untagged files.</summary>
    Preamp,
}

/// <summary>PB-03: the gain applied to a track: dB value (including pre-amp) and the linear factor after clipping prevention.</summary>
public sealed record ReplayGainDecision(double GainDb, float Linear, ReplayGainSource Source);

public sealed class ReplayGainChangedEventArgs(string? path) : EventArgs
{
    /// <summary>The file whose gain may have changed; null when all may have (settings changed).</summary>
    public string? Path { get; } = path;
}

/// <summary>
/// PB-03: decides the ReplayGain for an entry. The engine asks before a track starts and again when
/// <see cref="GainChanged"/> names the playing file (e.g. its loudness analysis just finished).
/// </summary>
public interface IReplayGainProvider
{
    /// <summary>The gain for <paramref name="entry"/>, or null to play it unchanged. Called on the engine's control thread.</summary>
    ReplayGainDecision? GetGain(Playlist playlist, PlaylistEntry entry);

    event EventHandler<ReplayGainChangedEventArgs>? GainChanged;
}
