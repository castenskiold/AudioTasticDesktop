using System.Globalization;
using AudioTastic.Core.Playback;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.App.Ui;

/// <summary>Text shown by the player: time display (CO-06), scrolling title (UI-06) and format indicators (UI-06).</summary>
public static class DisplayText
{
    /// <summary>m:ss, or h:mm:ss for an hour or more.</summary>
    public static string Time(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }
        return time.TotalHours >= 1
            ? string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}")
            : string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}");
    }

    /// <summary>CO-06: elapsed, or remaining with a leading minus. Streams without a length always show elapsed.</summary>
    public static string TimeDisplay(TimeSpan position, TimeSpan? duration, bool remaining) =>
        remaining && duration is { } length ? "-" + Time(length - position) : Time(position);

    /// <summary>UI-06: "Artist – Title (5:12)".</summary>
    public static string Title(TrackInfo? track)
    {
        if (track is null)
        {
            return "AudioTastic";
        }
        var name = string.IsNullOrWhiteSpace(track.Artist) ? track.Title : $"{track.Artist} – {track.Title}";
        return track.Duration is { } d ? $"{name} ({Time(d)})" : name;
    }

    /// <summary>UI-06 / MW5: codec, bit depth (lossless), bitrate, sample rate and channels.</summary>
    public static IReadOnlyList<string> Indicators(TrackInfo? track, ReplayGainDecision? replayGain = null)
    {
        if (track is null)
        {
            return [];
        }
        var s = track.Stream;
        var list = new List<string> { CodecLabel(s.Codec, track.Path) };
        if (s.IsLossless && s.BitsPerSample > 0)
        {
            list.Add(string.Create(CultureInfo.InvariantCulture, $"{s.BitsPerSample} BIT"));
        }
        if (s.Bitrate > 0)
        {
            list.Add(string.Create(CultureInfo.InvariantCulture, $"{(s.Bitrate + 500) / 1000} KBPS"));
        }
        list.Add(string.Create(CultureInfo.InvariantCulture, $"{s.SampleRate / 1000.0:0.#} KHZ"));
        list.Add(s.Channels switch
        {
            1 => "MONO",
            2 => "STEREO",
            6 => "5.1",
            8 => "7.1",
            var n => string.Create(CultureInfo.InvariantCulture, $"{n} CH"),
        });
        if (replayGain is not null)
        {
            list.Add("RG"); // PB-03: a ReplayGain / loudness gain is applied
        }
        return list;
    }

    /// <summary>Short format name for the indicator row.</summary>
    public static string CodecLabel(string codec, string path)
    {
        if (codec.StartsWith("pcm_", StringComparison.Ordinal))
        {
            return Path.GetExtension(path).ToUpperInvariant() switch
            {
                ".AIF" or ".AIFF" or ".AIFC" => "AIFF",
                _ => "WAV",
            };
        }
        return codec switch
        {
            "wavpack" => "WV",
            "mp3" or "mp3float" => "MP3",
            _ => codec.ToUpperInvariant(),
        };
    }

    /// <summary>Format summary for tooltips and the About/info text.</summary>
    public static string Describe(AudioStreamInfo s) =>
        string.Join(" · ", new[]
        {
            s.Codec,
            s.BitsPerSample > 0 ? $"{s.BitsPerSample}-bit" : null,
            string.Create(CultureInfo.InvariantCulture, $"{s.SampleRate / 1000.0:0.#} kHz"),
            s.Channels == 1 ? "mono" : s.Channels == 2 ? "stereo" : $"{s.Channels} ch",
            s.Bitrate > 0 ? $"{s.Bitrate / 1000} kbps" : null,
        }.OfType<string>());
}
