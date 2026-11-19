using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using AudioTastic.Metadata;
using AudioTastic.Platform;
using AudioTastic.Sdk.Audio;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Ui.Playlist;

/// <summary>
/// PL-01 … PL-07: the playlist window's state and operations, independent of the skinned controls (which are rebuilt
/// on skin switches): the active tab, the search filter, the playing entry, totals, adding files, and the
/// ADD/REM/SEL/SORT/LIST, row and tab menus. UI thread only.
/// </summary>
public sealed class PlaylistController : IDisposable
{
    private readonly PlaylistService _service;
    private readonly PlaybackEngine _engine;
    private readonly TagLoader _loader;
    private readonly TagService _tags;
    private readonly IPlatformServices _platform;
    private readonly ILogger<PlaylistController> _logger;
    private readonly IEnumerable<IAudioSourceFactory> _factories;
    private readonly Analysis.AnalysisService? _analysis;
    private readonly Analysis.BeatInfoService? _beats;
    private readonly TagWriter? _writer;
    private readonly TagWriteService? _writes;
    private readonly FileInfoReader? _info;
    private readonly OnlineLookupService? _lookup;
    private readonly Data.Settings.SettingsStore? _settings;

    /// <summary>AN-01 (PW6): "Analysing 3/120" while the analysis queue works; null when idle.</summary>
    public string? AnalysisStatus => _analysis?.Status is { Total: > 0 } s ? $"Analysing {s.Done + 1}/{s.Total}" : null;
    private Core.Playlists.Playlist _attached;
    private string _search = string.Empty;
    private IReadOnlyList<PlaylistEntry> _visible = [];
    private PlaylistListView? _list;

    public PlaylistController(PlaylistService service, PlaybackEngine engine, TagLoader loader, TagService tags,
        IEnumerable<IAudioSourceFactory> factories, IPlatformServices platform, ILogger<PlaylistController> logger,
        Analysis.AnalysisService? analysis = null, Analysis.BeatInfoService? beats = null, TagWriter? writer = null, TagWriteService? writes = null, FileInfoReader? info = null,
        OnlineLookupService? lookup = null, Data.Settings.SettingsStore? settings = null)
    {
        _info = info;
        _lookup = lookup;
        _settings = settings;
        _beats = beats;
        _writer = writer;
        _writes = writes;
        if (writes is not null)
        {
            // MD-01: written files refresh their rows; edits waiting for the player are written once it lets go.
            writes.Written += (_, e) => Dispatcher.UIThread.Post(() => OnTagsWritten(e.Path, e.Tags));
            writes.Failed += (_, e) => Dispatcher.UIThread.Post(() => Message?.Invoke(this, e.Message));
            engine.TrackChanged += (_, _) => RetryTagWrites();
            engine.StateChanged += (_, _) => RetryTagWrites();
        }
        _analysis = analysis;
        if (analysis is not null)
        {
            analysis.StatusChanged += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(() => DetailsChanged?.Invoke(this, EventArgs.Empty));
        }
        _service = service;
        _engine = engine;
        _loader = loader;
        _tags = tags;
        _platform = platform;
        _logger = logger;
        _factories = factories; // PG-01: live view of the enabled input plugins
        _attached = service.Active;
        _attached.Changed += OnActiveChanged;
        service.ActiveChanged += (_, _) => Reattach();
        service.TabsChanged += (_, _) => TabsChanged?.Invoke(this, EventArgs.Empty);
        engine.TrackChanged += (_, _) => Dispatcher.UIThread.Post(() => PlayingChanged?.Invoke(this, EventArgs.Empty));
        engine.Queue.Changed += (_, _) => Dispatcher.UIThread.Post(() => DetailsChanged?.Invoke(this, EventArgs.Empty)); // PB-09 numbers
        engine.ResumePositions.Changed += (_, _) => Dispatcher.UIThread.Post(() => DetailsChanged?.Invoke(this, EventArgs.Empty)); // PB-07 marker
        loader.Updated += (_, playlists) => Dispatcher.UIThread.Post(() =>
        {
            foreach (var p in playlists)
            {
                p.NotifyDetailsChanged();
            }
        });
        Recompute();
        foreach (var playlist in service.Playlists)
        {
            loader.ProbePlaylist(playlist);
        }
    }

