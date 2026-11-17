using System.Globalization;

namespace AudioTastic.Core.Playlists;

/// <summary>PL-06: sort comparers. Text compares naturally ("Track 2" before "Track 10"), case- and accent-insensitively.</summary>
public static class PlaylistSorting
{
    public static IComparer<PlaylistEntry> Comparer(PlaylistSortKey key) => key switch
    {
        PlaylistSortKey.Title => Text(e => e.Tags?.Title ?? e.FileName),
        PlaylistSortKey.Artist => Text(e => e.Tags?.Artist ?? e.Tags?.AlbumArtist),
        PlaylistSortKey.Album => Then(Text(e => e.Tags?.Album), Then(Number(e => e.Tags?.DiscNumber ?? (e.Tags?.TrackNumber is null ? null : 1)), Number(e => e.Tags?.TrackNumber))),
        PlaylistSortKey.TrackNumber => Then(Number(e => e.Tags?.DiscNumber ?? (e.Tags?.TrackNumber is null ? null : 1)), Number(e => e.Tags?.TrackNumber)),
        PlaylistSortKey.Path => Then(Text(e => e.Path), Comparer<PlaylistEntry>.Create((a, b) => Nullable.Compare(a.Start, b.Start))),
        PlaylistSortKey.FileName => Text(e => Path.GetFileName(e.Path)),
        PlaylistSortKey.Duration => Comparer<PlaylistEntry>.Create((a, b) => Nullable.Compare(a.Duration, b.Duration)),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    /// <summary>Natural, case- and accent-insensitive comparison; null and empty sort last.</summary>
    public static int NaturalCompare(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return string.IsNullOrEmpty(a) ? (string.IsNullOrEmpty(b) ? 0 : 1) : -1;
        }
        int i = 0, j = 0;
        var compare = CultureInfo.CurrentCulture.CompareInfo;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsAsciiDigit(a[i]) && char.IsAsciiDigit(b[j]))
            {
                var si = i;
                var sj = j;
                while (i < a.Length && char.IsAsciiDigit(a[i]))
                {
                    i++;
                }
                while (j < b.Length && char.IsAsciiDigit(b[j]))
                {
                    j++;
                }
                var na = a.AsSpan(si, i - si).TrimStart('0');
                var nb = b.AsSpan(sj, j - sj).TrimStart('0');
                var byLength = na.Length.CompareTo(nb.Length);
                if (byLength != 0)
                {
                    return byLength;
                }
                var byDigits = na.SequenceCompareTo(nb);
                if (byDigits != 0)
                {
                    return byDigits;
                }
                continue;
            }
            var ei = i;
            var ej = j;
            while (ei < a.Length && !char.IsAsciiDigit(a[ei]))
            {
                ei++;
            }
            while (ej < b.Length && !char.IsAsciiDigit(b[ej]))
            {
                ej++;
            }
            var text = compare.Compare(a, i, ei - i, b, j, ej - j, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace);
            if (text != 0)
            {
                return text;
            }
            i = ei;
            j = ej;
        }
        return (a.Length - i).CompareTo(b.Length - j);
    }

    private static Comparer<PlaylistEntry> Text(Func<PlaylistEntry, string?> key) =>
        Comparer<PlaylistEntry>.Create((a, b) => NaturalCompare(key(a), key(b)));

    private static Comparer<PlaylistEntry> Number(Func<PlaylistEntry, int?> key) =>
        Comparer<PlaylistEntry>.Create((a, b) => (key(a), key(b)) switch
        {
            (null, null) => 0,
            (null, _) => 1,
            (_, null) => -1,
            var (x, y) => x!.Value.CompareTo(y!.Value),
        });

    private static Comparer<PlaylistEntry> Then(Comparer<PlaylistEntry> first, Comparer<PlaylistEntry> second) =>
        Comparer<PlaylistEntry>.Create((a, b) => first.Compare(a, b) is var r && r != 0 ? r : second.Compare(a, b));
}

/// <summary>
/// PL-05: "J" search. An entry matches when every word of the query occurs in its title, artist, album or file name,
/// ignoring case and accents.
/// </summary>
public sealed class SearchFilter
{
    private const CompareOptions Options = CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace;
    private readonly string[] _words;
    private readonly CompareInfo _compare = CultureInfo.CurrentCulture.CompareInfo;

    public SearchFilter(string? query)
    {
        _words = (query ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public bool IsEmpty => _words.Length == 0;

    public bool Matches(PlaylistEntry entry)
    {
        if (IsEmpty)
        {
            return true;
        }
        var tags = entry.Tags;
        foreach (var word in _words)
        {
            if (!Contains(tags?.Title, word) && !Contains(tags?.Artist, word) && !Contains(tags?.Album, word)
                && !Contains(Path.GetFileName(entry.Path), word))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Indexes of the matching entries, in order.</summary>
    public IReadOnlyList<int> Apply(IReadOnlyList<PlaylistEntry> entries)
    {
        var result = new List<int>();
        for (var i = 0; i < entries.Count; i++)
        {
            if (Matches(entries[i]))
            {
                result.Add(i);
            }
        }
        return result;
    }

    private bool Contains(string? text, string word) => text is not null && _compare.IndexOf(text, word, Options) >= 0;
}
