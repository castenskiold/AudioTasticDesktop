using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AudioTastic.Sdk.Playlists;

namespace AudioTastic.Core.Playlists.Formats;

/// <summary>
/// PL-03: M3U and M3U8 (#EXTM3U, #EXTINF:seconds,title). AudioTastic adds "#AT-RANGE:startMs,endMs" before a CUE
/// virtual track; other players ignore unknown # lines. .m3u8 is UTF-8; .m3u is read as UTF-8 or the legacy code
/// page and written in the legacy code page when it can hold every name, else UTF-8.
/// </summary>
public sealed class M3uFormat : IPlaylistFormat
{
    private const string RangeTag = "#AT-RANGE:";

    public string Name => "M3U / M3U8 playlist";
    public IReadOnlyCollection<string> Extensions { get; } = [".m3u", ".m3u8"];
    public bool CanWrite => true;

    public IReadOnlyList<PlaylistItem> Read(string file) => Parse(PlaylistText.Read(file), PlaylistText.FolderOf(file));

    public static IReadOnlyList<PlaylistItem> Parse(string text, string baseFolder)
    {
        var items = new List<PlaylistItem>();
        string? title = null;
        TimeSpan? duration = null;
        TimeSpan? start = null, end = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                var comma = line.IndexOf(',', StringComparison.Ordinal);
                var seconds = comma > 8 ? line[8..comma] : line[8..];
                duration = double.TryParse(seconds, NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s >= 0
                    ? TimeSpan.FromSeconds(s) : null;
                title = comma >= 0 && comma + 1 < line.Length ? line[(comma + 1)..].Trim() : null;
                continue;
            }
            if (line.StartsWith(RangeTag, StringComparison.OrdinalIgnoreCase))
            {
                var parts = line[RangeTag.Length..].Split(',');
                start = parts.Length > 0 && long.TryParse(parts[0], CultureInfo.InvariantCulture, out var a) ? TimeSpan.FromTicks(a) : null;
                end = parts.Length > 1 && long.TryParse(parts[1], CultureInfo.InvariantCulture, out var b) ? TimeSpan.FromTicks(b) : null;
                continue;
            }
            if (line.StartsWith('#'))
            {
                continue;
            }
            var (artist, name) = PlaylistText.SplitArtistTitle(title);
            items.Add(new PlaylistItem(PlaylistText.Resolve(line, baseFolder), name, artist, duration, start, end));
            title = null;
            duration = null;
            start = end = null;
        }
        return items;
    }

    public void Write(string file, IReadOnlyList<PlaylistItem> items, bool relativePaths)
    {
        var text = Format(items, PlaylistText.FolderOf(file), relativePaths);
        var encoding = file.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase) ? new UTF8Encoding(false) : PlaylistText.BestLegacyOrUtf8(text);
        File.WriteAllText(file, text, encoding);
    }

    public static string Format(IReadOnlyList<PlaylistItem> items, string baseFolder, bool relativePaths)
    {
        var sb = new StringBuilder("#EXTM3U\n");
        foreach (var item in items)
        {
            var seconds = item.Duration is { } d ? (long)Math.Round(d.TotalSeconds) : -1;
            var title = item.Artist is not null && item.Title is not null ? $"{item.Artist} - {item.Title}" : item.Title;
            if (title is not null || seconds >= 0)
            {
                sb.Append(CultureInfo.InvariantCulture, $"#EXTINF:{seconds},{title}\n");
            }
            if (item.Start is { } start)
            {
                sb.Append(CultureInfo.InvariantCulture, $"{RangeTag}{start.Ticks},{item.End?.Ticks.ToString(CultureInfo.InvariantCulture)}\n");
            }
            sb.Append(PlaylistText.Write(item.Path, baseFolder, relativePaths)).Append('\n');
        }
        return sb.ToString();
    }
}

/// <summary>PL-03: PLS ([playlist], FileN, TitleN, LengthN, NumberOfEntries, Version=2).</summary>
public sealed class PlsFormat : IPlaylistFormat
{
    public string Name => "PLS playlist";
    public IReadOnlyCollection<string> Extensions { get; } = [".pls"];
    public bool CanWrite => true;

