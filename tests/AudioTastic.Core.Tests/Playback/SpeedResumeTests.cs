using AudioTastic.Core.Audio;
using AudioTastic.Core.Playback;
using AudioTastic.Core.Playlists;
using AudioTastic.Core.Tests.Audio;
using AudioTastic.Data;
using AudioTastic.Data.Cache;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Audio;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Core.Tests.Playback;

/// <summary>PB-06 speed and pitch (SoundTouch), PB-07 resume (session and long files).</summary>
public sealed class SpeedResumeTests : IDisposable
{
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly CacheStore _cache;

    public SpeedResumeTests()
    {
        var dir = Directory.CreateTempSubdirectory("audiotastic-speed-").FullName;
        _paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, dir, dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        _settings = new SettingsStore(_paths);
        _cache = new CacheStore(_paths);
    }

    public void Dispose() => Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

    [Theory]
    [InlineData(2.0, true, 440)]   // keep pitch: half as long, same pitch
    [InlineData(0.5, true, 440)]   // twice as long, same pitch
    [InlineData(2.0, false, 880)]  // turntable: half as long, an octave up
    public void Speed_changes_tempo_and_optionally_pitch(double speed, bool keepPitch, double expectedHz)
    {
        Assert.SkipUnless(TimeStretchSource.IsAvailable, "SoundTouch missing: build native/ with CMake (native/README.md).");
        using var stretch = new TimeStretchSource(new Sine(44_100, 440, 4));
        stretch.SetSpeed(speed, keepPitch);
        var output = ReadAll(stretch);

        var frames = output.Length / 2;
        Assert.InRange(frames, 4 * 44_100 / speed * 0.97, 4 * 44_100 / speed * 1.03);
        Assert.InRange(Frequency(output, 44_100), expectedHz * 0.98, expectedHz * 1.02);
        Assert.InRange(stretch.PositionFrames, 4 * 44_100 - 10, 4 * 44_100);
    }

    [Fact]
    public void Normal_speed_bypasses_soundtouch_exactly()
    {
        using var plain = new Sine(44_100, 440, 1);
        using var stretch = new TimeStretchSource(new Sine(44_100, 440, 1));
        stretch.SetSpeed(1.0, keepPitch: true);
        Assert.Equal(ReadAll(plain), ReadAll(stretch));
    }

    [Fact]
    public async Task Long_files_resume_where_they_were_stopped_and_forget_once_finished()
    {
        _settings.Update(SettingsGroup.Playback, s => s.Playback.ResumeMinimumMinutes = 20);
        var resume = new ResumeStore(_cache);
        using var output = new FakeAudioOutput();
        using var engine = new PlaybackEngine([new SyntheticFactory()], output, _settings, NullLogger<PlaybackEngine>.Instance, resume);
        var path = TempFiles.Touch(SyntheticFactory.PathFor("book", seconds: 25 * 60));
        var playlist = new Playlist("books", [new PlaylistEntry(path)]);

        await engine.PlayAsync(playlist, playlist.Entries[0]);
        await engine.SeekAsync(TimeSpan.FromMinutes(7));
        await engine.StopAsync();

        Assert.True(resume.HasPosition(path));
        Assert.InRange(resume.GetPosition(path)!.Value, TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(7.1));

        await engine.PlayAsync(playlist, playlist.Entries[0]);
        Assert.InRange(engine.Position, TimeSpan.FromMinutes(7), TimeSpan.FromMinutes(7.1));

        // Short files are not remembered.
        var song = TempFiles.Touch(SyntheticFactory.PathFor("song", seconds: 180));
        await engine.OpenAsync([song]);
        await engine.SeekAsync(TimeSpan.FromMinutes(1));
        await engine.StopAsync();
        Assert.False(resume.HasPosition(song));

        resume.ClearPosition(path);
        Assert.False(resume.HasPosition(path));
    }

