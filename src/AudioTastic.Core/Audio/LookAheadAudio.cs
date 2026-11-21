using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using AudioTastic.Sdk.Audio;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Core.Audio;

/// <summary>Where playback is: the entry, whether it plays, its position in track time, its length and speed.</summary>
public sealed record PlayingPoint(PlaylistEntry? Entry, bool Playing, TimeSpan Position, TimeSpan? Duration, double Speed)
{
    public static PlayingPoint Of(IPlayerController player)
    {
        ArgumentNullException.ThrowIfNull(player);
        return new PlayingPoint(player.CurrentEntry, player.State == PlaybackState.Playing, player.Position, player.Duration, player.Speed);
    }
}

/// <summary>
/// CN-09, video only: the audio the speakers will play <c>ahead</c> from now. A TV shows a cast picture seconds late,
/// so the visualisation is drawn from the playing file decoded ahead by that delay; the picture then reaches the
/// screen when the speakers reach that audio. A second decoder follows the playing entry (and CUE range): it reads on
/// sequentially while the target moves forward and seeks when it jumps (a seek, another track).
/// The audio is the decoder's (before ReplayGain, DSP and volume), which is what a visualisation needs. Not for live
/// streams (they cannot be read ahead) or beyond the track's end (silence). One thread only.
/// </summary>
public sealed class LookAheadAudio : IDisposable
{
    private const int RingFrames = 16_384;
    private const int ChunkFrames = 2048;

    private readonly Func<PlayingPoint> _playing;
    private readonly IEnumerable<IAudioSourceFactory> _factories;
    private readonly ILogger _logger;
    private readonly float[] _ring = new float[RingFrames * 2]; // stereo, frame f at (f % RingFrames)
    private float[] _chunk = [];
    private PlaylistEntry? _entry;
    private IAudioSource? _source;
    private bool _failed;
    private long _end; // file frame just after the newest decoded frame
    private long _start; // oldest file frame held in the ring

    public LookAheadAudio(Func<PlayingPoint> playing, IEnumerable<IAudioSourceFactory> factories, ILogger logger)
    {
        _playing = playing;
        _factories = factories;
        _logger = logger;
    }

    /// <summary>The sample rate of the latest <see cref="Read"/>.</summary>
    public int SampleRate { get; private set; } = 44_100;

    /// <summary>
    /// Copies the stereo window (up to <paramref name="stereo"/>.Length / 2 frames) that ends where the speakers will
    /// be in <paramref name="ahead"/>. Returns the frames copied; 0 when nothing plays, past the end of the track, or
    /// when the entry cannot be read ahead (a live stream: the caller falls back to the live feed, see
    /// <see cref="CanReadAhead"/>).
    /// </summary>
    public int Read(Span<float> stereo, TimeSpan ahead)
    {
        var now = _playing();
        var entry = now.Entry;
        if (!now.Playing || entry is null || !CanReadAhead(entry))
        {
            return 0;
        }
        if (!ReferenceEquals(entry, _entry))
        {
            Open(entry);
        }
        if (_source is not { } source)
        {
            return 0;
        }
        var rate = source.Format.SampleRate;
        SampleRate = rate;
        var trackTime = now.Position + (ahead * Math.Max(0.25, now.Speed));
        if (trackTime < TimeSpan.Zero || (now.Duration is { } length && trackTime > length))
        {
            return 0;
        }
        var fileStart = entry.Start ?? TimeSpan.Zero;
        var target = source.Format.TimeToFrames(fileStart + trackTime);
        var wanted = Math.Min(stereo.Length / 2, RingFrames / 2);
        var from = Math.Max(0, target - wanted);
        if (from < _start || target > _end + (rate * 2))
        {
            // A jump (seek, first read, backwards): restart decoding at the start of the window.
            if (!source.CanSeek)
            {
                return 0;
            }
            source.Seek(from);
            _start = _end = from;
        }
        while (_end < target)
        {
            if (!Decode(source))
            {
                break; // end of file
            }
        }
        var last = Math.Min(target, _end);
        var frames = (int)Math.Min(wanted, last - _start);
        if (frames <= 0)
        {
            return 0;
        }
        for (var i = 0; i < frames; i++)
        {
            var f = (int)((last - frames + i) % RingFrames);
            stereo[i * 2] = _ring[f * 2];
            stereo[(i * 2) + 1] = _ring[(f * 2) + 1];
        }
        return frames;
    }

    /// <summary>True for entries with a known length (files, CUE tracks); live streams cannot be read ahead.</summary>
    public static bool CanReadAhead(PlaylistEntry entry) =>
        !(entry ?? throw new ArgumentNullException(nameof(entry))).Path.Contains("://", StringComparison.Ordinal);

    private bool Decode(IAudioSource source)
    {
        var channels = source.Format.Channels;
        if (_chunk.Length < ChunkFrames * channels)
        {
            _chunk = new float[ChunkFrames * channels];
        }
        int read;
        try
        {
            read = source.Read(_chunk.AsSpan(0, ChunkFrames * channels));
        }
        catch (AudioSourceException ex)
        {
            _logger.LogDebug(ex, "Look-ahead decoding stopped");
            return false;
        }
        if (read <= 0)
        {
            return false;
        }
        for (var i = 0; i < read; i++)
        {
            var f = (int)((_end + i) % RingFrames);
            var left = _chunk[i * channels];
            _ring[f * 2] = left;
            _ring[(f * 2) + 1] = channels > 1 ? _chunk[(i * channels) + 1] : left;
        }
        _end += read;
        _start = Math.Max(_start, _end - RingFrames);
        return true;
    }

    private void Open(PlaylistEntry entry)
    {
        _source?.Dispose();
        _source = null;
        _entry = entry;
        _start = _end = 0;
        foreach (var factory in _factories.Where(f => f.CanOpen(entry.Path)))
        {
            try
            {
                _source = factory.Open(entry.Path);
                _failed = false;
                return;
            }
            catch (AudioSourceException ex)
            {
                _logger.LogDebug(ex, "{Factory} could not open {Path} for the look-ahead", factory.Name, entry.Path);
            }
        }
        if (!_failed)
        {
            _logger.LogInformation("Cannot read {Path} ahead for the visuals; they follow the speakers' audio instead", entry.Path);
        }
        _failed = true;
    }

    /// <summary>True when the playing entry could not be opened for reading ahead.</summary>
    public bool Failed => _failed;

    public void Dispose()
    {
        _source?.Dispose();
        _source = null;
    }
}
