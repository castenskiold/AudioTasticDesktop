using AudioTastic.Data.Settings;

namespace AudioTastic.Data.Tests;

/// <summary>
/// A settings file another program holds open (antivirus, the search indexer, a parallel test) made File.Move throw
/// out of SettingsStore.Update; from the engine's save timer that ended the whole process (seen on Windows CI).
/// </summary>
public sealed class SettingsBusyFileTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("audiotastic-busy-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private AppPaths Paths() =>
        AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, _dir, _dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();

    [Fact]
    public void A_busy_settings_file_never_throws_and_the_change_is_written_later()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file another handle has open.");
        var paths = Paths();
        var store = new SettingsStore(paths);
        using (new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read)) // no FileShare.Delete
        {
            store.Update(SettingsGroup.Playback, s => s.Playback.Volume = 33); // retried, then kept in memory
            Assert.Equal(33, store.Current.Playback.Volume);
        }
        Assert.NotEqual(33, new SettingsStore(paths).Current.Playback.Volume); // not on disk yet

        store.Update(SettingsGroup.Playback, s => s.Playback.Muted = true);
        var reloaded = new SettingsStore(paths).Current.Playback;
        Assert.Equal(33, reloaded.Volume); // the next save wrote the earlier change too
        Assert.True(reloaded.Muted);
    }

    [Fact]
    public async Task A_file_that_is_busy_for_a_moment_is_written_by_the_retry()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Only Windows refuses to replace a file another handle has open.");
        var paths = Paths();
        var store = new SettingsStore(paths);
        var handle = new FileStream(paths.SettingsFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        var release = Task.Run(async () =>
        {
            await Task.Delay(60, TestContext.Current.CancellationToken);
            await handle.DisposeAsync();
        }, TestContext.Current.CancellationToken);
        store.Update(SettingsGroup.Playback, s => s.Playback.Volume = 44);
        await release;
        Assert.Equal(44, new SettingsStore(paths).Current.Playback.Volume);
    }
}