    public PlaylistService Service => _service;
    public Core.Playlists.Playlist Active => _service.Active;

    /// <summary>The active playlist's entries after the search filter (PL-05).</summary>
    public IReadOnlyList<PlaylistEntry> Visible => _visible;

    /// <summary>The entries shown changed (edit, tab switch, search).</summary>
    public event EventHandler? ViewChanged;

    /// <summary>Tags or durations of shown entries changed; repaint.</summary>
    public event EventHandler? DetailsChanged;

    public event EventHandler? PlayingChanged;
    public event EventHandler? TabsChanged;

    /// <summary>The list selection changed (PL-07 selection duration).</summary>
    public event EventHandler? SelectionChanged;

    internal void NotifySelectionChanged() => SelectionChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>Asks the search box to take keyboard focus (J).</summary>
    public event EventHandler? SearchRequested;

    public string Search
    {
        get => _search;
        set
        {
            value ??= string.Empty;
            if (value != _search)
            {
                _search = value;
                Recompute();
            }
        }
    }

    /// <summary>Selected entries of the list (in list order).</summary>
    public IReadOnlyList<PlaylistEntry> Selection => _list?.SelectedEntries ?? [];

    /// <summary>The entry to highlight as playing in the active tab, or null.</summary>
    public PlaylistEntry? PlayingEntry => _engine.PlayingPlaylist == Active ? _engine.CurrentEntry : null;

    /// <summary>True when <paramref name="playlist"/> is the one Next/Previous follow (PL-02).</summary>
    public bool IsPlayingPlaylist(Core.Playlists.Playlist playlist) =>
        _engine.PlayingPlaylist == playlist && _engine.State != PlaybackState.Stopped;

    public bool IsAudio(string path) => _factories.Any(f => f.CanOpen(path));

    internal void AttachList(PlaylistListView list) => _list = list;

    internal void DetachList(PlaylistListView list)
    {
        if (_list == list)
        {
            _list = null;
        }
    }

    public void FocusSearch() => SearchRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>Entries on screen need tags (lazy loading, MD-01).</summary>
    public void RequestTags(IEnumerable<PlaylistEntry> entries) => _loader.RequestVisible(Active, entries);

    public void Play(PlaylistEntry entry) => Run(() => _engine.PlayAsync(Active, entry));

    /// <summary>PB-09: queue positions (1-based) of <paramref name="entry"/>, for the row's "[1]" marker.</summary>
    public IReadOnlyList<int> QueuePositions(PlaylistEntry entry) => _engine.Queue.PositionsOf(entry);

    /// <summary>PB-07: the entry has a saved per-file resume position.</summary>
    public bool HasResumePosition(PlaylistEntry entry) => !entry.IsUrl && _engine.ResumePositions.HasPosition(entry.Path);

    /// <summary>PB-09 (Q, row menu, Shift+Enter in search): adds the selected entries to the play-next queue.</summary>
    public void QueueSelection() => Queue(Selection);

    public void Queue(IReadOnlyList<PlaylistEntry> entries)
    {
        if (entries.Count == 0)
        {
            return;
        }
        _engine.Queue.Enqueue(Active, entries);
        var count = _engine.Queue.Count;
        Message?.Invoke(this, entries.Count == 1
            ? $"Queued: {entries[0].DisplayName} (plays next; [n] in the playlist is its place in the queue, {count} queued)"
            : $"Queued {entries.Count} tracks (they play next, in order; {count} queued)");
    }

    /// <summary>The row menu's "Stop after this track" (the shell toggles it and confirms).</summary>
    public event EventHandler? StopAfterCurrentRequested;

    /// <summary>A short confirmation for the main window's title area.</summary>
    public event EventHandler<string>? Message;

    /// <summary>PB-09: removes the entries from the queue.</summary>
    public void Unqueue(IEnumerable<PlaylistEntry> entries)
    {
        foreach (var entry in entries)
        {
            _engine.Queue.Remove(entry);
        }
    }

