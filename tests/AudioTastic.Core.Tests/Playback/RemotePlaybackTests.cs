using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using AudioTastic.Core.Tests.Audio;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Core.Tests.Playback;

/// <summary>CN-01 / CN-02: the engine playing through a cast device (remote mode).</summary>
public sealed class RemotePlaybackTests : IDisposable
{
    private readonly FakeAudioOutput _output = new();
    private readonly SettingsStore _settings;
    private readonly PlaybackEngine _engine;
    private readonly FakeRemote _remote = new();

    public RemotePlaybackTests()
    {
        var dir = Directory.CreateTempSubdirectory("audiotastic-remote-").FullName;
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

    private static PlaylistEntry Entry(string name, double seconds = 30) =>
        new(TempFiles.Touch(SyntheticFactory.PathFor(name, seconds: seconds)));

    [Fact]
    public async Task Casting_moves_the_playing_track_to_the_device_at_its_position_and_back()
    {
        var list = new Playlist("l", [Entry("a"), Entry("b")]);
        await _engine.PlayAsync(list, list.Entries[0]);
        await Wait.UntilAsync(() => _engine.Position > TimeSpan.FromMilliseconds(300));

        await _engine.SetRemoteAsync(_remote);

        var load = Assert.Single(_remote.Loads);
        Assert.Same(list.Entries[0], load.Entry);
        Assert.InRange(load.Start.TotalSeconds, 0.25, 3);
        Assert.False(load.Paused);
        Assert.Equal(PlaybackState.Playing, _engine.State);
        Assert.Same(_remote, _engine.Remote);

        _remote.Position = TimeSpan.FromSeconds(12);
        Assert.Equal(TimeSpan.FromSeconds(12), _engine.Position); // the device's position

        await _engine.SetRemoteAsync(null);
        Assert.Equal(1, _remote.Stops);
        Assert.Null(_engine.Remote);
        Assert.Equal(PlaybackState.Playing, _engine.State);
        Assert.InRange(_engine.Position.TotalSeconds, 11.9, 14); // continues locally where the device was
    }

    [Fact]
    public async Task Transport_commands_and_volume_go_to_the_device()
    {
        var list = new Playlist("l", [Entry("a")]);
        await _engine.SetRemoteAsync(_remote); // stopped: just remembered
        Assert.Empty(_remote.Loads);
        await _engine.PlayAsync(list, list.Entries[0]);
        Assert.Single(_remote.Loads);
        Assert.Equal(TimeSpan.Zero, _remote.Loads[0].Start);

        await _engine.PauseAsync();
        Assert.Equal(PlaybackState.Paused, _engine.State);
        await _engine.PlayAsync();
        Assert.Equal(PlaybackState.Playing, _engine.State);
        await _engine.SeekAsync(TimeSpan.FromSeconds(20));
        _engine.Volume = 40;
        await Wait.UntilAsync(() => _remote.Volume == 40);
        await _engine.StopAsync();

        Assert.Equal(["pause", "resume", "seek 20", "stop"], _remote.Commands);
        Assert.Equal(PlaybackState.Stopped, _engine.State);
        Assert.Empty(_output.Opens); // nothing ever played on this computer
    }

    [Fact]
    public async Task The_end_of_a_track_on_the_device_plays_the_next_entry_there()
    {
        var list = new Playlist("l", [Entry("a"), Entry("b")]);
        await _engine.SetRemoteAsync(_remote);
        await _engine.PlayAsync(list, list.Entries[0]);

        _remote.HasEnded = true; // the device reports FINISHED
        await Wait.UntilAsync(() => _remote.Loads.Count == 2, 5000);

        Assert.Same(list.Entries[1], _remote.Loads[1].Entry);
        Assert.Equal("b", _engine.CurrentTrack!.Title);
        _remote.HasEnded = true;
        await Wait.UntilAsync(() => _engine.State == PlaybackState.Stopped, 5000); // end of the playlist
    }

    [Fact]
    public async Task A_lost_device_hands_playback_back_to_this_computer_paused()
    {
        var list = new Playlist("l", [Entry("a")]);
        await _engine.SetRemoteAsync(_remote);
        await _engine.PlayAsync(list, list.Entries[0]);
        _remote.Position = TimeSpan.FromSeconds(7);

        _remote.RaiseLost();
        await Wait.UntilAsync(() => _engine.Remote is null && _engine.State == PlaybackState.Paused, 5000);

        Assert.InRange(_engine.Position.TotalSeconds, 6.9, 7.5);
    }

    [Fact]
    public async Task A_device_that_refuses_two_tracks_in_a_row_is_given_up_on()
    {
        var list = new Playlist("l", [Entry("a"), Entry("b"), Entry("c")]);
        await _engine.PlayAsync(list, list.Entries[0]);
        await Wait.UntilAsync(() => _engine.Position > TimeSpan.FromMilliseconds(300));
        _remote.Refuse = true;

        await _engine.SetRemoteAsync(_remote);

        Assert.Equal(2, _remote.Loads.Count); // a, then b, both refused; c is never tried there
        Assert.Null(_engine.Remote);
        Assert.Equal(PlaybackState.Paused, _engine.State);
        Assert.Equal("a", _engine.CurrentTrack!.Title); // back on this computer at the track asked for
        Assert.InRange(_engine.Position.TotalSeconds, 0.25, 3);
    }

    private sealed class FakeRemote : IRemotePlayer
    {
        public List<(PlaylistEntry Entry, TimeSpan Start, bool Paused)> Loads { get; } = [];

        public List<string> Commands { get; } = [];

        public int Stops { get; private set; }

        public int Volume { get; private set; } = -1;

        public string Name => "Fake TV";

        public TimeSpan Position { get; set; }

        public bool HasEnded { get; set; }

        public event EventHandler? Lost;

        public void RaiseLost() => Lost?.Invoke(this, EventArgs.Empty);

        public bool Refuse { get; set; }

        public Task LoadAsync(PlaylistEntry entry, TrackInfo track, TimeSpan start, bool paused)
        {
            Loads.Add((entry, start, paused));
            if (Refuse)
            {
                throw new IOException("The device refused SetAVTransportURI (UPnP error 716).");
            }
            Position = start;
            HasEnded = false;
            return Task.CompletedTask;
        }

        public Task PauseAsync() => Record("pause");

        public Task ResumeAsync() => Record("resume");

        public Task StopAsync()
        {
            Stops++;
            return Record("stop");
        }

        public Task SeekAsync(TimeSpan position)
        {
            Position = position;
            return Record($"seek {position.TotalSeconds:0}");
        }

        public Task SetVolumeAsync(int volume, bool muted)
        {
            Volume = volume;
            return Task.CompletedTask;
        }

        private Task Record(string command)
        {
            Commands.Add(command);
            return Task.CompletedTask;
        }
    }
}
