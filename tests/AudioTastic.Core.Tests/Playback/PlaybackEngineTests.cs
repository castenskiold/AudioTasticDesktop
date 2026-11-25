using AudioTastic.Core.Playback;
using AudioTastic.Core.Tests.Audio;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Audio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Core.Tests.Playback;

/// <summary>CO-02 transport state machine, CO-05 volume, CO-06 position, PB-04 device fallback.</summary>
public sealed class PlaybackEngineTests : IDisposable
{
    private readonly SyntheticFactory _factory = new();
    private readonly FakeAudioOutput _output = new();
    private readonly AppPaths _paths = TempPaths();
    private readonly SettingsStore _settings;
    private readonly PlaybackEngine _engine;

    public PlaybackEngineTests()
    {
        _settings = new SettingsStore(_paths);
        _engine = new PlaybackEngine([_factory], _output, _settings, NullLogger<PlaybackEngine>.Instance);
    }

    public void Dispose()
    {
        _engine.Dispose();
        _output.Dispose();
    }

    private static AppPaths TempPaths()
    {
        var dir = Directory.CreateTempSubdirectory("audiotastic-engine-").FullName;
        return AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
    }

    private static string Track(string name, int rate = 44_100, double seconds = 1, int channels = 2) =>
        TempFiles.Touch(SyntheticFactory.PathFor(name, rate, seconds, channels));

    [Fact]
    public async Task ReplayGain_from_the_provider_sets_the_deck_gain_and_follows_updates()
    {
        // PB-03: the gain is applied before the track starts and updated when the provider says it changed.
        var provider = new FakeGainProvider { Decision = new ReplayGainDecision(-6, 0.5f, ReplayGainSource.TrackTag) };
        _engine.ReplayGainProvider = provider;
        var changes = 0;
        _engine.ReplayGainChanged += (_, _) => Interlocked.Increment(ref changes);

        await _engine.OpenAsync([Track("one", seconds: 5)]);

        Assert.Equal(0.5f, _engine.Graph.DeckA.Gain.TargetGain);
        Assert.Equal(ReplayGainSource.TrackTag, _engine.ReplayGain!.Source);
        Assert.Equal(1, changes);

        provider.Decision = new ReplayGainDecision(3, 1.41f, ReplayGainSource.TrackAnalysis);
        provider.Raise(_engine.CurrentEntry!.Path);
        await Wait.UntilAsync(() => _engine.ReplayGain?.Source == ReplayGainSource.TrackAnalysis);
        Assert.Equal(1.41f, _engine.Graph.DeckA.Gain.TargetGain);

        provider.Decision = null; // e.g. ReplayGain switched off
        provider.Raise(null);
        await Wait.UntilAsync(() => _engine.ReplayGain is null);
        Assert.Equal(1f, _engine.Graph.DeckA.Gain.TargetGain);
        Assert.Equal(3, changes);

        provider.Decision = new ReplayGainDecision(-1, 0.9f, ReplayGainSource.TrackTag);
        provider.Raise("some other file.flac"); // not the playing file: ignored
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Null(_engine.ReplayGain);
    }

    [Fact]
    public void Peak_tap_records_min_and_max_per_10_ms()
    {
        var tap = new AudioTastic.Core.Audio.PeakTap();
        tap.Configure(1000); // 10 frames per pair
        var samples = new float[40]; // 20 stereo frames: two pairs
        samples[3] = 0.8f;
        samples[30] = -0.5f;
        tap.Write(samples, 2);

        var min = new float[10];
        var max = new float[10];
        Assert.Equal(2, tap.ReadLatest(min, max));
        Assert.Equal(0.8f, max[0]);
        Assert.Equal(-0.5f, min[1]);
        Assert.Equal(2, tap.Written);
    }

    private sealed class FakeGainProvider : IReplayGainProvider
    {
        public ReplayGainDecision? Decision { get; set; }
        public event EventHandler<ReplayGainChangedEventArgs>? GainChanged;
        public ReplayGainDecision? GetGain(AudioTastic.Core.Playlists.Playlist playlist, AudioTastic.Core.Playlists.PlaylistEntry entry) => Decision;
        public void Raise(string? path) => GainChanged?.Invoke(this, new ReplayGainChangedEventArgs(path));
    }