    /// <summary>
    /// PL-04: adds files, folders and playlists to <paramref name="target"/> (default: the active tab) at
    /// <paramref name="index"/> (default: the end); with <paramref name="play"/> the first added entry starts.
    /// </summary>
    public async Task AddAsync(IEnumerable<string> paths, int? index = null, bool play = false, Core.Playlists.Playlist? target = null)
    {
        target ??= Active;
        try
        {
            var entries = await _service.ResolveAsync(paths, IsAudio, _tags.ReadEmbeddedCueSheet);
            if (entries.Count == 0)
            {
                return;
            }
            target.Add(entries, index);
            _loader.RequestVisible(target, entries.Take(50));
            _loader.ProbePlaylist(target);
            if (play)
            {
                await _engine.PlayAsync(target, entries[0]);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not add files");
        }
    }

    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>
    /// FM-10: adds a stream URL to the active tab. A link to a playlist (.m3u, .m3u8, .pls, .xspf) is downloaded and
    /// its entries (usually stream URLs) are added. Returns an error message, or null on success.
    /// </summary>
    public async Task<string?> AddUrlAsync(Uri url, bool play)
    {
        var path = url.AbsolutePath;
        if (_service.Formats.FirstOrDefault(f => f.Extensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)) is null)
        {
            await AddAsync([url.AbsoluteUri], play: play);
            return null;
        }
        var folder = Directory.CreateTempSubdirectory("audiotastic-url-");
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            if (bytes.Length > 1024 * 1024)
            {
                return "The playlist is larger than 1 MB.";
            }
            var file = System.IO.Path.Combine(folder.FullName, "stream" + System.IO.Path.GetExtension(path));
            await File.WriteAllBytesAsync(file, bytes);
            var before = Active.Entries.Length;
            await AddAsync([file], play: play);
            return Active.Entries.Length == before ? "The playlist contains no entries." : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _logger.LogWarning(ex, "Could not download {Url}", url);
            return ex.Message;
        }
        finally
        {
            folder.Delete(recursive: true);
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AudioTastic/0.4");
        return client;
    }

    /// <summary>PL-07: duration of the selection and of the whole playlist; Unknown when some lengths are not known.</summary>
    public (TimeSpan Selected, TimeSpan Total, bool Unknown, int Count) Totals()
    {
        var total = TimeSpan.Zero;
        var unknown = false;
        foreach (var e in Active.Entries)
        {
            if (e.Duration is { } d)
            {
                total += d;
            }
            else
            {
                unknown = true;
            }
        }
        var selected = Selection.Aggregate(TimeSpan.Zero, (sum, e) => sum + (e.Duration ?? TimeSpan.Zero));
        return (selected, total, unknown, Active.Count);
    }

    // ---- editing ------------------------------------------------------------------------------------

    public void RemoveSelected() => Active.Remove(Selection);
    public void CropToSelection() => Active.Crop(Selection);
    public void MoveSelection(int delta) => Active.MoveBy(Selection, delta);
    public void Undo() => Active.Undo();
    public void Redo() => Active.Redo();

    /// <summary>PL-06: sorts the selection when several entries are selected, otherwise the whole playlist.</summary>
    public void Sort(PlaylistSortKey key) => Active.Sort(key, selection: Selection.Count > 1 ? Selection : null);

    // ---- menus ------------------------------------------------------------------------------------------

    public void ShowAddMenu(Control source) => Open(source,
        Item("Add files…", () => _ = AddFilesAsync(source)),
        Item("Add folder…", () => _ = AddFolderAsync(source)),
        Item("Add URL…", null)); // FM-10 (P4)

    public void ShowRemoveMenu(Control source) => Open(source,
        Item("Remove selected (Del)", RemoveSelected, Selection.Count > 0),
        Item("Crop (keep selected)", CropToSelection, Selection.Count > 0),
        Item("Remove duplicates", () => Active.RemoveDuplicates()),
        Item("Remove dead entries", () => Active.RemoveDead()),
        new Separator(),
        Item("Remove all", () => Active.Clear(), Active.Count > 0));

    public void ShowSelectMenu(Control source) => Open(source,
        Item("Select all (Ctrl+A)", () => _list?.SelectAll()),
        Item("Select none", () => _list?.SelectNone()),
        Item("Invert selection", () => _list?.InvertSelection()));

