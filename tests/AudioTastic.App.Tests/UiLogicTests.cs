using AudioTastic.App.Ui;
using AudioTastic.Core.Playback;
using AudioTastic.Sdk.Audio;
using Avalonia;
using Avalonia.Input;

namespace AudioTastic.App.Tests;

/// <summary>CO-06 time display, UI-06 title and indicators.</summary>
public sealed class DisplayTextTests
{
    [Theory]
    [InlineData(0, "0:00")]
    [InlineData(65, "1:05")]
    [InlineData(3599, "59:59")]
    [InlineData(3600, "1:00:00")]
    [InlineData(-3, "0:00")]
    public void Time_is_m_ss_or_h_mm_ss(int seconds, string expected) =>
        Assert.Equal(expected, DisplayText.Time(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void Remaining_time_has_a_minus_and_streams_always_show_elapsed()
    {
        Assert.Equal("-3:41", DisplayText.TimeDisplay(TimeSpan.FromSeconds(91), TimeSpan.FromSeconds(312), remaining: true));
        Assert.Equal("1:31", DisplayText.TimeDisplay(TimeSpan.FromSeconds(91), null, remaining: true));
    }

    [Fact]
    public void Title_is_artist_dash_title_with_duration()
    {
        var track = new TrackInfo("a.flac", "Track Title", "Artist Name", Stream("flac", true, 16), TimeSpan.FromSeconds(312));

        Assert.Equal("Artist Name – Track Title (5:12)", DisplayText.Title(track));
        Assert.Equal("Solo", DisplayText.Title(track with { Title = "Solo", Artist = null, Duration = null }));
    }

    [Fact]
    public void Indicators_show_codec_depth_bitrate_rate_and_channels()
    {
        var flac = new TrackInfo("x.flac", "t", null, Stream("flac", true, 24, 96_000, 2, 2_304_000), null);
        var mp3 = new TrackInfo("x.mp3", "t", null, Stream("mp3float", false, 0, 44_100, 1, 320_000), null);
        var wav = new TrackInfo("x.aif", "t", null, Stream("pcm_s16be", true, 16, 44_100, 6, 0), null);

        Assert.Equal(["FLAC", "24 BIT", "2304 KBPS", "96 KHZ", "STEREO"], DisplayText.Indicators(flac));
        Assert.Equal(["MP3", "320 KBPS", "44.1 KHZ", "MONO"], DisplayText.Indicators(mp3));
        Assert.Equal(["AIFF", "16 BIT", "44.1 KHZ", "5.1"], DisplayText.Indicators(wav));
        Assert.Empty(DisplayText.Indicators(null));
    }

    [Fact]
    public void State_badges_show_output_speed_loop_stop_and_sleep()
    {
        Assert.Empty(DisplayText.StateBadges(new PlayerBadges(1.0, false, false, null, false)));
        Assert.Equal(["OUT", "1.25×", "A-B", "STOP AFTER", "SLEEP 30 MIN"],
            DisplayText.StateBadges(new PlayerBadges(1.25, true, true, 30, true)));
        Assert.Equal(["SLEEP"], DisplayText.StateBadges(new PlayerBadges(1.0, false, false, 0, false))); // end of track/playlist
    }

    private static AudioStreamInfo Stream(string codec, bool lossless, int bits, int rate = 44_100, int channels = 2, long bitrate = 0) =>
        new(codec, rate, channels, bits, bitrate, lossless);
}

/// <summary>UI-01 snapping and docking.</summary>
public sealed class SnapGeometryTests
{
    private static readonly PixelRect Screen = new(0, 0, 1920, 1080);

    [Fact]
    public void Snaps_to_a_screen_edge_within_10_px()
    {
        var moving = new PixelRect(7, 500, 330, 128);

        var offset = SnapGeometry.Snap(moving, [], [Screen], 10);

        Assert.Equal(new PixelVector(-7, 0), offset);
    }

    [Fact]
    public void Does_not_snap_beyond_the_distance()
    {
        var moving = new PixelRect(30, 500, 330, 128);

        Assert.Equal(default, SnapGeometry.Snap(moving, [], [Screen], 10));
    }

    [Fact]
    public void Snaps_below_another_window_and_aligns_left_edges()
    {
        var main = new PixelRect(400, 300, 330, 128);
        var moving = new PixelRect(404, 435, 330, 104); // 7 px below, 4 px right

        var offset = SnapGeometry.Snap(moving, [main], [Screen], 10);

        Assert.Equal(new PixelVector(-4, -7), offset);
    }

    [Fact]
    public void Ignores_windows_that_are_far_away_on_the_other_axis()
    {
        var other = new PixelRect(400, 900, 330, 128);
        var moving = new PixelRect(735, 100, 330, 128); // right of 'other' horizontally, but far above

        Assert.Equal(default, SnapGeometry.Snap(moving, [other], [], 10));
    }

    [Fact]
    public void Resizing_snaps_the_right_edge_to_the_player_above()
    {
        var player = new PixelRect(400, 300, 495, 201);
        var playlist = new PixelRect(400, 501, 488, 348); // right edge 7 px short of the player's

        Assert.Equal(new PixelVector(7, 0), SnapGeometry.SnapResize(playlist, [player], [Screen], 10));
        Assert.Equal(new PixelVector(-6, 0), SnapGeometry.SnapResize(new PixelRect(400, 501, 501, 348), [player], [Screen], 10));
        Assert.Equal(default, SnapGeometry.SnapResize(new PixelRect(400, 501, 470, 348), [player], [Screen], 10)); // a free resize
    }

    [Fact]
    public void Resizing_snaps_the_bottom_edge_to_a_window_or_the_screen()
    {
        var art = new PixelRect(895, 300, 300, 540);
        var playlist = new PixelRect(400, 501, 495, 334); // bottom 5 px above the art window's bottom

        Assert.Equal(new PixelVector(0, 5), SnapGeometry.SnapResize(playlist, [art], [Screen], 10));
        Assert.Equal(new PixelVector(0, 4), SnapGeometry.SnapResize(new PixelRect(100, 700, 300, 376), [], [Screen], 10));
    }

    [Fact]
    public void Resizing_ignores_windows_far_away_on_the_other_axis()
    {
        var far = new PixelRect(400, 900, 495, 100);
        var art = new PixelRect(1000, 100, 300, 300);

        Assert.Equal(default, SnapGeometry.SnapResize(new PixelRect(400, 100, 498, 300), [far], [], 10));
        Assert.Equal(new PixelVector(-3, 0), SnapGeometry.SnapResize(new PixelRect(400, 100, 603, 300), [art], [], 10)); // touch on the right
    }

    [Fact]
    public void Docked_group_follows_touching_edges_transitively()
    {
        var main = new PixelRect(100, 100, 330, 128);
        PixelRect[] others =
        [
            new(100, 228, 330, 104),   // equalizer below main
            new(100, 332, 330, 200),   // playlist below equalizer
            new(430, 100, 300, 300),   // art window right of main
            new(900, 900, 100, 100),   // somewhere else
        ];

        var group = SnapGeometry.DockedGroup(main, others);

        Assert.Equal([0, 2, 1], group);
    }
}

/// <summary>Feature Specification 2.4 default keys.</summary>
public sealed class KeyMapTests
{
    [Theory]
    [InlineData(Key.Z, KeyModifiers.None, "previous")]
    [InlineData(Key.X, KeyModifiers.None, "play")]
    [InlineData(Key.C, KeyModifiers.None, "pause")]
    [InlineData(Key.V, KeyModifiers.None, "stop")]
    [InlineData(Key.B, KeyModifiers.None, "next")]
    [InlineData(Key.L, KeyModifiers.None, "open")]
    [InlineData(Key.S, KeyModifiers.None, "shuffle")]
    [InlineData(Key.R, KeyModifiers.None, "repeat")]
    [InlineData(Key.W, KeyModifiers.Control, "miniPlayer")]
    [InlineData(Key.D, KeyModifiers.Control, "doubleSize")]
    [InlineData(Key.P, KeyModifiers.Control, "settings")]
    public void Winamp_defaults(Key key, KeyModifiers modifiers, string action) =>
        Assert.Equal(action, KeyMap.ActionFor(key, modifiers));

    [Fact]
    public void Unbound_keys_do_nothing() => Assert.Null(KeyMap.ActionFor(Key.F9, KeyModifiers.None));
}
