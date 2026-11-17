namespace AudioTastic.Data.Cache;

/// <summary>
/// Identifies one version of a file: full path + size + last-write time (UTC ticks).
/// If any part changes, cached data for the old version no longer applies.
/// </summary>
public readonly record struct FileKey(string Path, long Size, long LastWriteUtcTicks)
{
    /// <summary>Path in the form used for lookups (full path; lower-case on Windows, where paths are case-insensitive).</summary>
    public string NormalizedPath => Normalize(Path);

    /// <summary>Builds the key from a file on disk.</summary>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    public static FileKey FromFile(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists)
        {
            throw new FileNotFoundException("File not found.", path);
        }
        return new FileKey(info.FullName, info.Length, info.LastWriteTimeUtc.Ticks);
    }

    internal static string Normalize(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        return OperatingSystem.IsWindows() ? full.ToUpperInvariant() : full;
    }
}