    public void ShowSortMenu(Control source)
    {
        var scope = Selection.Count > 1 ? " (selection)" : string.Empty;
        Open(source,
            Item($"By title{scope}", () => Sort(PlaylistSortKey.Title)),
            Item($"By artist{scope}", () => Sort(PlaylistSortKey.Artist)),
            Item($"By album{scope}", () => Sort(PlaylistSortKey.Album)),
            Item($"By track number{scope}", () => Sort(PlaylistSortKey.TrackNumber)),
            Item($"By path{scope}", () => Sort(PlaylistSortKey.Path)),
            Item($"By file name{scope}", () => Sort(PlaylistSortKey.FileName)),
            Item($"By duration{scope}", () => Sort(PlaylistSortKey.Duration)),
            new Separator(),
            Item($"Reverse{scope}", () => Active.Reverse(Selection.Count > 1 ? Selection : null)),
            Item($"Randomise{scope}", () => Active.Randomise(selection: Selection.Count > 1 ? Selection : null)),
            new Separator(),
            Item("Undo (Ctrl+Z)", Undo, Active.CanUndo),
            Item("Redo (Ctrl+Y)", Redo, Active.CanRedo));
    }

    public void ShowListMenu(Control source) => Open(source,
        Item("New playlist", () => _service.New()),
        Item("Load playlist…", () => _ = LoadPlaylistAsync(source)),
        Item("Save playlist… (relative paths)", () => _ = SavePlaylistAsync(source, Active, relative: true)),
        Item("Save playlist… (absolute paths)", () => _ = SavePlaylistAsync(source, Active, relative: false)));

    /// <summary>Playlist context menu (Feature Specification 2.2); later-phase items are disabled.</summary>
    public void ShowRowMenu(Control source)
    {
        var selection = Selection;
        var first = selection.Count > 0 ? selection[0] : null;
        var queued = selection.Any(e => QueuePositions(e).Count > 0);
        var isPlaying = first is not null && ReferenceEquals(first, PlayingEntry);
        Open(source,
            Item("Play", () => Play(first!), first is not null),
            Item("Play next (queue)  Q", QueueSelection, selection.Count > 0),
            Item("Remove from queue", () => Unqueue(selection), queued),
            Item(_engine.StopAfterCurrent && isPlaying ? "✓ Stop after this track" : "Stop after this track",
                () => StopAfterCurrentRequested?.Invoke(this, EventArgs.Empty), isPlaying),
            Item("File info…  Alt+3", () => ShowFileInfo(source, current: false), _info is not null && first is not null),
            Item("Edit tags…  Ctrl+E", () => EditTags(source), _writes is not null && selection.Any(e => !e.IsUrl)),
            Item("Look up online…", () => EditTags(source, lookUp: true), LookupEnabled && selection.Count(e => !e.IsUrl) == 1),
            Item("Convert…", null),
            Item("Reset resume position", () =>
            {
                foreach (var e in selection.Where(e => !e.IsUrl))
                {
                    _engine.ResumePositions.ClearPosition(e.Path);
                }
            }, selection.Any(HasResumePosition)),
            Item("Show in folder", () => _platform.OpenFolder(Path.GetDirectoryName(first!.Path)!), first is { IsUrl: false }),
            Item("Adjust beat grid…", () => _ = AdjustBeatGridAsync(source, first!), _beats is not null && first is { IsUrl: false, Start: null }),
            new Separator(),
            Item("Remove", RemoveSelected, selection.Count > 0),
            Item("Crop", CropToSelection, selection.Count > 0));
    }

    public void ShowTabMenu(Control source, Core.Playlists.Playlist playlist, Action rename)
    {
        var index = _service.Playlists.ToList().IndexOf(playlist);
        Open(source,
            Item("Rename", rename),
            Item("Duplicate", () => _service.Duplicate(playlist)),
            Item("Save as…", () => _ = SavePlaylistAsync(source, playlist, relative: true)),
            Item("Move left", () => _service.Move(playlist, index - 1), index > 0),
            Item("Move right", () => _service.Move(playlist, index + 1), index < _service.Playlists.Count - 1),
            new Separator(),
            Item("Close", () => _service.Close(playlist)));
    }

    public void Dispose() => _attached.Changed -= OnActiveChanged;

    /// <summary>
    /// MD-01: Edit tags… (Ctrl+E) for the selected files, or the playing track when nothing is selected. Files whose
    /// tags cannot be written are skipped (and said so); the playing track is written once the player releases it.
    /// </summary>
    /// <summary>MD-03: online lookup is available (a service and Settings › Metadata allow it).</summary>
    private bool LookupEnabled => _lookup is not null && _settings?.Current.Metadata is { OnlineLookup: true } m && (m.LookupTags || m.LookupCovers);

