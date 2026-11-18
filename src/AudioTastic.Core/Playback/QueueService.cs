using AudioTastic.Core.Playlists;

namespace AudioTastic.Core.Playback;

/// <summary>
/// PB-09: the "play next" queue. Queued entries play before the normal play order (shuffle/repeat), in the order
/// they were queued; each remembers its playlist, which becomes the playing playlist when it starts. Entries that were
/// removed from their playlist are skipped. Thread-safe; <see cref="Changed"/> is raised on the caller's thread.
/// </summary>
public sealed class QueueService
{
    private readonly Lock _lock = new();
    private readonly List<(Playlist Playlist, PlaylistEntry Entry)> _items = [];
    private int _version;

    /// <summary>Queue contents changed (PW4 numbers, preloads).</summary>
    public event EventHandler? Changed;

    public int Version => Volatile.Read(ref _version);

    public int Count
    {
        get
        {
            lock (_lock)
            {
                return _items.Count;
            }
        }
    }

    public IReadOnlyList<(Playlist Playlist, PlaylistEntry Entry)> Items
    {
        get
        {
            lock (_lock)
            {
                return [.. _items];
            }
        }
    }

    /// <summary>Adds entries at the end of the queue.</summary>
    public void Enqueue(Playlist playlist, IEnumerable<PlaylistEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(playlist);
        ArgumentNullException.ThrowIfNull(entries);
        lock (_lock)
        {
            _items.AddRange(entries.Select(e => (playlist, e)));
        }
        OnChanged();
    }

    /// <summary>Removes an entry from the queue (all occurrences).</summary>
    public void Remove(PlaylistEntry entry)
    {
        int removed;
        lock (_lock)
        {
            removed = _items.RemoveAll(i => ReferenceEquals(i.Entry, entry));
        }
        if (removed > 0)
        {
            OnChanged();
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            if (_items.Count == 0)
            {
                return;
            }
            _items.Clear();
        }
        OnChanged();
    }

    /// <summary>1-based queue positions of <paramref name="entry"/>, empty when not queued (PW4 shows them).</summary>
    public IReadOnlyList<int> PositionsOf(PlaylistEntry entry)
    {
        lock (_lock)
        {
            var positions = new List<int>();
            for (var i = 0; i < _items.Count; i++)
            {
                if (ReferenceEquals(_items[i].Entry, entry))
                {
                    positions.Add(i + 1);
                }
            }
            return positions;
        }
    }

    /// <summary>The first queued entry that is still in its playlist, with its index; null when none.</summary>
    public (Playlist Playlist, int Index, PlaylistEntry Entry)? Peek()
    {
        lock (_lock)
        {
            foreach (var (playlist, entry) in _items)
            {
                var index = playlist.Entries.IndexOf(entry);
                if (index >= 0)
                {
                    return (playlist, index, entry);
                }
            }
            return null;
        }
    }

    /// <summary>Removes the first queued entry that is still in its playlist (and stale ones before it).</summary>
    public (Playlist Playlist, int Index, PlaylistEntry Entry)? Dequeue()
    {
        (Playlist, int, PlaylistEntry)? result = null;
        lock (_lock)
        {
            while (_items.Count > 0 && result is null)
            {
                var (playlist, entry) = _items[0];
                _items.RemoveAt(0);
                var index = playlist.Entries.IndexOf(entry);
                if (index >= 0)
                {
                    result = (playlist, index, entry);
                }
            }
        }
        OnChanged();
        return result;
    }

    private void OnChanged()
    {
        Interlocked.Increment(ref _version);
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
