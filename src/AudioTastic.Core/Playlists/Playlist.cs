using System.Collections.Immutable;

namespace AudioTastic.Core.Playlists;

/// <summary>What changed in a playlist.</summary>
public enum PlaylistChange
{
    /// <summary>Entries were added, removed or reordered (a new snapshot).</summary>
    Entries,

    /// <summary>Tags or missing state of existing entries changed (same snapshot).</summary>
    Details,

    Name,
}

public sealed class PlaylistChangedEventArgs(PlaylistChange change) : EventArgs
{
    public PlaylistChange Change { get; } = change;
}

/// <summary>PL-06 sort keys.</summary>
public enum PlaylistSortKey
{
    Title,
    Artist,
    Album,
    TrackNumber,
    Path,
    FileName,
    Duration,
}

/// <summary>
/// PL-01: one playlist (tab). Entries are an immutable snapshot replaced on every edit, so the playback engine can
/// read them from its own thread while the UI edits; undo/redo keep earlier snapshots. Edit on the UI thread.
/// </summary>
public sealed class Playlist
{
    private const int UndoLimit = 100;
    private readonly Stack<ImmutableArray<PlaylistEntry>> _undo = new();
    private readonly Stack<ImmutableArray<PlaylistEntry>> _redo = new();
    private ImmutableArray<PlaylistEntry> _entries;
    private string _name;
    private int _version;

    public Playlist(string name, IEnumerable<PlaylistEntry>? entries = null, Guid? id = null)
    {
        _name = name;
        _entries = entries is null ? [] : [.. entries];
        Id = id ?? Guid.NewGuid();
    }