    public void EditTags(Control source, bool lookUp = false)
    {
        if (_writer is null || _writes is null || TopLevel.GetTopLevel(source) is not Window owner)
        {
            return;
        }
        var entries = Selection.Where(e => !e.IsUrl).ToList();
        if (entries.Count == 0 && _engine.CurrentEntry is { IsUrl: false } current)
        {
            entries.Add(current);
        }
        var paths = entries.Select(e => e.Path).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (paths.Count == 0)
        {
            return;
        }
        var writer = _writer;
        var writes = _writes;
        Run(async () =>
        {
            var read = await Task.Run(() => paths.Select(p => (Path: p, Writable: TagWriter.CanWrite(p), Tags: writer.Read(p))).ToList());
            var writable = read.Where(r => r.Writable && r.Tags is not null).ToList();
            if (writable.Count == 0)
            {
                Message?.Invoke(this, paths.Count == 1 ? "The tags of this file cannot be written (read-only file or format)."
                    : "The tags of these files cannot be written (read-only files or formats).");
                return;
            }
            var heading = writable.Count == 1 ? Path.GetFileName(writable[0].Path) : $"{writable.Count} files";
            var skipped = paths.Count - writable.Count;
            var note = skipped > 0 ? $"{skipped} of {paths.Count} files cannot be written (read-only file or format) and are left out." : null;
            TagLookup? lookup = null;
            if (LookupEnabled && writable.Count == 1)
            {
                var m = _settings!.Current.Metadata;
                var entry = entries.First(e => string.Equals(e.Path, writable[0].Path, StringComparison.OrdinalIgnoreCase));
                lookup = new TagLookup(_lookup!, entry.Duration, entry.Path, m.LookupTags, m.LookupCovers, StartAtOnce: lookUp);
            }
            if (await TagEditorWindow.ShowAsync(owner, [.. writable.Select(r => r.Tags!)], heading, note, lookup) is not { } edit)
            {
                return;
            }
            var outcome = await writes.WriteAsync([.. writable.Select(r => r.Path)], edit);
            if (outcome.Failures.Count > 0)
            {
                return; // each failure was reported through Failed
            }
            Message?.Invoke(this, outcome.Deferred > 0
                ? "Tags saved; the playing track's tags are written when it stops or the next track starts"
                : outcome.Written == 1 ? "Tags saved" : $"Tags saved to {outcome.Written} files");
        });
    }

    /// <summary>
    /// MD-05: File info… (Alt+3) for the first selected entry, or (<paramref name="current"/>, the main window and the
    /// format badges) the playing track: file and audio details, loudness and tempo, and all tags.
    /// </summary>
    public void ShowFileInfo(Control source, bool current)
    {
        var selected = Selection is { Count: > 0 } selection ? selection[0] : null;
        var entry = current ? _engine.CurrentEntry ?? selected : selected ?? _engine.CurrentEntry;
        if (_info is null || entry is null || TopLevel.GetTopLevel(source) is not Window owner)
        {
            return;
        }
        var info = _info;
        Run(async () =>
        {
            var (details, tags) = await Task.Run(() => (info.Read(entry.Path), entry.IsUrl ? null : _tags.Read(entry.Path)));
            if (details is null)
            {
                Message?.Invoke(this, $"No information: {entry.DisplayName} cannot be opened.");
                return;
            }
            var file = details.File.ToList();
            if (entry is { Start: { } start })
            {
                file.Add(new InfoRow("CUE track", $"{FileInfoReader.Time(start)} – {(entry.End is { } end ? FileInfoReader.Time(end) : "end")}"));
            }
            var heading = entry.Tags?.DisplayName ?? Path.GetFileName(entry.Path);
            await new FileInfoWindow(heading,
            [
                ("File", file),
                ("Audio", details.Audio),
                ("Loudness and tempo", LoudnessAndTempo(entry, tags)),
                ("Tags", details.Tags),
            ]).ShowDialog(owner);
        });
    }