    [Fact]
    public async Task Stream_title_changes_update_the_current_track()
    {
        // FM-10: a live stream starts with the station name; ICY titles replace it while it plays.
        var radio = new FakeRadio();
        using var output = new FakeAudioOutput();
        using var engine = new PlaybackEngine([new FakeRadioFactory(radio)], output, _settings, NullLogger<PlaybackEngine>.Instance);
        var titles = new List<string>();
        engine.TrackChanged += (_, e) =>
        {
            if (e.Track is { } t)
            {
                lock (titles)
                {
                    titles.Add(t.Title);
                }
            }
        };

        await engine.OpenAsync(["http://radio.test/live"]);
        Assert.Equal("Test FM", engine.CurrentTrack!.Title);
        Assert.Null(engine.Duration);

        radio.SetTitle("Artist - Song");

        Assert.Equal("Artist - Song", engine.CurrentTrack!.Title);
        Assert.Equal("Test FM", engine.CurrentTrack.Artist);
        lock (titles)
        {
            Assert.Equal(["Test FM", "Artist - Song"], titles);
        }
        await engine.StopAsync();
    }

    [Fact]
    public async Task Files_keep_their_duration_even_when_the_source_offers_stream_metadata()
    {
        // Regression (P4): only sources without a length are treated as live streams.
        var file = new FakeRadio(lengthFrames: 44_100 * 3);
        using var output = new FakeAudioOutput();
        using var engine = new PlaybackEngine([new FakeRadioFactory(file)], output, _settings, NullLogger<PlaybackEngine>.Instance);

        await engine.OpenAsync(["http://radio.test/file.mp3"]);

        Assert.Equal(TimeSpan.FromSeconds(3), engine.Duration);
        Assert.NotEqual("Test FM", engine.CurrentTrack!.Title);
        await engine.StopAsync();
    }

    private sealed class FakeRadio(long? lengthFrames = null) : IAudioSource, IStreamMetadata
    {
        private readonly SyntheticSource _signal = new(44_100, 2, long.MaxValue);

        public AudioStreamInfo Info => _signal.Info;
        public AudioFormat Format => _signal.Format;
        public long? LengthFrames => lengthFrames;
        public long PositionFrames => _signal.PositionFrames;
        public bool CanSeek => false;
        public IReadOnlyDictionary<string, string> Tags => new Dictionary<string, string>();
        public string? StreamTitle { get; private set; }
        public string? StationName => "Test FM";
        public event EventHandler<string?>? StreamTitleChanged;

        public void SetTitle(string title)
        {
            StreamTitle = title;
            StreamTitleChanged?.Invoke(this, title);
        }

        public int Read(Span<float> buffer) => _signal.Read(buffer);
        public void Seek(long frame) => throw new NotSupportedException();
        public void Dispose()
        {
        }
    }

    private sealed class FakeRadioFactory(FakeRadio radio) : IAudioSourceFactory
    {
        public string Name => "Radio";
        public IReadOnlyCollection<string> Extensions => [];
        public bool CanOpen(string path) => path.StartsWith("http://", StringComparison.Ordinal);
        public IAudioSource Open(string path) => radio;
    }

    [Fact]
    public async Task Open_plays_the_first_track_at_its_sample_rate()
    {
        TrackChangedEventArgs? changed = null;
        _engine.TrackChanged += (_, e) => changed = e;

        await _engine.OpenAsync([Track("one", 48_000), Track("two")]);

        Assert.Equal(PlaybackState.Playing, _engine.State);
        Assert.Equal(0, _engine.CurrentIndex);
        Assert.Equal("one", _engine.CurrentTrack!.Title);
        Assert.Equal(TimeSpan.FromSeconds(1), _engine.Duration);
        Assert.Equal(new AudioFormat(48_000, 2), _output.Format);
        Assert.True(_output.IsRunning);
        Assert.Equal(0, changed!.Index);
    }

    [Fact]
    public async Task Track_end_advances_to_the_next_track_and_stops_after_the_last()
    {
        var states = new List<PlaybackState>();
        _engine.StateChanged += (_, e) => { lock (states) { states.Add(e.State); } };

        await _engine.OpenAsync([Track("a", seconds: 0.2), Track("b", seconds: 0.2)]);
        await Wait.UntilAsync(() => _engine.CurrentIndex == 1);
        await Wait.UntilAsync(() => _engine.State == PlaybackState.Stopped);

        Assert.Equal(2, _factory.Opened.Count);
        Assert.All(_factory.Opened, s => Assert.True(s.Disposed));
        Assert.Equal(TimeSpan.Zero, _engine.Position);
    }

    [Fact]
    public async Task Pause_toggles_and_holds_the_position()
    {
        await _engine.OpenAsync([Track("long", seconds: 30)]);
        await Wait.UntilAsync(() => _engine.Position > TimeSpan.FromMilliseconds(50));

        await _engine.PauseAsync();
        var held = _engine.Position;
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Equal(PlaybackState.Paused, _engine.State);
        Assert.Equal(held, _engine.Position);

        await _engine.PauseAsync();
        Assert.Equal(PlaybackState.Playing, _engine.State);
        await Wait.UntilAsync(() => _engine.Position > held);
    }