    public Guid Id { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (value != _name)
            {
                _name = value;
                Changed?.Invoke(this, new PlaylistChangedEventArgs(PlaylistChange.Name));
            }
        }
    }

    /// <summary>Current snapshot (safe to read from any thread).</summary>
    public ImmutableArray<PlaylistEntry> Entries => _entries.IsDefault ? [] : _entries; // one reference: reads are atomic

    /// <summary>Increases on every change of <see cref="Entries"/>.</summary>
    public int Version => Volatile.Read(ref _version);

    public int Count => Entries.Length;

    /// <summary>Saved scroll position of the tab (PL-02).</summary>
    public int ScrollIndex { get; set; }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public event EventHandler<PlaylistChangedEventArgs>? Changed;

    public int IndexOf(PlaylistEntry entry) => Entries.IndexOf(entry);

    /// <summary>Inserts <paramref name="entries"/> at <paramref name="index"/> (default: the end). Returns the insert position.</summary>
    public int Add(IEnumerable<PlaylistEntry> entries, int? index = null)
    {
        var list = entries.ToArray();
        var current = Entries;
        var at = Math.Clamp(index ?? current.Length, 0, current.Length);
        if (list.Length > 0)
        {
            Apply(current.InsertRange(at, list));
        }
        return at;
    }

    public void Remove(IEnumerable<PlaylistEntry> entries)
    {
        var set = entries.ToHashSet(ReferenceEqualityComparer.Instance);
        if (set.Count > 0)
        {
            Apply([.. Entries.Where(e => !set.Contains(e))]);
        }
    }

    /// <summary>Keeps only <paramref name="keep"/> (PL-01 crop).</summary>
    public void Crop(IEnumerable<PlaylistEntry> keep)
    {
        var set = keep.ToHashSet(ReferenceEqualityComparer.Instance);
        Apply([.. Entries.Where(set.Contains)]);
    }

    public void Clear() => Apply([]);

    /// <summary>Moves the selected entries one place up (-1) or down (+1), as a block (Alt+arrows).</summary>
    public void MoveBy(IEnumerable<PlaylistEntry> selection, int delta)
    {
        var set = selection.ToHashSet(ReferenceEqualityComparer.Instance);
        var list = Entries.ToList();
        if (set.Count == 0 || delta == 0)
        {
            return;
        }
        var indexes = Enumerable.Range(0, list.Count).Where(i => set.Contains(list[i])).ToList();
        if ((delta < 0 && indexes[0] == 0) || (delta > 0 && indexes[^1] == list.Count - 1))
        {
            return;
        }
        foreach (var i in delta < 0 ? indexes : Enumerable.Reverse(indexes))
        {
            (list[i], list[i + delta]) = (list[i + delta], list[i]);
        }
        Apply([.. list]);
    }

    /// <summary>Moves the selected entries so they start at <paramref name="index"/> (drag to reorder, PL-04).</summary>
    public void MoveTo(IEnumerable<PlaylistEntry> selection, int index)
    {
        var current = Entries;
        var set = selection.ToHashSet(ReferenceEqualityComparer.Instance);
        var moving = current.Where(set.Contains).ToArray();
        if (moving.Length == 0)
        {
            return;
        }
        // The insert position counts only entries that stay.
        var before = current.Take(Math.Clamp(index, 0, current.Length)).Count(e => !set.Contains(e));
        var rest = current.Where(e => !set.Contains(e)).ToList();
        rest.InsertRange(before, moving);
        Apply([.. rest]);
    }

    /// <summary>PL-06: sorts everything, or only the positions held by <paramref name="selection"/>.</summary>
    public void Sort(PlaylistSortKey key, bool descending = false, IEnumerable<PlaylistEntry>? selection = null)
    {
        var comparer = PlaylistSorting.Comparer(key);
        Reorder(selection, items => descending ? items.OrderByDescending(e => e, comparer) : items.OrderBy(e => e, comparer));
    }

    public void Reverse(IEnumerable<PlaylistEntry>? selection = null) => Reorder(selection, items => items.Reverse());

    public void Randomise(Random? random = null, IEnumerable<PlaylistEntry>? selection = null)
    {
        var r = random ?? Random.Shared;
        Reorder(selection, items =>
        {
            var array = items.ToArray();
            r.Shuffle(array);
            return array;
        });
    }

    /// <summary>PL-06: removes later entries with the same file and CUE range. Returns how many were removed.</summary>
    public int RemoveDuplicates()
    {
        var kept = new List<PlaylistEntry>();
        var seen = new HashSet<(string, TimeSpan?, TimeSpan?)>();
        foreach (var e in Entries)
        {
            var key = (OperatingSystem.IsWindows() ? e.Path.ToUpperInvariant() : e.Path, e.Start, e.End);
            if (seen.Add(key))
            {
                kept.Add(e);
            }
        }
        var removed = Count - kept.Count;
        if (removed > 0)
        {
            Apply([.. kept]);
        }
        return removed;
    }

    /// <summary>PL-06: removes entries whose file no longer exists (URLs are kept). Returns how many were removed.</summary>
    public int RemoveDead(Func<string, bool>? exists = null)
    {
        exists ??= File.Exists;
        var kept = Entries.Where(e => e.IsUrl || exists(e.Path)).ToArray();
        var removed = Count - kept.Length;
        if (removed > 0)
        {
            Apply([.. kept]);
        }
        return removed;
    }

    public void Undo()
    {
        if (_undo.Count == 0)
        {
            return;
        }
        _redo.Push(Entries);
        Set(_undo.Pop());
    }

    public void Redo()
    {
        if (_redo.Count == 0)
        {
            return;
        }
        _undo.Push(Entries);
        Set(_redo.Pop());
    }

    /// <summary>Tags or missing flags of entries changed (no new snapshot).</summary>
    public void NotifyDetailsChanged() => Changed?.Invoke(this, new PlaylistChangedEventArgs(PlaylistChange.Details));

    /// <summary>Replaces the entries without an undo step (loading).</summary>
    public void Reset(IEnumerable<PlaylistEntry> entries)
    {
        _undo.Clear();
        _redo.Clear();
        Set([.. entries]);
    }

    private void Reorder(IEnumerable<PlaylistEntry>? selection, Func<IEnumerable<PlaylistEntry>, IEnumerable<PlaylistEntry>> order)
    {
        var current = Entries;
        if (selection is null)
        {
            Apply([.. order(current)]);
            return;
        }
        var set = selection.ToHashSet(ReferenceEqualityComparer.Instance);
        var slots = Enumerable.Range(0, current.Length).Where(i => set.Contains(current[i])).ToArray();
        var ordered = order(slots.Select(i => current[i])).ToArray();
        var builder = current.ToBuilder();
        for (var i = 0; i < slots.Length; i++)
        {
            builder[slots[i]] = ordered[i];
        }
        Apply(builder.ToImmutable());
    }

    private void Apply(ImmutableArray<PlaylistEntry> entries)
    {
        _undo.Push(Entries);
        if (_undo.Count > UndoLimit)
        {
            var keep = _undo.Take(UndoLimit).Reverse().ToArray();
            _undo.Clear();
            foreach (var s in keep)
            {
                _undo.Push(s);
            }
        }
        _redo.Clear();
        Set(entries);
    }

    private void Set(ImmutableArray<PlaylistEntry> entries)
    {
        _entries = entries;
        Interlocked.MemoryBarrier(); // publish the snapshot before the version and the event
        Interlocked.Increment(ref _version);
        Changed?.Invoke(this, new PlaylistChangedEventArgs(PlaylistChange.Entries));
    }
}