    /// <summary>MD-05: ReplayGain from the tags and from our own analysis (PB-03), and the beat analysis (AN-02).</summary>
    private List<InfoRow> LoudnessAndTempo(PlaylistEntry entry, Sdk.Metadata.TrackTags? tags)
    {
        static string Db(double value) => value.ToString("+0.00 dB;-0.00 dB;0.00 dB", System.Globalization.CultureInfo.InvariantCulture);
        static string Peak(double value) => value.ToString("0.000000", System.Globalization.CultureInfo.InvariantCulture);
        var rows = new List<InfoRow>();
        if (tags?.ReplayGain is { } rg)
        {
            if (rg.TrackGainDb is { } tg)
            {
                rows.Add(new("ReplayGain track", rg.TrackPeak is { } tp ? $"{Db(tg)}, peak {Peak(tp)}" : Db(tg)));
            }
            if (rg.AlbumGainDb is { } ag)
            {
                rows.Add(new("ReplayGain album", rg.AlbumPeak is { } ap ? $"{Db(ag)}, peak {Peak(ap)}" : Db(ag)));
            }
        }
        else if (!entry.IsUrl)
        {
            rows.Add(new("ReplayGain tags", "none"));
        }
        if (entry.IsUrl)
        {
            return rows;
        }
        if (_analysis?.TryGetLoudness(Analysis.AnalysisTarget.For(entry)) is { } loudness)
        {
            var lufs = double.IsFinite(loudness.IntegratedLufs) ? loudness.IntegratedLufs.ToString("0.0 'LUFS'", System.Globalization.CultureInfo.InvariantCulture) : "silent";
            var peakDb = loudness.SamplePeak > 0 ? (20 * Math.Log10(loudness.SamplePeak)).ToString("0.0 'dBFS'", System.Globalization.CultureInfo.InvariantCulture) : "silent";
            rows.Add(new("Loudness (analysed)", $"{lufs}, track gain {Db(loudness.GainDb)}, sample peak {peakDb}"));
        }
        else if (_analysis is not null)
        {
            rows.Add(new("Loudness (analysed)", "not analysed yet"));
        }
        if (_beats?.TryGet(entry) is { } beats)
        {
            var c = System.Globalization.CultureInfo.InvariantCulture;
            rows.Add(new("BPM (analysed)", $"{beats.Bpm.ToString("0.0", c)} (confidence {beats.Confidence.ToString("0.00", c)})"));
            rows.Add(new("Beat", $"{FileInfoReader.Time(TimeSpan.FromSeconds(beats.BeatStart))} – {FileInfoReader.Time(TimeSpan.FromSeconds(beats.BeatEnd))}"
                + (beats.Breaks.Count > 0 ? $", {beats.Breaks.Count} break{(beats.Breaks.Count == 1 ? "" : "s")}" : string.Empty)));
        }
        else if (_beats is not null)
        {
            rows.Add(new("BPM (analysed)", "not analysed yet"));
        }
        return rows;
    }

    private void RetryTagWrites()
    {
        if (_writes is { PendingCount: > 0 } writes)
        {
            _ = writes.Retry();
        }
    }

