using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using AudioTastic.Core.Tests.Audio;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Core.Tests.Playback;

/// <summary>PB-08 A-B repeat, PB-09 queue and stop after current, PB-10 sleep timer.</summary>
public sealed class TransportExtrasTests : IDisposable
{
    private readonly FakeAudioOutput _output = new() { Capture = true };
    private readonly SettingsStore _settings;
    private readonly PlaybackEngine _engine;

    public TransportExtrasTests()
    {
        var dir = Directory.CreateTempSubdirectory("audiotastic-extras-").FullName;
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        _settings = new SettingsStore(paths);
        _settings.Update(SettingsGroup.Playback, s => s.Playback.Volume = 100);
        _engine = new PlaybackEngine([new SyntheticFactory()], _output, _settings, NullLogger<PlaybackEngine>.Instance);
    }

    public void Dispose()
    {
        _engine.Dispose();
        _output.Dispose();
    }

    private static PlaylistEntry Entry(string name, double seconds = 0.5) =>
        new(TempFiles.Touch(SyntheticFactory.PathFor(name, seconds: seconds)));

    [Fact]
    public async Task Queued_entries_play_before_the_play_order_even_from_another_playlist()
    {
        var main = new Playlist("main", [Entry("m1"), Entry("m2"), Entry("m3")]);
        var other = new Playlist("other", [Entry("o1")]);
        var played = new List<string>();
        _engine.TrackChanged += (_, e) =>
        {
            if (e.Track is { } t)
            {
                lock (played)
                {
                    played.Add(t.Title);
                }
            }
        };
        _engine.Queue.Enqueue(main, [main.Entries[2]]);
        _engine.Queue.Enqueue(other, [other.Entries[0]]);
        Assert.Equal([1], _engine.Queue.PositionsOf(main.Entries[2]));

        await _engine.PlayAsync(main, main.Entries[0]);
        await Wait.UntilAsync(() => _engine.State == PlaybackState.Stopped, 15_000);

        lock (played)
        {
            Assert.Equal(["m1", "m3", "o1"], played); // queue first; o1 is the last entry of 'other'
        }
        Assert.Equal(0, _engine.Queue.Count);
        Assert.Same(other, _engine.PlayingPlaylist);
    }

    [Fact]
    public async Task Stop_after_current_stops_once_then_switches_itself_off()
    {
        var list = new Playlist("l", [Entry("a"), Entry("b")]);
        await _engine.PlayAsync(list, list.Entries[0]);
        _engine.StopAfterCurrent = true;
        await Wait.UntilAsync(() => _engine.StopAfterCurrent);

        await Wait.UntilAsync(() => _engine.State == PlaybackState.Stopped, 15_000);

        Assert.Equal("a", _engine.CurrentTrack!.Title);
        Assert.False(_engine.StopAfterCurrent);
        Assert.Equal(0, _engine.Graph.Handovers); // no gapless preload while set
    }

    [Fact]
    public async Task A_B_repeat_loops_the_section_seamlessly()
    {
        var list = new Playlist("l", [Entry("loop", seconds: 30)]);
        await _engine.PlayAsync(list, list.Entries[0]);

        await _engine.SetAbLoopAsync(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        Assert.Equal(new AbLoop(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)), _engine.AbLoop);
        await Wait.UntilAsync(() => _output.Captured.Length > 44_100 * 2 * 5, 20_000);

        // The position stays inside the loop, and the audio after B continues with A's samples.
        Assert.InRange(_engine.Position, TimeSpan.FromSeconds(0.99), TimeSpan.FromSeconds(2.01));
        // B-1 directly followed by A cannot happen without the loop (the synthetic signal repeats every 2,001 frames,
        // and B - A = 44,100 is not a multiple of that); after the wrap the audio continues from A.
        var captured = _output.Captured;
        var wrap = FindWrap(captured, 88_199, 44_100);
        for (var f = 0; f < 2000; f++)
        {
            Assert.Equal(SyntheticSource.ValueAt(44_100 + f, 0), captured[wrap + 2 + (f * 2)]);
        }

        await _engine.ClearAbLoopAsync();
        Assert.Null(_engine.AbLoop);
        await _engine.StopAsync();
    }

    [Fact]
    public async Task A_B_key_cycles_a_then_loop_then_off()
    {
        var list = new Playlist("l", [Entry("keys", seconds: 30)]);
        await _engine.PlayAsync(list, list.Entries[0]);

        await _engine.AbRepeatAsync();
        Assert.NotNull(_engine.AbLoop);
        Assert.Null(_engine.AbLoop!.B);
        await _engine.AbRepeatAsync();
        Assert.NotNull(_engine.AbLoop!.B);
        Assert.True(_engine.AbLoop.B - _engine.AbLoop.A >= TimeSpan.FromSeconds(1));
        await _engine.AbRepeatAsync();
        Assert.Null(_engine.AbLoop);
        await _engine.StopAsync();
    }

    [Fact]
    public async Task Sleep_timer_stops_after_the_time_and_raises_finished()
    {
        _settings.Update(SettingsGroup.Playback, s => s.Playback.SleepTimer.FadeSeconds = 1);
        using var sleep = new SleepTimerService(_engine, _settings, NullLogger<SleepTimerService>.Instance);
        var finished = new TaskCompletionSource();
        sleep.Finished += (_, _) => finished.TrySetResult();
        var list = new Playlist("l", [Entry("sleep", seconds: 60)]);
        await _engine.PlayAsync(list, list.Entries[0]);

        sleep.StartAfter(TimeSpan.FromSeconds(1.5));
        Assert.Equal(SleepMode.AfterTime, sleep.Mode);
        await finished.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        Assert.Equal(PlaybackState.Stopped, _engine.State);
        Assert.Null(sleep.Mode);
    }

    [Fact]
    public async Task Sleep_timer_at_end_of_track_uses_stop_after_current_and_cancel_undoes_it()
    {
        using var sleep = new SleepTimerService(_engine, _settings, NullLogger<SleepTimerService>.Instance);
        var list = new Playlist("l", [Entry("x", seconds: 30), Entry("y")]);
        await _engine.PlayAsync(list, list.Entries[0]);

        sleep.StartAtEndOfTrack();
        await Wait.UntilAsync(() => _engine.StopAfterCurrent);
        Assert.NotNull(sleep.Remaining);
        sleep.Cancel();
        await Wait.UntilAsync(() => !_engine.StopAfterCurrent);
        Assert.Null(sleep.Mode);
        await _engine.StopAsync();
    }

    private static int FindWrap(float[] captured, long last, long first)
    {
        for (var i = 0; i + 3 < captured.Length; i += 2)
        {
            if (captured[i] == SyntheticSource.ValueAt(last, 0) && captured[i + 1] == SyntheticSource.ValueAt(last, 1) &&
                captured[i + 2] == SyntheticSource.ValueAt(first, 0) && captured[i + 3] == SyntheticSource.ValueAt(first, 1))
            {
                return i;
            }
        }
        throw new InvalidOperationException("No loop wrap found in the captured audio.");
    }
}
