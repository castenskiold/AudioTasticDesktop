using AudioTastic.App.Integration;
using AudioTastic.Core.Playback;
using AudioTastic.Platform;

namespace AudioTastic.App.Tests;

/// <summary>SI-03 taskbar progress.</summary>
public sealed class TaskbarTests
{
    private static readonly TimeSpan Length = TimeSpan.FromMinutes(4);

    [Fact]
    public void Progress_is_green_while_playing_and_yellow_when_paused()
    {
        Assert.Equal((TaskbarProgress.Normal, 0.25), TaskbarService.ProgressFor(PlaybackState.Playing, TimeSpan.FromMinutes(1), Length, enabled: true));
        Assert.Equal((TaskbarProgress.Normal, 0.5), TaskbarService.ProgressFor(PlaybackState.Buffering, TimeSpan.FromMinutes(2), Length, enabled: true));
        Assert.Equal((TaskbarProgress.Paused, 0.75), TaskbarService.ProgressFor(PlaybackState.Paused, TimeSpan.FromMinutes(3), Length, enabled: true));
        Assert.Equal(1.0, TaskbarService.ProgressFor(PlaybackState.Playing, TimeSpan.FromMinutes(5), Length, enabled: true).Fraction);
    }

    [Fact]
    public void No_progress_when_stopped_for_streams_or_when_switched_off()
    {
        Assert.Equal((TaskbarProgress.None, 0.0), TaskbarService.ProgressFor(PlaybackState.Stopped, TimeSpan.FromMinutes(1), Length, enabled: true));
        Assert.Equal((TaskbarProgress.None, 0.0), TaskbarService.ProgressFor(PlaybackState.Playing, TimeSpan.FromMinutes(1), null, enabled: true));
        Assert.Equal((TaskbarProgress.None, 0.0), TaskbarService.ProgressFor(PlaybackState.Playing, TimeSpan.FromMinutes(1), Length, enabled: false));
    }
}
