using AudioTastic.Core.Audio.Beats;
using AudioTastic.Core.Playlists;

namespace AudioTastic.Analysis;

/// <summary>
/// AN-02 / XF-02: gives the engine a track's beat analysis from the cache; when it is missing, asks the analysis
/// queue to run it with high priority (the engine then mixes Intro/Outro for this transition).
/// </summary>
public sealed class BeatInfoService(AnalysisService analysis) : IBeatInfoProvider
{
    public BeatInfo? TryGet(PlaylistEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var target = AnalysisTarget.For(entry);
        if (!target.IsAnalysable)
        {
            return null;
        }
        if (analysis.TryGetBeats(target) is { } beats)
        {
            return beats;
        }
        analysis.Request(target, AnalysisKinds.Beats, AnalysisPriority.Next);
        return null;
    }
}
