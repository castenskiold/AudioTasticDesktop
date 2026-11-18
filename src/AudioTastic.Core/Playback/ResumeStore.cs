using System.Buffers.Binary;
using System.Collections.Concurrent;
using AudioTastic.Data.Cache;

namespace AudioTastic.Core.Playback;

/// <summary>
/// PB-07 / PB-06: per-file playback memory in cache.db: the resume position of long files (kind "resume") and the
/// remembered speed (kind "speed"). Keyed by file version (path + size + modified time). The set of files with a
/// resume position is kept in memory for the playlist marker. Thread-safe. Without a cache (tests) nothing is stored.
/// </summary>
public sealed class ResumeStore
{
    private const string ResumeKind = "resume";
    private const string SpeedKind = "speed";
    private const int DataVersion = 1;

    private readonly CacheStore? _cache;
    private readonly Lazy<ConcurrentDictionary<string, bool>> _withPosition;

    public ResumeStore(CacheStore? cache)
    {
        _cache = cache;
        _withPosition = new Lazy<ConcurrentDictionary<string, bool>>(() =>
            new ConcurrentDictionary<string, bool>((_cache?.GetAll(ResumeKind, DataVersion) ?? []).Select(e => new KeyValuePair<string, bool>(e.Path, true)),
                StringComparer.Ordinal));
    }

    /// <summary>A resume position was saved or cleared.</summary>
    public event EventHandler<string>? Changed;

    /// <summary>True when <paramref name="path"/> has a saved resume position (PW4 marker).</summary>
    public bool HasPosition(string path) => !path.Contains("://", StringComparison.Ordinal) && _withPosition.Value.ContainsKey(Normalize(path));

    public TimeSpan? GetPosition(string path) => Read(path, ResumeKind) is { } seconds ? TimeSpan.FromSeconds(seconds) : null;

    public void SetPosition(string path, TimeSpan position)
    {
        if (Write(path, ResumeKind, position.TotalSeconds))
        {
            _withPosition.Value[Normalize(path)] = true;
            Changed?.Invoke(this, path);
        }
    }

    public void ClearPosition(string path)
    {
        if (_cache is null || path.Contains("://", StringComparison.Ordinal))
        {
            return;
        }
        _cache.Delete(path, ResumeKind);
        if (_withPosition.Value.TryRemove(Normalize(path), out _))
        {
            Changed?.Invoke(this, path);
        }
    }

    public double? GetSpeed(string path) => Read(path, SpeedKind);

    public void SetSpeed(string path, double speed) => Write(path, SpeedKind, speed);

    private double? Read(string path, string kind)
    {
        if (_cache is null || path.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }
        try
        {
            return _cache.TryGet(FileKey.FromFile(path), kind, DataVersion) is { Length: 8 } data
                ? BinaryPrimitives.ReadDoubleLittleEndian(data)
                : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private bool Write(string path, string kind, double value)
    {
        if (_cache is null || path.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }
        try
        {
            Span<byte> data = stackalloc byte[8];
            BinaryPrimitives.WriteDoubleLittleEndian(data, value);
            _cache.Put(FileKey.FromFile(path), kind, DataVersion, data);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Normalize(string path) => new FileKey(path, 0, 0).NormalizedPath;
}