    /// <summary>MD-01: every row of the written file (in every tab) shows the new tags.</summary>
    private void OnTagsWritten(string path, Sdk.Metadata.TrackTags? tags)
    {
        foreach (var playlist in _service.Playlists)
        {
            var changed = false;
            foreach (var entry in playlist.Entries)
            {
                if (string.Equals(entry.Path, path, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                {
                    TagLoader.Refresh(entry, tags);
                    changed = true;
                }
            }
            if (changed)
            {
                playlist.NotifyDetailsChanged();
            }
        }
    }

    /// <summary>
    /// AN-02: moves a file's beat grid by a few milliseconds when the kick detection is a little off (beat matching
    /// then aligns on the corrected beats). Negative = earlier.
    /// </summary>
    private async Task AdjustBeatGridAsync(Control source, PlaylistEntry entry)
    {
        if (_beats is null || TopLevel.GetTopLevel(source) is not Window owner)
        {
            return;
        }
        var current = _beats.GetNudge(entry.Path);
        var text = await InputWindow.ShowAsync(owner, "Adjust beat grid",
            $"Move the beat grid of {entry.DisplayName} by (milliseconds, negative = earlier; 0 removes the adjustment):",
            current.ToString("0", System.Globalization.CultureInfo.InvariantCulture));
        if (text is not null && double.TryParse(text.Trim(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ms))
        {
            _beats.SetNudge(entry.Path, Math.Clamp(ms, -250, 250));
        }
    }

    // ---- dialogs --------------------------------------------------------------------------------------

    /// <summary>PG-01: file dialog filter with every extension of the enabled input plugins plus playlist formats.</summary>
    public FilePickerFileType OpenableFiles() => new("Audio files and playlists")
    {
        Patterns = _factories.SelectMany(f => f.Extensions).Concat(_service.Formats.SelectMany(f => f.Extensions))
            .Distinct(StringComparer.OrdinalIgnoreCase).Select(e => "*" + e).ToArray(),
    };

    private async Task AddFilesAsync(Control source)
    {
        if (TopLevel.GetTopLevel(source) is not { } top)
        {
            return;
        }
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add files",
            AllowMultiple = true,
            FileTypeFilter = [OpenableFiles(), FilePickerFileTypes.All],
        });
        await AddAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private async Task AddFolderAsync(Control source)
    {
        if (TopLevel.GetTopLevel(source) is not { } top)
        {
            return;
        }
        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Add folder", AllowMultiple = true });
        await AddAsync(folders.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    private async Task LoadPlaylistAsync(Control source)
    {
        if (TopLevel.GetTopLevel(source) is not { } top)
        {
            return;
        }
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Load playlist",
            AllowMultiple = true,
            FileTypeFilter = [PlaylistFileTypes(), FilePickerFileTypes.All],
        });
        foreach (var path in files.Select(f => f.TryGetLocalPath()).OfType<string>())
        {
            try
            {
                var tab = _service.ImportAsTab(path);
                _loader.ProbePlaylist(tab);
            }
            catch (Exception ex) when (ex is IOException or System.Xml.XmlException or NotSupportedException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not load playlist {Path}", path);
            }
        }
    }

    private async Task SavePlaylistAsync(Control source, Core.Playlists.Playlist playlist, bool relative)
    {
        if (TopLevel.GetTopLevel(source) is not { } top)
        {
            return;
        }
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = relative ? "Save playlist (relative paths)" : "Save playlist (absolute paths)",
            SuggestedFileName = playlist.Name + ".m3u8",
            DefaultExtension = "m3u8",
            FileTypeChoices = _service.Formats.Where(f => f.CanWrite)
                .Select(f => new FilePickerFileType(f.Name) { Patterns = f.Extensions.Select(e => "*" + e).ToArray() }).ToArray(),
        });
        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }
        try
        {
            _service.Export(playlist, path, relative);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Could not save playlist {Path}", path);
        }
    }

    private FilePickerFileType PlaylistFileTypes() =>
        new("Playlists") { Patterns = _service.Formats.SelectMany(f => f.Extensions).Select(e => "*" + e).ToArray() };

    // ---- helpers --------------------------------------------------------------------------------------

    private void Reattach()
    {
        _attached.Changed -= OnActiveChanged;
        _attached = _service.Active;
        _attached.Changed += OnActiveChanged;
        Recompute();
        TabsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnActiveChanged(object? sender, PlaylistChangedEventArgs e)
    {
        if (e.Change == PlaylistChange.Entries)
        {
            Recompute();
        }
        else if (e.Change == PlaylistChange.Details)
        {
            if (_search.Length > 0)
            {
                Recompute(); // tags may now match the search
            }
            DetailsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void Recompute()
    {
        var entries = Active.Entries;
        if (string.IsNullOrWhiteSpace(_search))
        {
            _visible = entries;
        }
        else
        {
            var filter = new SearchFilter(_search);
            _visible = entries.Where(filter.Matches).ToArray();
        }
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    private static MenuItem Item(string header, Action? action, bool enabled = true)
    {
        var item = new MenuItem { Header = header, IsEnabled = action is not null && enabled };
        if (action is not null)
        {
            item.Click += (_, _) => action();
        }
        return item;
    }

    private static void Open(Control source, params object[] items) => new ContextMenu { ItemsSource = items }.Open(source);

    private async void Run(Func<Task> command)
    {
        try
        {
            await command();
        }
        catch (ObjectDisposedException)
        {
        }
#pragma warning disable CA1031 // UI command: log instead of crashing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Playlist command failed");
        }
    }
}
