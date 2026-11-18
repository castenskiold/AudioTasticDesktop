using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using AudioTastic.Data.Cache;
using AudioTastic.Data.Settings;
using AudioTastic.Metadata;
using AudioTastic.Sdk.Metadata;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Analysis;

/// <summary>
/// PB-03: decides each track's ReplayGain.
/// <list type="number">
/// <item>Mode (Settings › Playback): Off, Track, Album, or Auto (album gain when a neighbouring entry in the playlist is
/// from the same album).</item>
/// <item>ReplayGain tags (REPLAYGAIN_*, Opus R128_*) plus the pre-amp for tagged files.</item>
/// <item>Otherwise, when "analyse untagged files" is on, our EBU R128 analysis (cached): track gain = −18 − I; album
/// gain from all the album's tracks in the playlist once they are analysed. Until the analysis is ready the track
/// plays with the untagged pre-amp only; <see cref="GainChanged"/> then lets the engine update it.</item>
/// <item>"Prevent clipping" limits the gain so the (track or album) peak stays at or below full scale.</item>
/// <item>"Write tags" stores analysed track values in the file (TagLib), and keeps the cached analysis for the
/// changed file.</item>
/// </list>
/// </summary>
public sealed class ReplayGainService : IReplayGainProvider, IDisposable
{
    private readonly SettingsStore _settings;
    private readonly TagService _tags;
    private readonly AnalysisService _analysis;
    private readonly ILogger<ReplayGainService> _logger;

    public ReplayGainService(SettingsStore settings, TagService tags, AnalysisService analysis, ILogger<ReplayGainService> logger)
    {
        _settings = settings;
        _tags = tags;
        _analysis = analysis;
        _logger = logger;
        _analysis.Completed += OnAnalysisCompleted;
        _settings.Changed += OnSettingsChanged;
    }

    public event EventHandler<ReplayGainChangedEventArgs>? GainChanged;

    private ReplayGainSettings Options => _settings.Current.Playback.ReplayGain;

    public ReplayGainDecision? GetGain(Playlist playlist, PlaylistEntry entry)
    {
        var options = Options;
        if (options.Mode == ReplayGainMode.Off)
        {
            return null;
        }
        if (entry.IsUrl)
        {
            return Decide(options.PreampUntaggedDb, null, ReplayGainSource.Preamp, options);
        }
        var album = options.Mode == ReplayGainMode.Album || (options.Mode == ReplayGainMode.Auto && HasAlbumNeighbour(playlist, entry));

        // 1. Tags (a CUE virtual track's file tags describe the whole file, so they only count as album values).
        if (TagsOf(entry)?.ReplayGain is { } rg)
        {
            var (gain, peak, source) = album || entry.IsVirtual
                ? (rg.AlbumGainDb ?? rg.TrackGainDb, rg.AlbumPeak ?? rg.TrackPeak, rg.AlbumGainDb is null ? ReplayGainSource.TrackTag : ReplayGainSource.AlbumTag)
                : (rg.TrackGainDb ?? rg.AlbumGainDb, rg.TrackPeak ?? rg.AlbumPeak, rg.TrackGainDb is null ? ReplayGainSource.AlbumTag : ReplayGainSource.TrackTag);
            if (gain is { } g && (!entry.IsVirtual || album || !options.AnalyseUntagged))
            {
                return Decide(g + options.PreampTaggedDb, peak, source, options);
            }
        }

        // 2. Our analysis.
        if (options.AnalyseUntagged)
        {
            var target = AnalysisTarget.For(entry);
            if (_analysis.TryGetLoudness(target) is { } track)
            {
                if (album && AlbumLoudness(playlist, entry) is { } albumGain)
                {
                    return Decide(albumGain.GainDb + options.PreampUntaggedDb, albumGain.Peak, ReplayGainSource.AlbumAnalysis, options);
                }
                return Decide(track.GainDb + options.PreampUntaggedDb, track.SamplePeak, ReplayGainSource.TrackAnalysis, options);
            }
            _analysis.Request(target, AnalysisKinds.Loudness, AnalysisPriority.Current);
        }

        // 3. Nothing known yet.
        return Decide(options.PreampUntaggedDb, null, ReplayGainSource.Preamp, options);
    }

    /// <summary>Queues loudness analysis ahead of time (the next track), when it will be needed.</summary>
    public void Prefetch(Playlist playlist, PlaylistEntry entry)
    {
        var options = Options;
        if (options.Mode == ReplayGainMode.Off || !options.AnalyseUntagged || entry.IsUrl || TagsOf(entry)?.ReplayGain is not null)
        {
            return;
        }
        _analysis.Request(AnalysisTarget.For(entry), AnalysisKinds.Loudness, AnalysisPriority.Next);
        if (options.Mode != ReplayGainMode.Track)
        {
            foreach (var other in AlbumEntries(playlist, entry))
            {
                _analysis.Request(AnalysisTarget.For(other), AnalysisKinds.Loudness, AnalysisPriority.Background);
            }
        }
    }

