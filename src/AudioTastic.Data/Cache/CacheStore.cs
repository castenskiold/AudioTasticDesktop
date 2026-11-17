using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Data.Cache;

/// <summary>
/// AN-01 part 1: SQLite cache (cache.db) for per-file analysis results such as waveform peaks,
/// loudness and BPM data. Entries are keyed by <see cref="FileKey"/> plus a "kind" name and a
/// data-format version, so a changed file or a changed analyser automatically misses the cache.
/// Thread-safe: every call opens its own (pooled) connection.
/// </summary>
public sealed class CacheStore
{
    private readonly string _connectionString;
    private readonly ILogger _logger;

    public CacheStore(AppPaths paths, ILogger<CacheStore>? logger = null)
        : this(paths?.CacheDatabase ?? throw new ArgumentNullException(nameof(paths)), logger)
    {
    }

    internal CacheStore(string databasePath, ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
        DatabasePath = databasePath;
        var dir = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();

        using var connection = Open();
        Execute(connection, "PRAGMA journal_mode=WAL;");
        var applied = CacheMigrations.Apply(connection);
        if (applied > 0)
        {
            _logger.LogInformation("Applied {Count} cache migration(s); schema version {Version}", applied, SchemaVersion);
        }
    }

    public string DatabasePath { get; }

    /// <summary>Highest applied migration version.</summary>
    public int SchemaVersion
    {
        get
        {
            using var connection = Open();
            return CacheMigrations.CurrentVersion(connection);
        }
    }

    /// <summary>Stores (or replaces) data of <paramref name="kind"/> for the file version in <paramref name="key"/>.</summary>
    public void Put(FileKey key, string kind, int dataVersion, ReadOnlySpan<byte> data)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        using var connection = Open();
        using var tx = connection.BeginTransaction();
        var fileId = GetOrCreateFileId(connection, tx, key);
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO cache_entries (file_id, kind, data_version, data, created_utc)
            VALUES ($file, $kind, $version, $data, $created)
            ON CONFLICT (file_id, kind) DO UPDATE SET
                data_version = excluded.data_version, data = excluded.data, created_utc = excluded.created_utc;
            """;
        cmd.Parameters.AddWithValue("$file", fileId);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$version", dataVersion);
        cmd.Parameters.Add("$data", SqliteType.Blob).Value = data.ToArray();
        cmd.Parameters.AddWithValue("$created", DateTime.UtcNow.ToString("O"));
        cmd.ExecuteNonQuery();
        tx.Commit();
    }

    /// <summary>
    /// Returns cached data of <paramref name="kind"/> for exactly this file version and data version,
    /// or null when there is none (file changed, never analysed, or analyser version changed).
    /// </summary>
    public byte[]? TryGet(FileKey key, string kind, int dataVersion)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT e.data FROM cache_entries e
            JOIN files f ON f.id = e.file_id
            WHERE f.path = $path AND f.size = $size AND f.mtime_ticks = $mtime
              AND e.kind = $kind AND e.data_version = $version;
            """;
        cmd.Parameters.AddWithValue("$path", key.NormalizedPath);
        cmd.Parameters.AddWithValue("$size", key.Size);
        cmd.Parameters.AddWithValue("$mtime", key.LastWriteUtcTicks);
        cmd.Parameters.AddWithValue("$kind", kind);
        cmd.Parameters.AddWithValue("$version", dataVersion);
        return cmd.ExecuteScalar() as byte[];
    }

    /// <summary>Removes all cached data for a path (every version of the file).</summary>
    public int Remove(string path)
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM files WHERE path = $path;";
        cmd.Parameters.AddWithValue("$path", FileKey.Normalize(path));
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Number of file versions known to the cache.</summary>
    public long FileCount()
    {
        using var connection = Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM files;";
        return (long)cmd.ExecuteScalar()!;
    }

    private static long GetOrCreateFileId(SqliteConnection connection, SqliteTransaction tx, FileKey key)
    {
        var path = key.NormalizedPath;

        using (var find = connection.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT id FROM files WHERE path = $path AND size = $size AND mtime_ticks = $mtime;";
            find.Parameters.AddWithValue("$path", path);
            find.Parameters.AddWithValue("$size", key.Size);
            find.Parameters.AddWithValue("$mtime", key.LastWriteUtcTicks);
            if (find.ExecuteScalar() is long id)
            {
                return id;
            }
        }

        // The file changed (or is new): drop data for older versions of the same path.
        using (var purge = connection.CreateCommand())
        {
            purge.Transaction = tx;
            purge.CommandText = "DELETE FROM files WHERE path = $path;";
            purge.Parameters.AddWithValue("$path", path);
            purge.ExecuteNonQuery();
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = tx;
        insert.CommandText = "INSERT INTO files (path, size, mtime_ticks) VALUES ($path, $size, $mtime) RETURNING id;";
        insert.Parameters.AddWithValue("$path", path);
        insert.Parameters.AddWithValue("$size", key.Size);
        insert.Parameters.AddWithValue("$mtime", key.LastWriteUtcTicks);
        return (long)insert.ExecuteScalar()!;
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    internal static void Execute(SqliteConnection connection, string sql, SqliteTransaction? tx = null)
    {
        using var cmd = connection.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
