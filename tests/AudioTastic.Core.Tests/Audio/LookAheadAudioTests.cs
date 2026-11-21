using AudioTastic.Core.Audio;
using AudioTastic.Core.Playlists;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Core.Tests.Audio;

/// <summary>CN-09 video only: the look-ahead reads the audio the speakers will play after the TV's delay.</summary>
public sealed class LookAheadAudioTests
{
    private readonly SyntheticFactory _factory = new();
    private PlayingPoint _now = new(null, false, TimeSpan.Zero, null, 1);

    private LookAheadAudio Create() => new(() => _now, [_factory], NullLogger.Instance);

    private static PlaylistEntry Entry(string name = "t", double seconds = 30, TimeSpan? start = null, TimeSpan? end = null) =>
        new(TempFiles.Touch(SyntheticFactory.PathFor(name, seconds: seconds)), start, end);

    /// <summary>The window ends at file frame <paramref name="lastFrame"/> (exclusive): its samples match the synthetic signal.</summary>
    private static void AssertWindowEndsAt(float[] stereo, int frames, long lastFrame)
    {
        for (var i = 0; i < frames; i++)
        {
            var frame = lastFrame - frames + i;
            Assert.Equal(SyntheticSource.ValueAt(frame, 0), stereo[i * 2]);
            Assert.Equal(SyntheticSource.ValueAt(frame, 1), stereo[(i * 2) + 1]);
        }
    }

    [Fact]
    public void Window_ends_where_the_speakers_will_be_after_the_delay_and_follows_on()
    {
        using var look = Create();
        var entry = Entry();
        var stereo = new float[2048 * 2];
        for (var second = 1.0; second < 6; second += 1.0 / 30)
        {
            _now = new PlayingPoint(entry, true, TimeSpan.FromSeconds(second), TimeSpan.FromSeconds(30), 1);
            var frames = look.Read(stereo, TimeSpan.FromSeconds(3));
            Assert.Equal(2048, frames);
            AssertWindowEndsAt(stereo, frames, (long)Math.Round((second + 3) * 44_100));
        }
        Assert.Single(_factory.Opened); // one decoder, read on sequentially
    }

    [Fact]
    public void Seeks_backwards_and_far_ahead_restart_the_decoder_position()
    {
        using var look = Create();
        var entry = Entry();
        var stereo = new float[2048 * 2];
        foreach (var second in new[] { 10.0, 2.0, 20.0, 0.0 })
        {
            _now = new PlayingPoint(entry, true, TimeSpan.FromSeconds(second), TimeSpan.FromSeconds(30), 1);
            var frames = look.Read(stereo, TimeSpan.FromSeconds(2));
            Assert.Equal(2048, frames);
            AssertWindowEndsAt(stereo, frames, (long)Math.Round((second + 2) * 44_100));
        }
    }

    [Fact]
    public void Cue_tracks_read_from_their_place_in_the_file_and_speed_scales_the_delay()
    {
        using var look = Create();
        var entry = Entry(start: TimeSpan.FromSeconds(10), end: TimeSpan.FromSeconds(25));
        var stereo = new float[1024 * 2];
        _now = new PlayingPoint(entry, true, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(15), 1.5);
        var frames = look.Read(stereo, TimeSpan.FromSeconds(2));
        AssertWindowEndsAt(stereo, frames, (long)Math.Round((10 + 1 + (2 * 1.5)) * 44_100));
    }

    [Fact]
    public void Nothing_while_paused_past_the_end_or_for_live_streams()
    {
        using var look = Create();
        var stereo = new float[2048 * 2];
        var entry = Entry(seconds: 5);
        _now = new PlayingPoint(entry, false, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), 1);
        Assert.Equal(0, look.Read(stereo, TimeSpan.FromSeconds(1))); // paused
        _now = _now with { Playing = true, Position = TimeSpan.FromSeconds(4) };
        Assert.Equal(0, look.Read(stereo, TimeSpan.FromSeconds(3))); // beyond the end
        _now = new PlayingPoint(new PlaylistEntry("http://radio.example/stream"), true, TimeSpan.FromSeconds(1), null, 1);
        Assert.Equal(0, look.Read(stereo, TimeSpan.FromSeconds(3)));
        Assert.False(LookAheadAudio.CanReadAhead(_now.Entry!));
    }

    [Fact]
    public void A_new_track_opens_a_new_decoder()
    {
        using var look = Create();
        var stereo = new float[512 * 2];
        _now = new PlayingPoint(Entry("a"), true, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 1);
        look.Read(stereo, TimeSpan.FromSeconds(1));
        _now = new PlayingPoint(Entry("b"), true, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(30), 1);
        look.Read(stereo, TimeSpan.FromSeconds(1));
        Assert.Equal(2, _factory.Opened.Count);
        Assert.True(_factory.Opened[0].Disposed);
    }
}