    public IReadOnlyList<PlaylistItem> Read(string file)
    {
        var folder = PlaylistText.FolderOf(file);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in PlaylistText.Read(file).Split('\n'))
        {
            var line = raw.Trim();
            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0 && !line.StartsWith('['))
            {
                values[line[..eq].Trim()] = line[(eq + 1)..].Trim();
            }
        }
        var numbers = values.Keys
            .Where(k => k.StartsWith("File", StringComparison.OrdinalIgnoreCase))
            .Select(k => int.TryParse(k.AsSpan(4), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : -1)
            .Where(n => n > 0)
            .Order();
        var items = new List<PlaylistItem>();
        foreach (var n in numbers)
        {
            var path = values[$"File{n}"];
            values.TryGetValue($"Title{n}", out var title);
            TimeSpan? duration = values.TryGetValue($"Length{n}", out var length)
                && long.TryParse(length, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) && s >= 0
                ? TimeSpan.FromSeconds(s) : null;
            var (artist, name) = PlaylistText.SplitArtistTitle(title);
            items.Add(new PlaylistItem(PlaylistText.Resolve(path, folder), name, artist, duration));
        }
        return items;
    }

    public void Write(string file, IReadOnlyList<PlaylistItem> items, bool relativePaths)
    {
        var folder = PlaylistText.FolderOf(file);
        var sb = new StringBuilder("[playlist]\n");
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var n = i + 1;
            sb.Append(CultureInfo.InvariantCulture, $"File{n}={PlaylistText.Write(item.Path, folder, relativePaths)}\n");
            var title = item.Artist is not null && item.Title is not null ? $"{item.Artist} - {item.Title}" : item.Title;
            if (title is not null)
            {
                sb.Append(CultureInfo.InvariantCulture, $"Title{n}={title}\n");
            }
            sb.Append(CultureInfo.InvariantCulture, $"Length{n}={(item.Duration is { } d ? (long)Math.Round(d.TotalSeconds) : -1)}\n");
        }
        sb.Append(CultureInfo.InvariantCulture, $"NumberOfEntries={items.Count}\nVersion=2\n");
        File.WriteAllText(file, sb.ToString(), new UTF8Encoding(false));
    }
}

/// <summary>
/// PL-03: XSPF (xspf.org, version 1). Locations are file URIs (relative URIs when relative paths are chosen).
/// CUE ranges are kept in a &lt;meta rel="https://audiotastic.app/xspf/range"&gt; element (start and end in ticks).
/// </summary>
public sealed class XspfFormat : IPlaylistFormat
{
    private const string RangeRel = "https://audiotastic.app/xspf/range";
    private static readonly XNamespace Ns = "http://xspf.org/ns/0/";

    public string Name => "XSPF playlist";
    public IReadOnlyCollection<string> Extensions { get; } = [".xspf"];
    public bool CanWrite => true;

    public IReadOnlyList<PlaylistItem> Read(string file)
    {
        var folder = PlaylistText.FolderOf(file);
        var doc = XDocument.Load(file, LoadOptions.None);
        var ns = doc.Root?.Name.Namespace ?? Ns;
        var items = new List<PlaylistItem>();
        foreach (var track in doc.Descendants(ns + "track"))
        {
            var location = track.Element(ns + "location")?.Value.Trim();
            if (string.IsNullOrEmpty(location))
            {
                continue;
            }
            var path = location.Contains("://", StringComparison.Ordinal) || location.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
                ? PlaylistText.Resolve(location, folder)
                : PlaylistText.Resolve(Uri.UnescapeDataString(location), folder);
            TimeSpan? duration = long.TryParse(track.Element(ns + "duration")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms)
                ? TimeSpan.FromMilliseconds(ms) : null;
            int? number = int.TryParse(track.Element(ns + "trackNum")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : null;
            TimeSpan? start = null, end = null;
            if (track.Elements(ns + "meta").FirstOrDefault(m => (string?)m.Attribute("rel") == RangeRel)?.Value.Split(',') is { } range)
            {
                start = range.Length > 0 && long.TryParse(range[0], CultureInfo.InvariantCulture, out var a) ? TimeSpan.FromTicks(a) : null;
                end = range.Length > 1 && long.TryParse(range[1], CultureInfo.InvariantCulture, out var b) ? TimeSpan.FromTicks(b) : null;
            }
            items.Add(new PlaylistItem(path, NullIfEmpty(track.Element(ns + "title")?.Value), NullIfEmpty(track.Element(ns + "creator")?.Value),
                duration, start, end, number, NullIfEmpty(track.Element(ns + "album")?.Value)));
        }
        return items;
    }

    public void Write(string file, IReadOnlyList<PlaylistItem> items, bool relativePaths)
    {
        var folder = PlaylistText.FolderOf(file);
        var tracks = items.Select(item =>
        {
            var written = PlaylistText.Write(item.Path, folder, relativePaths);
            var location = written.Contains("://", StringComparison.Ordinal) ? written
                : Path.IsPathRooted(written) ? new Uri(written).AbsoluteUri
                : string.Join('/', written.Split(Path.DirectorySeparatorChar).Select(Uri.EscapeDataString));
            var track = new XElement(Ns + "track", new XElement(Ns + "location", location));
            if (item.Title is not null)
            {
                track.Add(new XElement(Ns + "title", item.Title));
            }
            if (item.Artist is not null)
            {
                track.Add(new XElement(Ns + "creator", item.Artist));
            }
            if (item.Album is not null)
            {
                track.Add(new XElement(Ns + "album", item.Album));
            }
            if (item.TrackNumber is { } n)
            {
                track.Add(new XElement(Ns + "trackNum", n));
            }
            if (item.Duration is { } d)
            {
                track.Add(new XElement(Ns + "duration", (long)Math.Round(d.TotalMilliseconds)));
            }
            if (item.Start is { } start)
            {
                track.Add(new XElement(Ns + "meta", new XAttribute("rel", RangeRel),
                    string.Create(CultureInfo.InvariantCulture, $"{start.Ticks},{item.End?.Ticks}")));
            }
            return track;
        });
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null),
            new XElement(Ns + "playlist", new XAttribute("version", "1"), new XElement(Ns + "trackList", tracks)));
        using var writer = XmlWriter.Create(file, new XmlWriterSettings { Indent = true, Encoding = new UTF8Encoding(false) });
        doc.Save(writer);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>PL-03: Windows Media Player playlists (WPL: smil/body/seq/media src). Paths only.</summary>
public sealed class WplFormat : IPlaylistFormat
{
    public string Name => "Windows Media playlist";
    public IReadOnlyCollection<string> Extensions { get; } = [".wpl"];
    public bool CanWrite => true;

