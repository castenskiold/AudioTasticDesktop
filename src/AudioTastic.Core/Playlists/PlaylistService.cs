using System.Text.Json;
using AudioTastic.Core.Playlists.Formats;
using AudioTastic.Data;
using AudioTastic.Sdk.Playlists;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Core.Playlists;

/// <summary>
/// PL-01 / PL-02 / PL-03: the open playlists (tabs), which one is shown, import/export, and auto-save: each tab is
/// saved as playlists/&lt;id&gt;.m3u8 (with #EXTINF titles, so reopening needs no tag reading) plus
/// playlists/playlists.json (tab order, names, active tab, scroll position). Saving happens 1 s after the last change
/// and on <see cref="Flush"/>. Use on the UI thread, except <see cref="ResolveAsync"/>.
/// </summary>
public sealed class PlaylistService : IDisposable
{
    public const string StateFileName = "playlists.json";
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private readonly AppPaths _paths;
    private readonly ILogger<PlaylistService> _logger;
    private readonly List<Playlist> _playlists = [];
    private readonly HashSet<Guid> _dirty = [];
    private readonly Lock _saveLock = new();
    private readonly Timer _saveTimer;
    private Playlist _active;
    private bool _stateDirty;

    public PlaylistService(AppPaths paths, ILogger<PlaylistService> logger, IEnumerable<IPlaylistFormat>? formats = null)
    {
        _paths = paths;
        _logger = logger;
        _pluginFormats = formats ?? [];
        _saveTimer = new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        _active = Track(new Playlist("Main"));
    }

    /// <summary>The built-in formats followed by those of enabled input plugins (PG-01; read live).</summary>
    public IReadOnlyList<IPlaylistFormat> Formats => [.. PlaylistFormats.BuiltIn, .. _pluginFormats];

    private readonly IEnumerable<IPlaylistFormat> _pluginFormats;
    public IReadOnlyList<Playlist> Playlists => _playlists;

