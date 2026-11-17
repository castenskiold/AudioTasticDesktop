using Microsoft.Data.Sqlite;

namespace AudioTastic.Data.Cache;

/// <summary>
/// Ordered schema migrations for cache.db. Never edit an existing migration; append a new one.
/// Applied versions are recorded in the schema_migrations table.
/// </summary>
internal static class CacheMigrations
{
    private static readonly (int Version, string Name, string Sql)[] All =
    [
        (1, "files and cache entries", """
            CREATE TABLE files (
                id          INTEGER PRIMARY KEY,
                path        TEXT    NOT NULL,
                size        INTEGER NOT NULL,
                mtime_ticks INTEGER NOT NULL,
                UNIQUE (path, size, mtime_ticks)
            );
            CREATE INDEX ix_files_path ON files (path);
            CREATE TABLE cache_entries (
                file_id      INTEGER NOT NULL REFERENCES files (id) ON DELETE CASCADE,
                kind         TEXT    NOT NULL,
                data_version INTEGER NOT NULL,
                data         BLOB    NOT NULL,
                created_utc  TEXT    NOT NULL,
                PRIMARY KEY (file_id, kind)
            );
            """),
    ];

    public static int LatestVersion => All[^1].Version;

    /// <summary>Applies all migrations not yet recorded. Returns how many were applied.</summary>
    public static int Apply(SqliteConnection connection)
    {
        CacheStore.Execute(connection, """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                version     INTEGER PRIMARY KEY,
                name        TEXT NOT NULL,
                applied_utc TEXT NOT NULL
            );
            """);

        var current = CurrentVersion(connection);
        var applied = 0;
        foreach (var (version, name, sql) in All.Where(m => m.Version > current).OrderBy(m => m.Version))
        {
            using var tx = connection.BeginTransaction();
            CacheStore.Execute(connection, sql, tx);
            using var record = connection.CreateCommand();
            record.Transaction = tx;
            record.CommandText = "INSERT INTO schema_migrations (version, name, applied_utc) VALUES ($v, $n, $t);";
            record.Parameters.AddWithValue("$v", version);
            record.Parameters.AddWithValue("$n", name);
            record.Parameters.AddWithValue("$t", DateTime.UtcNow.ToString("O"));
            record.ExecuteNonQuery();
            tx.Commit();
            applied++;
        }
        return applied;
    }

    public static int CurrentVersion(SqliteConnection connection)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;";
        return Convert.ToInt32(cmd.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }
}