    [Fact]
    public async Task Session_is_saved_and_resumes_paused_at_the_position()
    {
        using var output = new FakeAudioOutput();
        var path = TempFiles.Touch(SyntheticFactory.PathFor("session", seconds: 120));
        var playlist = new Playlist("tab", [new PlaylistEntry(path)]);
        using (var engine = new PlaybackEngine([new SyntheticFactory()], output, _settings, NullLogger<PlaybackEngine>.Instance))
        {
            await engine.PlayAsync(playlist, playlist.Entries[0]);
            await engine.SeekAsync(TimeSpan.FromSeconds(42));
        }
        var session = _settings.Current.Playback.LastSession!;
        Assert.Equal(playlist.Id.ToString(), session.PlaylistId);
        Assert.Equal(path, session.Path);
        Assert.InRange(session.PositionSeconds, 42, 43);

        using var restarted = new PlaybackEngine([new SyntheticFactory()], output, _settings, NullLogger<PlaybackEngine>.Instance);
        await restarted.ResumeSessionAsync(playlist, playlist.Entries[0], TimeSpan.FromSeconds(session.PositionSeconds), play: false);
        Assert.Equal(PlaybackState.Paused, restarted.State);
        Assert.InRange(restarted.Position, TimeSpan.FromSeconds(42), TimeSpan.FromSeconds(43));
    }

    [Fact]
    public async Task Speed_is_saved_and_remembered_per_file_when_enabled()
    {
        _settings.Update(SettingsGroup.Playback, s => s.Playback.RememberSpeedPerFile = true);
        var resume = new ResumeStore(_cache);
        using var output = new FakeAudioOutput();
        using var engine = new PlaybackEngine([new SyntheticFactory()], output, _settings, NullLogger<PlaybackEngine>.Instance, resume);
        var a = TempFiles.Touch(SyntheticFactory.PathFor("speed-a", seconds: 60));
        var b = TempFiles.Touch(SyntheticFactory.PathFor("speed-b", seconds: 60));

        await engine.OpenAsync([a]);
        engine.Speed = 1.5;
        Assert.Equal(1.5, _settings.Current.Playback.Speed);
        await engine.OpenAsync([b]);
        Assert.Equal(1.0, engine.Speed); // b has no saved speed
        await engine.OpenAsync([a]);
        Assert.Equal(1.5, engine.Speed);
        await engine.StopAsync();
    }

    private static float[] ReadAll(IAudioSource source)
    {
        var all = new List<float>();
        var buffer = new float[4096];
        int n;
        while ((n = source.Read(buffer)) > 0)
        {
            all.AddRange(buffer.AsSpan(0, n * 2).ToArray());
        }
        return [.. all];
    }

    /// <summary>Frequency from rising zero crossings of the left channel (middle part).</summary>
    private static double Frequency(float[] interleaved, int rate)
    {
        var frames = interleaved.Length / 2;
        int first = -1, last = -1, crossings = 0;
        for (var f = frames / 4; f < frames * 3 / 4; f++)
        {
            if (interleaved[(f - 1) * 2] < 0 && interleaved[f * 2] >= 0)
            {
                if (first < 0)
                {
                    first = f;
                }
                last = f;
                crossings++;
            }
        }
        return (crossings - 1) * (double)rate / (last - first);
    }

    private sealed class Sine(int rate, double hz, double seconds) : IAudioSource
    {
        public AudioStreamInfo Info { get; } = new("sine", rate, 2, 16, 0, true);
        public AudioFormat Format { get; } = new(rate, 2);
        public long? LengthFrames { get; } = (long)(rate * seconds);
        public long PositionFrames { get; private set; }
        public bool CanSeek => true;
        public IReadOnlyDictionary<string, string> Tags { get; } = new Dictionary<string, string>();

        public int Read(Span<float> buffer)
        {
            var frames = (int)Math.Min(buffer.Length / 2, LengthFrames!.Value - PositionFrames);
            for (var f = 0; f < frames; f++)
            {
                var v = (float)(0.4 * Math.Sin(2 * Math.PI * hz * (PositionFrames + f) / rate));
                buffer[2 * f] = v;
                buffer[(2 * f) + 1] = v;
            }
            PositionFrames += frames;
            return frames;
        }

        public void Seek(long frame) => PositionFrames = Math.Clamp(frame, 0, LengthFrames!.Value);

        public void Dispose()
        {
        }
    }
}