    [Fact]
    public async Task Seek_lands_on_the_exact_frame()
    {
        await _engine.OpenAsync([Track("long", seconds: 30)]);
        await _engine.PauseAsync();

        await _engine.SeekAsync(TimeSpan.FromSeconds(12.345));

        Assert.Equal((long)Math.Round(12.345 * 44_100), _engine.Graph.DeckA.PositionFrames);
    }

    [Fact]
    public async Task Previous_restarts_after_three_seconds_otherwise_goes_back()
    {
        await _engine.OpenAsync([Track("first", seconds: 30), Track("second", seconds: 30)]);
        await _engine.NextAsync();
        Assert.Equal(1, _engine.CurrentIndex);

        await _engine.SeekAsync(TimeSpan.FromSeconds(10));
        await _engine.PreviousAsync();
        Assert.Equal(1, _engine.CurrentIndex);
        Assert.True(_engine.Position < TimeSpan.FromSeconds(3));

        await _engine.PreviousAsync();
        Assert.Equal(0, _engine.CurrentIndex);
        Assert.Equal(PlaybackState.Playing, _engine.State);
    }

    [Fact]
    public async Task Stop_resets_to_the_start_and_play_starts_again()
    {
        await _engine.OpenAsync([Track("long", seconds: 30)]);
        await _engine.SeekAsync(TimeSpan.FromSeconds(5));

        await _engine.StopAsync();
        Assert.Equal(PlaybackState.Stopped, _engine.State);
        Assert.Equal(TimeSpan.Zero, _engine.Position);
        Assert.False(_output.IsRunning);

        await _engine.PlayAsync();
        Assert.Equal(PlaybackState.Playing, _engine.State);
        Assert.True(_engine.Position < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_new_sample_rate_reopens_the_output()
    {
        await _engine.OpenAsync([Track("cd", 44_100, 30), Track("hires", 96_000, 30)]);

        await _engine.NextAsync();

        Assert.Equal(new AudioFormat(96_000, 2), _output.Format);
        Assert.Equal([44_100, 96_000], _output.Opens.Select(o => o.Format.SampleRate));
    }

    [Fact]
    public async Task Unplayable_files_are_reported_and_skipped()
    {
        var errors = new List<string>();
        _engine.PlaybackError += (_, e) => errors.Add(Path.GetFileName(e.Path));

        await _engine.OpenAsync([Track("bad"), Path.Combine(TempFiles.Folder, "missing.syn"), Track("good", seconds: 30)]);

        Assert.Equal(2, _engine.CurrentIndex);
        Assert.Equal(PlaybackState.Playing, _engine.State);
        Assert.Equal(2, errors.Count);
    }

    [Fact]
    public async Task Nothing_playable_ends_stopped()
    {
        await _engine.OpenAsync([Track("bad")]);

        Assert.Equal(PlaybackState.Stopped, _engine.State);
    }

    [Fact]
    public async Task Volume_and_mute_set_the_master_gain_and_are_saved()
    {
        _engine.Volume = 50;
        Assert.Equal(0.125f, _engine.Graph.Master.TargetGain, 6);
        _engine.IsMuted = true;
        Assert.Equal(0f, _engine.Graph.Master.TargetGain);
        _engine.IsMuted = false;
        await Wait.UntilAsync(() => new SettingsStore(_paths).Current.Playback.Volume == 50);

        Assert.False(new SettingsStore(_paths).Current.Playback.Muted);
    }

    [Fact]
    public async Task Position_events_arrive_at_about_10_hz()
    {
        var count = 0;
        _engine.PositionChanged += (_, _) => Interlocked.Increment(ref count);
        await _engine.OpenAsync([Track("long", seconds: 30)]);

        await Task.Delay(550, TestContext.Current.CancellationToken);

        Assert.InRange(count, 3, 8);
    }

    [Fact]
    public async Task Chosen_device_is_used_and_lost_device_falls_back_to_default()
    {
        await _engine.SetOutputDeviceAsync("Headphones");
        await _engine.OpenAsync([Track("long", seconds: 30)]);
        Assert.Equal("Headphones", _output.Opens[^1].Device);

        _output.Lose();
        await Wait.UntilAsync(() => _output.Opens.Count == 2 && _output.IsRunning);

        Assert.Null(_output.Opens[^1].Device); // default device
        Assert.Equal(PlaybackState.Playing, _engine.State);
        Assert.Equal("Headphones", _settings.Current.Output.DeviceName); // the choice is kept for next time
    }

    [Fact]
    public async Task With_no_device_left_playback_pauses_and_continues_when_one_appears()
    {
        // P14 soak: a Remote Desktop disconnect removed the only device; playback stopped and lost its place.
        await _engine.OpenAsync([Track("long", seconds: 30)]);
        await Wait.UntilAsync(() => _engine.Position > TimeSpan.FromMilliseconds(300));

        _output.NoDevice = true;
        _output.Lose();
        await Wait.UntilAsync(() => _engine.State == PlaybackState.Paused);
        var where = _engine.Position;
        Assert.True(where > TimeSpan.Zero);
        Assert.NotNull(_engine.CurrentTrack); // still loaded

        await _engine.PlayAsync(); // still no device: stays paused
        Assert.Equal(PlaybackState.Paused, _engine.State);

        _output.NoDevice = false;
        _output.RaiseDevicesChanged();
        await Wait.UntilAsync(() => _engine.State == PlaybackState.Playing && _output.IsRunning);
        Assert.True(_engine.Position >= where - TimeSpan.FromMilliseconds(50)); // from where it was, not the start
    }

    [Fact]
    public async Task Stopping_while_waiting_for_a_device_ends_the_wait()
    {
        await _engine.OpenAsync([Track("long", seconds: 30)]);
        _output.NoDevice = true;
        _output.Lose();
        await Wait.UntilAsync(() => _engine.State == PlaybackState.Paused);
        await _engine.StopAsync();

        _output.NoDevice = false;
        _output.RaiseDevicesChanged();
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Equal(PlaybackState.Stopped, _engine.State); // a stop is the user's choice: nothing resumes
    }

    [Fact]
    public async Task Missing_saved_device_falls_back_to_default()
    {
        _settings.Update(SettingsGroup.Output, s => s.Output.DeviceName = "Gone");

        await _engine.OpenAsync([Track("long", seconds: 30)]);

        Assert.Null(_output.Opens[^1].Device);
        Assert.Equal(PlaybackState.Playing, _engine.State);
    }

    [Fact]
    public async Task Output_carries_the_source_audio_unchanged()
    {
        _engine.Volume = 100;
        _engine.Graph.Master.SnapToTarget(); // skip the 10 ms volume ramp for an exact comparison
        _output.Capture = true;
        await _engine.OpenAsync([Track("check", seconds: 0.5)]);
        await Wait.UntilAsync(() => _engine.State == PlaybackState.Stopped);

        var audio = _output.Captured;
        var start = Array.FindIndex(audio, s => s != 0f);
        // The first non-zero sample is frame 0 or 1 of the synthetic signal (frame 0 / channel 0 may be exactly zero).
        var offset = SyntheticSource.ValueAt(0, 0) == 0f ? start - 1 : start;
        for (var f = 0; f < 22_050; f += 97)
        {
            Assert.Equal(SyntheticSource.ValueAt(f, 0), audio[offset + (2 * f)]);
        }
    }

    [Fact]
    public async Task Repeat_track_replays_the_track_at_its_end()
    {
        _engine.Repeat = RepeatMode.Track;
        await Wait.UntilAsync(() => _engine.Repeat == RepeatMode.Track);

        await _engine.OpenAsync([Track("loop", seconds: 0.2), Track("other", seconds: 0.2)]);
        await Wait.UntilAsync(() => _factory.Opened.Count >= 3);

        Assert.All(_factory.Opened, s => Assert.Equal(44_100 * 0.2, s.LengthFrames!.Value, 0));
        Assert.Equal(0, _engine.CurrentIndex);
        Assert.Equal(RepeatMode.Track, new SettingsStore(_paths).Current.Playback.Repeat);
    }

    [Fact]
    public async Task Repeat_playlist_wraps_to_the_first_track()
    {
        _engine.Repeat = RepeatMode.Playlist;
        var indexes = new List<int>();
        _engine.TrackChanged += (_, e) => { lock (indexes) { indexes.Add(e.Index); } };

        await _engine.OpenAsync([Track("a", seconds: 0.2), Track("b", seconds: 0.2)]);
        await Wait.UntilAsync(() => { lock (indexes) { return indexes.Count >= 3; } });

        lock (indexes)
        {
            Assert.Equal([0, 1, 0], indexes.Take(3));
        }
    }

    [Fact]
    public async Task Shuffle_is_saved_and_next_follows_the_shuffled_order()
    {
        _engine.Shuffle = true;
        await Wait.UntilAsync(() => _engine.Shuffle);
        var tracks = Enumerable.Range(0, 8).Select(i => Track($"s{i}", seconds: 30)).ToArray();

        await _engine.OpenAsync(tracks);
        var seen = new HashSet<int> { _engine.CurrentIndex };
        for (var i = 0; i < 7; i++)
        {
            await _engine.NextAsync();
            seen.Add(_engine.CurrentIndex);
        }

        Assert.Equal(8, seen.Count); // every track once before any repeats
        Assert.True(new SettingsStore(_paths).Current.Playback.Shuffle);
    }
}