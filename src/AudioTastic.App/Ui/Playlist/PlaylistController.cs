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
    private readonly IReadOnlyList<IAudioSourceFactory> _factories;
    private Core.Playlists.Playlist _attached;
    private string _search = string.Empty;
    private IReadOnlyList<PlaylistEntry> _visible = [];
    private PlaylistListView? _list;

    public PlaylistController(PlaylistService service, PlaybackEngine engine, TagLoader loader, TagService tags,
        IEnumerable<IAudioSourceFactory> factories, IPlatformServices platform, ILogger<PlaylistController> logger)
    {
        _service = service;
        _engine = engine;
        _loader = loader;
        _tags = tags;
        _platform = platform;
        _logger = logger;
        _factories = [.. factories];
        _attached = service.Active;
        _attached.Changed += OnActiveChanged;
        service.ActiveChanged += (_, _) => Reattach();
        service.TabsChanged += (_, _) => TabsChanged?.Invoke(this, EventArgs.Empty);
        engine.TrackChanged += (_, _) => Dispatcher.UIThread.Post(() => PlayingChanged?.Invoke(this, EventArgs.Empty));
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
        Open(source,
            Item("Play", () => Play(first!), first is not null),
            Item("Play next (queue)", null),
            Item("Stop after this track", null),
            Item("File info…", null),
            Item("Edit tags…", null),
            Item("Look up online…", null),
            Item("Convert…", null),
            Item("Reset resume position", null),
            Item("Show in folder", () => _platform.OpenFolder(Path.GetDirectoryName(first!.Path)!), first is { IsUrl: false }),
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

    // ---- dialogs --------------------------------------------------------------------------------------

    private async Task AddFilesAsync(Control source)
    {
        if (TopLevel.GetTopLevel(source) is not { } top)
        {
            return;
        }
        var extensions = _factories.SelectMany(f => f.Extensions).Concat(_service.Formats.SelectMany(f => f.Extensions)).Distinct().Select(e => "*" + e).ToArray();
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add files",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Audio files and playlists") { Patterns = extensions }, FilePickerFileTypes.All],
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