    /// <summary>The tab shown in the playlist window.</summary>
    public Playlist Active
    {
        get => _active;
        set
        {
            if (value != _active && _playlists.Contains(value))
            {
                _active = value;
                MarkStateDirty();
                ActiveChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Tabs were added, closed, renamed or reordered.</summary>
    public event EventHandler? TabsChanged;
    public event EventHandler? ActiveChanged;

    /// <summary>Loads the saved tabs; starts with one empty "Main" tab when there are none.</summary>
    public void Load()
    {
        var statePath = Path.Combine(_paths.PlaylistsFolder, StateFileName);
        if (!File.Exists(statePath))
        {
            return;
        }
        try
        {
            var state = JsonSerializer.Deserialize<SavedState>(File.ReadAllText(statePath), JsonOptions) ?? new SavedState();
            var loaded = new List<Playlist>();
            foreach (var tab in state.Tabs)
            {
                var file = Path.Combine(_paths.PlaylistsFolder, $"{tab.Id:N}.m3u8");
                var items = File.Exists(file) ? new M3uFormat().Read(file) : [];
                loaded.Add(new Playlist(tab.Name, items.Select(PlaylistEntry.FromItem), tab.Id) { ScrollIndex = tab.Scroll });
            }
            if (loaded.Count == 0)
            {
                return;
            }
            foreach (var p in _playlists)
            {
                p.Changed -= OnPlaylistChanged;
            }
            lock (_dirty)
            {
                _playlists.Clear();
            }
            foreach (var p in loaded)
            {
                Track(p);
            }
            _active = loaded.FirstOrDefault(p => p.Id == state.Active) ?? loaded[0];
            _logger.LogInformation("Loaded {Count} playlist tab(s) with {Entries} entries", loaded.Count, loaded.Sum(p => p.Count));
            TabsChanged?.Invoke(this, EventArgs.Empty);
            ActiveChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not load the saved playlists; starting with an empty one");
        }
    }

    public Playlist New(string? name = null, IEnumerable<PlaylistEntry>? entries = null, bool activate = true)
    {
        var playlist = Track(new Playlist(name ?? NextName(), entries));
        MarkDirty(playlist);
        MarkStateDirty();
        TabsChanged?.Invoke(this, EventArgs.Empty);
        if (activate)
        {
            Active = playlist;
        }
        return playlist;
    }

    public Playlist Duplicate(Playlist playlist) =>
        New($"{playlist.Name} (copy)", playlist.Entries.Select(e => e.Clone()));

    /// <summary>Closes a tab; the last tab is emptied instead of closed.</summary>
    public void Close(Playlist playlist)
    {
        if (_playlists.Count == 1)
        {
            playlist.Clear();
            return;
        }
        var index = _playlists.IndexOf(playlist);
        if (index < 0)
        {
            return;
        }
        playlist.Changed -= OnPlaylistChanged;
        lock (_dirty)
        {
            _playlists.RemoveAt(index);
        }
        MarkStateDirty();
        TabsChanged?.Invoke(this, EventArgs.Empty);
        if (_active == playlist)
        {
            Active = _playlists[Math.Min(index, _playlists.Count - 1)];
        }
    }

    public void Move(Playlist playlist, int index)
    {
        bool removed;
        lock (_dirty)
        {
            removed = _playlists.Remove(playlist);
            if (removed)
            {
                _playlists.Insert(Math.Clamp(index, 0, _playlists.Count), playlist);
            }
        }
        if (removed)
        {
            MarkStateDirty();
            TabsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public bool IsPlaylistFile(string path) => PlaylistFormats.For(path, Formats) is not null;

    /// <summary>Reads a playlist file into entries (PL-03).</summary>
    public IReadOnlyList<PlaylistEntry> Import(string file)
    {
        var format = PlaylistFormats.For(file, Formats) ?? throw new NotSupportedException($"Unknown playlist format '{Path.GetExtension(file)}'.");
        return format.Read(file).Select(PlaylistEntry.FromItem).ToArray();
    }

    /// <summary>Opens a playlist file as a new tab (LIST › Load, or opening the file from the OS).</summary>
    public Playlist ImportAsTab(string file) => New(Path.GetFileNameWithoutExtension(file), Import(file));

    /// <summary>Writes <paramref name="playlist"/> in the format chosen by the file extension (PL-03).</summary>
    public void Export(Playlist playlist, string file, bool relativePaths)
    {
        var format = PlaylistFormats.For(file, Formats.Where(f => f.CanWrite))
            ?? throw new NotSupportedException($"Cannot write '{Path.GetExtension(file)}' playlists.");
        format.Write(file, playlist.Entries.Select(e => e.ToItem()).ToArray(), relativePaths);
    }

    /// <summary>
    /// PL-04: turns dropped/opened paths into entries, on a background thread. Folders are scanned recursively for
    /// audio files (natural order); playlist and .cue files are expanded; audio files with an embedded CUESHEET
    /// (from <paramref name="embeddedCueSheet"/>) become one virtual track per cue track (FM-09).
    /// </summary>
    public Task<IReadOnlyList<PlaylistEntry>> ResolveAsync(IEnumerable<string> paths, Func<string, bool> isAudio,
        Func<string, string?>? embeddedCueSheet = null, CancellationToken cancellationToken = default)
    {
        var list = paths.ToArray();
        return Task.Run<IReadOnlyList<PlaylistEntry>>(() =>
        {
            var result = new List<PlaylistEntry>();
            foreach (var path in list)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Directory.Exists(path))
                {
                    var files = Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                        .Where(isAudio)
                        .Order(Comparer<string>.Create(PlaylistSorting.NaturalCompare));
                    foreach (var file in files)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        result.AddRange(AudioFile(file));
                    }
                }
                else if (IsPlaylistFile(path))
                {
                    try
                    {
                        result.AddRange(Import(path));
                    }
                    catch (Exception ex) when (ex is IOException or System.Xml.XmlException or UnauthorizedAccessException)
                    {
                        _logger.LogWarning(ex, "Could not read playlist {Path}", path);
                    }
                }
                else
                {
                    result.AddRange(AudioFile(path));
                }
            }
            return result;
        }, cancellationToken);

        IEnumerable<PlaylistEntry> AudioFile(string file)
        {
            if (embeddedCueSheet?.Invoke(file) is { Length: > 0 } cue)
            {
                var items = CueParser.ToItems(CueParser.Parse(cue), PlaylistText.FolderOf(file), audioFile: file);
                if (items.Count > 0)
                {
                    return items.Select(PlaylistEntry.FromItem);
                }
            }
            return [new PlaylistEntry(file)];
        }
    }

    /// <summary>Saves pending changes now (also called on exit).</summary>
    public void Flush()
    {
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(_paths.PlaylistsFolder);
                Guid[] dirty;
                Playlist[] tabs;
                lock (_dirty)
                {
                    dirty = [.. _dirty];
                    _dirty.Clear();
                    tabs = [.. _playlists];
                }
                var m3u = new M3uFormat();
                foreach (var playlist in tabs.Where(p => dirty.Contains(p.Id)))
                {
                    var file = Path.Combine(_paths.PlaylistsFolder, $"{playlist.Id:N}.m3u8");
                    var temp = file + ".tmp";
                    m3u.Write(temp, playlist.Entries.Select(e => e.ToItem()).ToArray(), relativePaths: false);
                    File.Move(temp, file, overwrite: true);
                }
                if (_stateDirty || dirty.Length > 0)
                {
                    _stateDirty = false;
                    var state = new SavedState
                    {
                        Active = _active.Id,
                        Tabs = [.. tabs.Select(p => new SavedTab { Id = p.Id, Name = p.Name, Scroll = p.ScrollIndex })],
                    };
                    var statePath = Path.Combine(_paths.PlaylistsFolder, StateFileName);
                    File.WriteAllText(statePath + ".tmp", JsonSerializer.Serialize(state, JsonOptions));
                    File.Move(statePath + ".tmp", statePath, overwrite: true);
                    DeleteClosedTabFiles(tabs);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Could not save the playlists");
            }
        }
    }

    /// <summary>Remembers the scroll position of a tab (saved with the state).</summary>
    public void SetScroll(Playlist playlist, int index)
    {
        if (playlist.ScrollIndex != index)
        {
            playlist.ScrollIndex = index;
            MarkStateDirty();
        }
    }

    public void Dispose()
    {
        _saveTimer.Dispose();
        Flush();
    }

    private Playlist Track(Playlist playlist)
    {
        lock (_dirty)
        {
            _playlists.Add(playlist);
        }
        playlist.Changed += OnPlaylistChanged;
        return playlist;
    }

    private void OnPlaylistChanged(object? sender, PlaylistChangedEventArgs e)
    {
        if (sender is not Playlist playlist)
        {
            return;
        }
        if (e.Change == PlaylistChange.Name)
        {
            MarkStateDirty();
            TabsChanged?.Invoke(this, EventArgs.Empty);
        }
        else if (e.Change == PlaylistChange.Entries)
        {
            MarkDirty(playlist);
        }
    }

    private void MarkDirty(Playlist playlist)
    {
        lock (_dirty)
        {
            _dirty.Add(playlist.Id);
        }
        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private void MarkStateDirty()
    {
        _stateDirty = true;
        _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
    }

    private void DeleteClosedTabFiles(Playlist[] tabs)
    {
        var keep = tabs.Select(p => $"{p.Id:N}.m3u8").ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(_paths.PlaylistsFolder, "*.m3u8"))
        {
            var name = Path.GetFileName(file);
            if (!keep.Contains(name) && Guid.TryParseExact(Path.GetFileNameWithoutExtension(name), "N", out _))
            {
                File.Delete(file);
            }
        }
    }

    private string NextName()
    {
        for (var n = _playlists.Count + 1; ; n++)
        {
            var name = $"Playlist {n}";
            if (_playlists.All(p => p.Name != name))
            {
                return name;
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private sealed class SavedState
    {
        public int Format { get; set; } = 1;
        public Guid Active { get; set; }
        public List<SavedTab> Tabs { get; set; } = [];
    }

    private sealed class SavedTab
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public int Scroll { get; set; }
    }
}
