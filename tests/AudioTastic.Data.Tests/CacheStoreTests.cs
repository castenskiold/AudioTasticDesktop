using AudioTastic.Data.Cache;
using Microsoft.Data.Sqlite;

namespace AudioTastic.Data.Tests;

public sealed class CacheStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    private CacheStore NewStore() => new(_dir.Combine("cache.db"));

    private FileKey NewFile(string name, string content = "audio")
    {
        var path = _dir.Combine(name);
        File.WriteAllText(path, content);
        return FileKey.FromFile(path);
    }

    [Fact]
    public void Creates_database_with_latest_schema()
    {
        var store = NewStore();
        Assert.True(File.Exists(store.DatabasePath));
        Assert.Equal(CacheMigrations.LatestVersion, store.SchemaVersion);
    }

    [Fact]
    public void Migrations_are_applied_only_once()
    {
        _ = NewStore();
        var again = NewStore();
        Assert.Equal(CacheMigrations.LatestVersion, again.SchemaVersion);
    }

    [Fact]
    public void Put_then_TryGet_returns_data()
    {
        var store = NewStore();
        var key = NewFile("a.flac");
        store.Put(key, "waveform", 1, [1, 2, 3]);
        Assert.Equal(new byte[] { 1, 2, 3 }, store.TryGet(key, "waveform", 1));
    }

    [Fact]
    public void Put_replaces_existing_entry()
    {
        var store = NewStore();
        var key = NewFile("a.flac");
        store.Put(key, "waveform", 1, [1]);
        store.Put(key, "waveform", 2, [2]);
        Assert.Null(store.TryGet(key, "waveform", 1));
        Assert.Equal(new byte[] { 2 }, store.TryGet(key, "waveform", 2));
    }

    [Fact]
    public void Different_kinds_are_independent()
    {
        var store = NewStore();
        var key = NewFile("a.flac");
        store.Put(key, "waveform", 1, [1]);
        store.Put(key, "loudness", 1, [9]);
        Assert.Equal(new byte[] { 1 }, store.TryGet(key, "waveform", 1));
        Assert.Equal(new byte[] { 9 }, store.TryGet(key, "loudness", 1));
    }

    [Fact]
    public void Changed_file_misses_and_old_data_is_purged()
    {
        var store = NewStore();
        var oldKey = NewFile("a.flac", "one");
        store.Put(oldKey, "waveform", 1, [1]);

        var newKey = oldKey with { Size = oldKey.Size + 1 };
        Assert.Null(store.TryGet(newKey, "waveform", 1));

        store.Put(newKey, "waveform", 1, [2]);
        Assert.Null(store.TryGet(oldKey, "waveform", 1));
        Assert.Equal(1, store.FileCount());
    }

    [Fact]
    public void Remove_deletes_all_entries_for_path()
    {
        var store = NewStore();
        var key = NewFile("a.flac");
        store.Put(key, "waveform", 1, [1]);
        Assert.Equal(1, store.Remove(key.Path));
        Assert.Null(store.TryGet(key, "waveform", 1));
        Assert.Equal(0, store.FileCount());
    }

    [Fact]
    public void Concurrent_writes_do_not_fail()
    {
        var store = NewStore();
        var keys = Enumerable.Range(0, 20).Select(i => NewFile($"f{i}.mp3", new string('x', i + 1))).ToArray();
        Parallel.ForEach(keys, k => store.Put(k, "waveform", 1, [42]));
        Assert.Equal(20, store.FileCount());
        Assert.All(keys, k => Assert.Equal(new byte[] { 42 }, store.TryGet(k, "waveform", 1)));
    }

    [Fact]
    public void FileKey_FromFile_throws_for_missing_file()
    {
        Assert.Throws<FileNotFoundException>(() => FileKey.FromFile(_dir.Combine("missing.wav")));
    }
}