    public IReadOnlyList<PlaylistItem> Read(string file)
    {
        var folder = PlaylistText.FolderOf(file);
        var doc = XDocument.Load(file);
        return doc.Descendants()
            .Where(e => e.Name.LocalName == "media")
            .Select(e => (string?)e.Attribute("src"))
            .OfType<string>()
            .Where(src => src.Length > 0)
            .Select(src => new PlaylistItem(PlaylistText.Resolve(src, folder)))
            .ToList();
    }

    public void Write(string file, IReadOnlyList<PlaylistItem> items, bool relativePaths)
    {
        var folder = PlaylistText.FolderOf(file);
        var doc = new XDocument(
            new XProcessingInstruction("wpl", "version=\"1.0\""),
            new XElement("smil",
                new XElement("head",
                    new XElement("meta", new XAttribute("name", "Generator"), new XAttribute("content", "AudioTastic")),
                    new XElement("title", Path.GetFileNameWithoutExtension(file))),
                new XElement("body", new XElement("seq",
                    items.Select(i => new XElement("media", new XAttribute("src", PlaylistText.Write(i.Path, folder, relativePaths))))))));
        using var writer = XmlWriter.Create(file, new XmlWriterSettings { Indent = true, OmitXmlDeclaration = true, Encoding = new UTF8Encoding(false) });
        doc.Save(writer);
    }
}

/// <summary>PL-03 / FM-09: CUE sheets as playlists.</summary>
public sealed class CueFormat : IPlaylistFormat
{
    public string Name => "CUE sheet";
    public IReadOnlyCollection<string> Extensions { get; } = [".cue"];
    public bool CanWrite => true;

    public IReadOnlyList<PlaylistItem> Read(string file) =>
        CueParser.ToItems(CueParser.Parse(PlaylistText.Read(file)), PlaylistText.FolderOf(file));

    public void Write(string file, IReadOnlyList<PlaylistItem> items, bool relativePaths) =>
        File.WriteAllText(file, CueParser.Write(items, PlaylistText.FolderOf(file), relativePaths), new UTF8Encoding(false));
}

/// <summary>The built-in formats (PL-03).</summary>
public static class PlaylistFormats
{
    public static IReadOnlyList<IPlaylistFormat> BuiltIn { get; } =
        [new M3uFormat(), new PlsFormat(), new XspfFormat(), new WplFormat(), new CueFormat()];

    public static IPlaylistFormat? For(string path, IEnumerable<IPlaylistFormat>? formats = null) =>
        (formats ?? BuiltIn).FirstOrDefault(f => f.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase));
}
