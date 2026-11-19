using Avalonia.Threading;
using AudioTastic.Core.Playback;
using AudioTastic.Data.Settings;
using AudioTastic.Metadata;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Ui;

/// <summary>
/// MD-04: the playing track's lyrics for the Art &amp; Lyrics window. Found (<see cref="LyricsService"/>) off the UI
/// thread when the track changes, when its tags are written and when Settings › Metadata › Lyrics changes; the current
/// line follows the playback position (10 Hz). Events are raised on the UI thread.
/// </summary>
public sealed class LyricsCoordinator : IDisposable
{
    private readonly PlaybackEngine _engine;
    private readonly LyricsService _service;
    private readonly SettingsStore _settings;
    private readonly TagWriteService? _writes;
    private readonly ILogger<LyricsCoordinator> _logger;
    private int _version;
    private string? _path;
    private TimeSpan _start;
    private bool _on;

    public LyricsCoordinator(PlaybackEngine engine, LyricsService service, SettingsStore settings, ILogger<LyricsCoordinator> logger, TagWriteService? writes = null)
    {
        _engine = engine;
        _service = service;
        _settings = settings;
        _writes = writes;
        _logger = logger;
        _on = settings.Current.Metadata.Lyrics;
        engine.TrackChanged += OnTrackChanged;
        engine.PositionChanged += OnPositionChanged;
        settings.Changed += OnSettingsChanged;
        if (writes is not null)
        {
            writes.Written += OnTagsWritten;
        }
        Reload();
    }

    /// <summary>The lyrics, or null (none found, a stream, or lyrics off).</summary>
    public Lyrics? Current { get; private set; }

    /// <summary>Settings › Metadata › Lyrics: when off, the pane is hidden.</summary>
    public bool Enabled => _on;

    /// <summary>The synced line being sung, or -1.</summary>
    public int Line { get; private set; } = -1;

    /// <summary>Raised on the UI thread when <see cref="Current"/> or <see cref="Enabled"/> changes.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised on the UI thread when <see cref="Line"/> changes.</summary>
    public event EventHandler? LineChanged;

    /// <summary>Seeks to a synced line (clicking it).</summary>
    public void Seek(LyricLine line)
    {
        ArgumentNullException.ThrowIfNull(line);
        if (line.Time is { } time && time >= _start)
        {
            _ = _engine.SeekAsync(time - _start);
        }
    }

    public void Reload()
    {
        var version = Interlocked.Increment(ref _version);
        var entry = _engine.CurrentEntry;
        var path = _engine.CurrentTrack?.Path;
        _path = path;
        _start = entry?.Start ?? TimeSpan.Zero; // FM-09: a CUE track's lyrics times are in the whole file
        if (path is null || !_on)
        {
            Dispatcher.UIThread.Post(() => Apply(version, null));
            return;
        }
        _ = Task.Run(() =>
        {
            Lyrics? lyrics = null;
            try
            {
                lyrics = _service.Find(path);
            }
#pragma warning disable CA1031 // Lyrics are optional.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _logger.LogDebug(ex, "Lyrics of {Path} could not be read", path);
            }
            Dispatcher.UIThread.Post(() => Apply(version, lyrics));
        });
    }

    public void Dispose()
    {
        _engine.TrackChanged -= OnTrackChanged;
        _engine.PositionChanged -= OnPositionChanged;
        _settings.Changed -= OnSettingsChanged;
        if (_writes is not null)
        {
            _writes.Written -= OnTagsWritten;
        }
    }

    private void Apply(int version, Lyrics? lyrics)
    {
        if (version != Volatile.Read(ref _version))
        {
            return;
        }
        Current = lyrics;
        Line = lyrics?.LineAt(_engine.Position + _start) ?? -1;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void OnPositionChanged(object? sender, PlaybackPositionEventArgs e)
    {
        if (Current is not { Synced: true } lyrics)
        {
            return;
        }
        var line = lyrics.LineAt(e.Position + _start);
        if (line != Line)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (ReferenceEquals(lyrics, Current) && line != Line)
                {
                    Line = line;
                    LineChanged?.Invoke(this, EventArgs.Empty);
                }
            });
        }
    }

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e) => Reload();

    private void OnTagsWritten(object? sender, TagWrittenEventArgs e)
    {
        if (string.Equals(e.Path, _path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            Reload();
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Group == SettingsGroup.Metadata && e.Settings.Metadata.Lyrics != _on)
        {
            _on = e.Settings.Metadata.Lyrics;
            Reload();
        }
    }
}
