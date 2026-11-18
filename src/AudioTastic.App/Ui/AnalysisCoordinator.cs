using AudioTastic.Analysis;
using AudioTastic.Core.Playback;
using AudioTastic.Data.Settings;

namespace AudioTastic.App.Ui;

/// <summary>
/// CO-07 / AN-01: connects playback to the analysis queue. When a track starts, its waveform is taken from the cache
/// or analysed first in line (progress reported while it runs); the next entry's waveform and loudness are queued
/// behind it, so they are ready when it starts. Streams get the live waveform instead.
/// <see cref="Changed"/> is raised on background threads.
/// </summary>
public sealed class AnalysisCoordinator : IDisposable
{
    private readonly PlaybackEngine _engine;
    private readonly AnalysisService _analysis;
    private readonly ReplayGainService _replayGain;
    private readonly SettingsStore? _settings;
    private readonly Lock _lock = new();
    private AnalysisTarget? _target;

    public AnalysisCoordinator(PlaybackEngine engine, AnalysisService analysis, ReplayGainService replayGain, SettingsStore? settings = null)
    {
        _settings = settings;
        _engine = engine;
        _analysis = analysis;
        _replayGain = replayGain;
        engine.TrackChanged += OnTrackChanged;
        analysis.Completed += OnCompleted;
        analysis.Progress += OnProgress;
    }

    /// <summary>The playing track's waveform, once known.</summary>
    public Waveform? Waveform { get; private set; }

    /// <summary>0–1 while the playing track's waveform is being made; null otherwise.</summary>
    public double? Progress { get; private set; }

    /// <summary>True for live streams (scrolling waveform from <see cref="PlaybackEngine.Peaks"/>).</summary>
    public bool IsLive { get; private set; }

    /// <summary>Waveform, progress or live state changed.</summary>
    public event EventHandler? Changed;

    public void Dispose()
    {
        _engine.TrackChanged -= OnTrackChanged;
        _analysis.Completed -= OnCompleted;
        _analysis.Progress -= OnProgress;
    }

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e)
    {
        if (e.Track is null || e.Entry is not { } entry)
        {
            return;
        }
        var target = AnalysisTarget.For(entry);
        lock (_lock)
        {
            if (_target == target && (Waveform is not null || Progress is not null || IsLive))
            {
                return; // a stream title update or the same track again
            }
            _target = target;
            IsLive = entry.IsUrl;
            Waveform = null;
            Progress = null;
        }
        if (target.IsAnalysable)
        {
            if (_analysis.TryGetWaveform(target) is { } cached)
            {
                Set(target, cached, null);
            }
            else
            {
                Set(target, null, 0);
                _analysis.Request(target, AnalysisKinds.Waveform, AnalysisPriority.Current);
            }
        }
        else
        {
            Changed?.Invoke(this, EventArgs.Empty);
        }
        Prefetch(e.Index);
    }

    private void Prefetch(int index)
    {
        var playlist = _engine.PlayingPlaylist;
        var entries = playlist.Entries;
        // AN-02: with crossfading on, the beats of the current and next track are needed for beat matching, and with
        // "analyse in background" those of the following tracks too (up to 200 ahead).
        var xf = _settings?.Current.Crossfading;
        var beats = xf is { Enabled: true } ? AnalysisKinds.Beats : AnalysisKinds.None;
        if (beats != AnalysisKinds.None && index >= 0 && index < entries.Length && entries[index] is { IsUrl: false } current)
        {
            _analysis.Request(AnalysisTarget.For(current), beats, AnalysisPriority.Next); // below normal: keep the UI smooth
        }
        if (index + 1 < entries.Length && entries[index + 1] is { IsUrl: false } next)
        {
            _analysis.Request(AnalysisTarget.For(next), AnalysisKinds.Waveform | beats, AnalysisPriority.Next);
            _replayGain.Prefetch(playlist, next);
        }
        if (xf is { Enabled: true, AnalyseInBackground: true })
        {
            for (var i = index + 2; i < Math.Min(entries.Length, index + 200); i++)
            {
                if (entries[i] is { IsUrl: false } later)
                {
                    _analysis.Request(AnalysisTarget.For(later), AnalysisKinds.Beats, AnalysisPriority.Background);
                }
            }
        }
    }

    private void OnProgress(object? sender, AnalysisProgressEventArgs e)
    {
        if (e.Target == _target && Waveform is null)
        {
            Set(e.Target, null, e.Fraction);
        }
    }

    private void OnCompleted(object? sender, AnalysisCompletedEventArgs e)
    {
        if (e.Target != _target || !e.Kinds.HasFlag(AnalysisKinds.Waveform) && e.Error is null)
        {
            return;
        }
        Set(e.Target, _analysis.TryGetWaveform(e.Target), null);
    }

    private void Set(AnalysisTarget target, Waveform? waveform, double? progress)
    {
        lock (_lock)
        {
            if (target != _target)
            {
                return;
            }
            Waveform = waveform;
            Progress = progress;
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
