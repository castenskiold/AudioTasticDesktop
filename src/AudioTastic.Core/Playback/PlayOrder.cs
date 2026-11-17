using AudioTastic.Data.Settings;

namespace AudioTastic.Core.Playback;

/// <summary>
/// CO-03 / CO-04: decides which track comes next or before. With shuffle on, a Fisher–Yates order is generated when
/// shuffle is switched on or the list changes, with the current track first, so every track plays once before any
/// repeats; Previous walks back through that order. Repeat track replays the current track at its end (the Next
/// button still moves on); repeat playlist wraps around (and reshuffles). Not thread-safe; the engine calls it
/// inside its command gate.
/// </summary>
public sealed class PlayOrder
{
    private readonly Random _random;
    private int[] _order = [];
    private int _count;

    public PlayOrder(Random? random = null)
    {
        _random = random ?? Random.Shared;
    }

    public bool Shuffle { get; private set; }

    public RepeatMode Repeat { get; set; }

    /// <summary>The shuffled order (empty when shuffle is off). For tests and the playlist view.</summary>
    public IReadOnlyList<int> Order => _order;

    /// <summary>Call when the list changes. <paramref name="current"/> (or -1) becomes the first shuffled entry.</summary>
    public void Reset(int count, int current)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _count = count;
        _order = Shuffle ? NewOrder(current) : [];
    }

    /// <summary>Switches shuffle on or off; switching on makes a new order starting at <paramref name="current"/>.</summary>
    public void SetShuffle(bool shuffle, int current)
    {
        Shuffle = shuffle;
        _order = shuffle ? NewOrder(current) : [];
    }

    /// <summary>
    /// The track after <paramref name="current"/>, or null when playback should stop.
    /// <paramref name="trackEnded"/> is true when the current track finished on its own (repeat track applies then).
    /// </summary>
    public int? Next(int current, bool trackEnded)
    {
        if (_count == 0)
        {
            return null;
        }
        if (trackEnded && Repeat == RepeatMode.Track && current >= 0)
        {
            return current;
        }
        if (!Shuffle)
        {
            if (current + 1 < _count)
            {
                return current + 1;
            }
            return Repeat == RepeatMode.Off ? null : 0;
        }

        var position = Array.IndexOf(_order, current);
        if (position >= 0 && position + 1 < _order.Length)
        {
            return _order[position + 1];
        }
        if (position < 0 && _order.Length > 0)
        {
            return _order[0];
        }
        if (Repeat == RepeatMode.Off)
        {
            return null;
        }
        // Wrap around with a fresh order that does not start with the track that just played.
        _order = NewOrder(-1);
        if (_order.Length > 1 && _order[0] == current)
        {
            (_order[0], _order[^1]) = (_order[^1], _order[0]);
        }
        return _order[0];
    }

    /// <summary>The track before <paramref name="current"/>, or null at the start.</summary>
    public int? Previous(int current)
    {
        if (_count == 0)
        {
            return null;
        }
        if (!Shuffle)
        {
            if (current > 0)
            {
                return current - 1;
            }
            return Repeat == RepeatMode.Playlist ? _count - 1 : null;
        }
        var position = Array.IndexOf(_order, current);
        return position > 0 ? _order[position - 1] : null;
    }

    private int[] NewOrder(int first)
    {
        var order = Enumerable.Range(0, _count).ToArray();
        // Fisher–Yates
        for (var i = order.Length - 1; i > 0; i--)
        {
            var j = _random.Next(i + 1);
            (order[i], order[j]) = (order[j], order[i]);
        }
        if (first >= 0 && first < _count)
        {
            var at = Array.IndexOf(order, first);
            (order[0], order[at]) = (order[at], order[0]);
        }
        return order;
    }
}
