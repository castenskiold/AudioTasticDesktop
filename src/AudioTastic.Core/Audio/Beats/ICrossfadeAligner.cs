namespace AudioTastic.Core.Audio.Beats;

/// <summary>
/// XF-02: what the aligner needs to line up two tracks' kicks where they are heard together. Times in track seconds;
/// <see cref="FadeIn"/> and <see cref="Overlap"/> in output seconds.
/// </summary>
public sealed record CrossfadeAlignment(string OutgoingPath, double MixStart, double OutgoingSpeed, string IncomingPath, double IncomingStart,
    double IncomingSpeed, double FadeIn, double Overlap, double Period);

/// <summary>
/// XF-02: refines a beat-matched transition from the audio itself: decodes the full-volume overlap of both tracks
/// and returns how far (track seconds) the incoming entry point must move so the kicks coincide there; null when the
/// audio gives no clear answer (the grid's own alignment is kept then).
/// </summary>
public interface ICrossfadeAligner
{
    double? Refine(CrossfadeAlignment alignment);
}
