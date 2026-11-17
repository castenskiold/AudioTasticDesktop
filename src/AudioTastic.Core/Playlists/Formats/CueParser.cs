using System.Globalization;
using System.Text;
using AudioTastic.Sdk.Playlists;

namespace AudioTastic.Core.Playlists.Formats;

/// <summary>A parsed cue sheet.</summary>
public sealed record CueSheet(string? Title, string? Performer, string? Genre, string? Date, IReadOnlyList<CueFile> Files);

public sealed record CueFile(string Name, string Type, IReadOnlyList<CueTrack> Tracks);

/// <summary>A track; <see cref="Start"/> is INDEX 01.</summary>
public sealed record CueTrack(int Number, string? Title, string? Performer, TimeSpan Start);

/// <summary>
/// FM-09: CUE sheet parser (own code). Understands FILE, TRACK, TITLE, PERFORMER, INDEX 01 mm:ss:ff (75 frames per
/// second) and REM GENRE/DATE; other commands are ignored. A track ends where the next track in the same file starts
/// (INDEX 01), so pregaps stay with the previous track and playback of the whole file is continuous.
/// </summary>
public static class CueParser
{
    /// <summary>CD frames per second (CUE times are mm:ss:ff).</summary>
    private const int FramesPerSecond = 75;

    public static CueSheet Parse(string text)
    {
        string? title = null, performer = null, genre = null, date = null;
        var files = new List<CueFile>();
        string? fileName = null, fileType = null;
        var tracks = new List<CueTrack>();
        int? trackNumber = null;
        string? trackTitle = null, trackPerformer = null;
        TimeSpan? trackStart = null;

        void EndTrack()
        {
            if (trackNumber is { } n && trackStart is { } s)
            {
                tracks.Add(new CueTrack(n, trackTitle, trackPerformer, s));
            }
            trackNumber = null;
            trackTitle = trackPerformer = null;
            trackStart = null;
        }

        void EndFile()
        {
            EndTrack();
            if (fileName is not null)
            {
                files.Add(new CueFile(fileName, fileType ?? "WAVE", [.. tracks]));
            }
            tracks.Clear();
        }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim().TrimEnd('\r');
            var (command, rest) = SplitCommand(line);
            switch (command)
            {
                case "FILE":
                {
                    EndFile();
                    var (name, after) = ReadValue(rest, trailingWord: true);
                    fileName = name;
                    fileType = after.Trim().ToUpperInvariant();
                    break;
                }
                case "TRACK":
                    EndTrack();
                    trackNumber = int.TryParse(rest.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(),
                        NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : tracks.Count + 1;
                    break;
                case "TITLE":
                    if (trackNumber is null)
                    {
                        title = ReadValue(rest).Value;
                    }
                    else
                    {
                        trackTitle = ReadValue(rest).Value;
                    }
                    break;
                case "PERFORMER":
                    if (trackNumber is null)
                    {
                        performer = ReadValue(rest).Value;
                    }
                    else
                    {
                        trackPerformer = ReadValue(rest).Value;
                    }
                    break;
                case "INDEX":
                {
                    var parts = rest.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length == 2 && parts[0] == "01" && ParseTime(parts[1]) is { } time)
                    {
                        trackStart = time;
                    }
                    break;
                }
                case "REM":
                {
                    var (key, value) = SplitCommand(rest);
                    if (key == "GENRE")
                    {
                        genre = ReadValue(value).Value;
                    }
                    else if (key == "DATE")
                    {
                        date = ReadValue(value).Value;
                    }
                    break;
                }
            }
        }
        EndFile();
        return new CueSheet(title, performer, genre, date, files);
    }

    /// <summary>
    /// The playlist items of a cue sheet: one virtual track per TRACK. FILE names are resolved against
    /// <paramref name="baseFolder"/>; when the named file is missing, a file with the same name and another audio
    /// extension is used (rips are often re-encoded, e.g. .wav → .flac). With <paramref name="audioFile"/> (an
    /// embedded CUESHEET tag) every FILE refers to that file.
    /// </summary>
    public static IReadOnlyList<PlaylistItem> ToItems(CueSheet sheet, string baseFolder, string? audioFile = null)
    {
        var items = new List<PlaylistItem>();
        foreach (var file in sheet.Files)
        {
            var path = audioFile ?? FindAudio(PlaylistText.Resolve(file.Name, baseFolder));
            var tracks = file.Tracks;
            for (var i = 0; i < tracks.Count; i++)
            {
                var t = tracks[i];
                TimeSpan? end = i + 1 < tracks.Count ? tracks[i + 1].Start : null;
                // A file with one track from 0:00 is simply the whole file.
                var whole = tracks.Count == 1 && t.Start == TimeSpan.Zero;
                items.Add(new PlaylistItem(
                    path,
                    t.Title,
                    t.Performer ?? sheet.Performer,
                    end is { } e ? e - t.Start : null,
                    whole ? null : t.Start,
                    whole ? null : end,
                    t.Number,
                    sheet.Title));
            }
        }
        return items;
    }

    /// <summary>Writes a cue sheet for <paramref name="items"/>; consecutive virtual tracks of one file share a FILE block.</summary>
    public static string Write(IReadOnlyList<PlaylistItem> items, string baseFolder, bool relativePaths)
    {
        var sb = new StringBuilder();
        var number = 0;
        string? currentFile = null;
        foreach (var item in items)
        {
            if (!item.IsVirtual || item.Path != currentFile)
            {
                var type = Path.GetExtension(item.Path).Equals(".mp3", StringComparison.OrdinalIgnoreCase) ? "MP3" : "WAVE";
                sb.Append(CultureInfo.InvariantCulture, $"FILE \"{PlaylistText.Write(item.Path, baseFolder, relativePaths)}\" {type}\n");
                currentFile = item.IsVirtual ? item.Path : null;
            }
            number++;
            sb.Append(CultureInfo.InvariantCulture, $"  TRACK {number:00} AUDIO\n");
            if (item.Title is not null)
            {
                sb.Append(CultureInfo.InvariantCulture, $"    TITLE \"{Escape(item.Title)}\"\n");
            }
            if (item.Artist is not null)
            {
                sb.Append(CultureInfo.InvariantCulture, $"    PERFORMER \"{Escape(item.Artist)}\"\n");
            }
            sb.Append(CultureInfo.InvariantCulture, $"    INDEX 01 {FormatTime(item.Start ?? TimeSpan.Zero)}\n");
        }
        return sb.ToString();
    }

    /// <summary>mm:ss:ff → time; null when malformed.</summary>
    public static TimeSpan? ParseTime(string value)
    {
        var p = value.Split(':');
        if (p.Length != 3
            || !int.TryParse(p[0], NumberStyles.None, CultureInfo.InvariantCulture, out var m)
            || !int.TryParse(p[1], NumberStyles.None, CultureInfo.InvariantCulture, out var s)
            || !int.TryParse(p[2], NumberStyles.None, CultureInfo.InvariantCulture, out var f)
            || s > 59 || f > 74)
        {
            return null;
        }
        // Round once on the total so times are exact to the tick (and exact in samples at 44.1/48 kHz).
        var frames = ((((long)m * 60) + s) * FramesPerSecond) + f;
        return TimeSpan.FromTicks((long)Math.Round(frames * (double)TimeSpan.TicksPerSecond / FramesPerSecond));
    }

    public static string FormatTime(TimeSpan time)
    {
        var frames = (long)Math.Round(time.Ticks * (double)FramesPerSecond / TimeSpan.TicksPerSecond);
        return string.Create(CultureInfo.InvariantCulture, $"{frames / 75 / 60:00}:{frames / 75 % 60:00}:{frames % 75:00}");
    }

    private static readonly string[] AudioExtensions = [".flac", ".wav", ".ape", ".wv", ".mp3", ".m4a", ".aiff", ".aif", ".tta", ".ogg", ".opus"];

    private static string FindAudio(string path)
    {
        if (path.Contains("://", StringComparison.Ordinal) || File.Exists(path))
        {
            return path;
        }
        var stem = Path.ChangeExtension(path, null);
        return AudioExtensions.Select(e => stem + e).FirstOrDefault(File.Exists) ?? path;
    }

    private static (string Command, string Remainder) SplitCommand(string line)
    {
        var space = line.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? (line.ToUpperInvariant(), string.Empty) : (line[..space].ToUpperInvariant(), line[(space + 1)..].Trim());
    }

    /// <summary>A quoted or bare value, and what follows it.</summary>
    private static (string Value, string Remainder) ReadValue(string text, bool trailingWord = false)
    {
        text = text.Trim();
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end < 0 ? (text[1..], string.Empty) : (text[1..end], text[(end + 1)..]);
        }
        if (!trailingWord)
        {
            return (text, string.Empty);
        }
        var space = text.LastIndexOf(' ');
        // FILE name TYPE without quotes: the type is the last word.
        return space < 0 ? (text, string.Empty) : (text[..space], text[space..]);
    }

    private static string Escape(string text) => text.Replace('"', '\'');
}
