using System.Globalization;
using System.Text;
using AudioTastic.Core.Audio.Beats;
using AudioTastic.Core.Playlists;
using AudioTastic.Data.Cache;

namespace AudioTastic.Analysis;

/// <summary>
/// AN-02 / XF-02: gives the engine a track's beat analysis from the cache; when it is missing, asks the analysis
/// queue to run it with high priority (the engine then mixes Intro/Outro for this transition). A per-file nudge
/// (playlist menu › Adjust beat grid…, cache kind "beatnudge") moves the grid when the detection is a little off.
/// </summary>
public sealed class BeatInfoService(AnalysisService analysis, CacheStore? cache = null) : IBeatInfoProvider
{
    public const string NudgeKind = "beatnudge";
    private const int NudgeVersion = 1;

    /// <summary>The grid nudge of a file in milliseconds (0 when none).</summary>
    public double GetNudge(string path)
    {
        if (cache is null || path.Contains("://", StringComparison.Ordinal) || !File.Exists(path))
        {
            return 0;
        }
        return cache.TryGet(FileKey.FromFile(path), NudgeKind, NudgeVersion) is { } data &&
            double.TryParse(Encoding.UTF8.GetString(data), NumberStyles.Float, CultureInfo.InvariantCulture, out var ms) ? ms : 0;
    }

    /// <summary>Sets the grid nudge of a file in milliseconds (0 removes it).</summary>
    public void SetNudge(string path, double milliseconds)
    {
        if (cache is null || path.Contains("://", StringComparison.Ordinal) || !File.Exists(path))
        {
            return;
        }
        if (Math.Abs(milliseconds) < 0.05)
        {
            cache.Delete(path, NudgeKind);
        }
        else
        {
            cache.Put(FileKey.FromFile(path), NudgeKind, NudgeVersion, Encoding.UTF8.GetBytes(milliseconds.ToString("0.0", CultureInfo.InvariantCulture)));
        }
    }

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
            return entry.Start is null ? beats.Nudged(GetNudge(entry.Path) / 1000) : beats;
        }
        analysis.Request(target, AnalysisKinds.Beats, AnalysisPriority.Next);
        return null;
    }
}