    public void Dispose()
    {
        _analysis.Completed -= OnAnalysisCompleted;
        _settings.Changed -= OnSettingsChanged;
    }

    internal static ReplayGainDecision Decide(double gainDb, double? peak, ReplayGainSource source, ReplayGainSettings options)
    {
        gainDb = Math.Clamp(gainDb, -30, 30);
        var linear = Math.Pow(10, gainDb / 20);
        if (options.PreventClipping && peak is > 0)
        {
            linear = Math.Min(linear, 1 / peak.Value);
        }
        return new ReplayGainDecision(gainDb, (float)linear, source);
    }

    private TrackTags? TagsOf(PlaylistEntry entry) => entry.IsVirtual ? _tags.Read(entry.Path) : entry.Tags?.ReplayGain is not null ? entry.Tags : _tags.Read(entry.Path);

    private (double GainDb, float Peak)? AlbumLoudness(Playlist playlist, PlaylistEntry entry)
    {
        var results = new List<LoudnessResult>();
        foreach (var e in AlbumEntries(playlist, entry).Prepend(entry))
        {
            var target = AnalysisTarget.For(e);
            if (_analysis.TryGetLoudness(target) is { } r)
            {
                results.Add(r);
            }
            else
            {
                _analysis.Request(target, AnalysisKinds.Loudness, AnalysisPriority.Next);
                return null; // until every track is analysed, the track gain is used
            }
        }
        return LoudnessResult.Album(results);
    }

    private static bool HasAlbumNeighbour(Playlist playlist, PlaylistEntry entry)
    {
        var entries = playlist.Entries;
        var i = entries.IndexOf(entry);
        return i >= 0 && ((i > 0 && SameAlbum(entries[i - 1], entry)) || (i + 1 < entries.Length && SameAlbum(entries[i + 1], entry)));
    }

    private static IEnumerable<PlaylistEntry> AlbumEntries(Playlist playlist, PlaylistEntry entry) =>
        playlist.Entries.Where(e => !ReferenceEquals(e, entry) && !e.IsUrl && SameAlbum(e, entry));

    /// <summary>Same album name, and the same album artist (or, without one, the same folder).</summary>
    internal static bool SameAlbum(PlaylistEntry a, PlaylistEntry b)
    {
        var (ta, tb) = (a.Tags, b.Tags);
        if (string.IsNullOrWhiteSpace(ta?.Album) || !string.Equals(ta.Album, tb?.Album, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        return ta.AlbumArtist is { } artist && tb!.AlbumArtist is not null
            ? string.Equals(artist, tb.AlbumArtist, StringComparison.OrdinalIgnoreCase)
            : string.Equals(Path.GetDirectoryName(a.Path), Path.GetDirectoryName(b.Path), StringComparison.OrdinalIgnoreCase);
    }

    private void OnAnalysisCompleted(object? sender, AnalysisCompletedEventArgs e)
    {
        if (!e.Kinds.HasFlag(AnalysisKinds.Loudness))
        {
            return;
        }
        if (Options.WriteTags && e.Target.Start is null && _analysis.TryGetLoudness(e.Target) is { } loudness)
        {
            WriteTags(e.Target, loudness);
        }
        GainChanged?.Invoke(this, new ReplayGainChangedEventArgs(e.Target.Path));
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Group == SettingsGroup.Playback)
        {
            GainChanged?.Invoke(this, new ReplayGainChangedEventArgs(null));
        }
    }

    /// <summary>Writes the analysed track gain and peak into the file's tags, unless it already has ReplayGain tags.</summary>
    private void WriteTags(AnalysisTarget target, LoudnessResult loudness)
    {
        try
        {
            var oldKey = FileKey.FromFile(target.Path);
            using (var file = TagLib.File.Create(target.Path))
            {
                if (!double.IsNaN(file.Tag.ReplayGainTrackGain))
                {
                    return;
                }
                file.Tag.ReplayGainTrackGain = Math.Round(loudness.GainDb, 2);
                file.Tag.ReplayGainTrackPeak = Math.Round(loudness.SamplePeak, 6);
                file.Save();
            }
            _analysis.Rekey(target, oldKey);
            _logger.LogInformation("Wrote ReplayGain {Gain:+0.00;-0.00} dB to {Path}", loudness.GainDb, target.Path);
        }
#pragma warning disable CA1031 // Tag writing is optional: a locked, read-only or unsupported file is skipped.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogInformation("Could not write ReplayGain tags to {Path}: {Error}", target.Path, ex.Message);
        }
    }
}
