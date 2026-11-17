using System.IO.Compression;
using System.Reflection;

namespace AudioTastic.App.Skins;

/// <summary>UI-03: where a skin's files come from (zip package, folder, or the built-in default skin).</summary>
public interface ISkinSource : IDisposable
{
    /// <summary>Human-readable location, for messages.</summary>
    string Location { get; }

    bool Exists(string path);

    /// <summary>Opens a file (forward-slash relative path); throws <see cref="FileNotFoundException"/> when missing.</summary>
    Stream Open(string path);
}

/// <summary>An .atskin file (zip with skin.json at the root).</summary>
public sealed class ZipSkinSource : ISkinSource
{
    private readonly ZipArchive _zip;
    private readonly Dictionary<string, ZipArchiveEntry> _entries;

    public ZipSkinSource(string file)
    {
        Location = file;
        // Read the (small) package into memory so the file is not locked while the skin is in use
        // and can be replaced by Install or deleted.
        _zip = new ZipArchive(new MemoryStream(File.ReadAllBytes(file)), ZipArchiveMode.Read);
        _entries = _zip.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .GroupBy(e => Normalize(e.FullName), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
    }

    public string Location { get; }

    public bool Exists(string path) => _entries.ContainsKey(Normalize(path));

    public Stream Open(string path)
    {
        if (!_entries.TryGetValue(Normalize(path), out var entry))
        {
            throw new FileNotFoundException($"'{path}' is not in {Location}.");
        }
        // Copy out: zip entry streams are not seekable, and loaders (fonts, PNG) may need to seek.
        var copy = new MemoryStream();
        using (var s = entry.Open())
        {
            s.CopyTo(copy);
        }
        copy.Position = 0;
        return copy;
    }

    public void Dispose() => _zip.Dispose();

    internal static string Normalize(string path) => path.Replace('\\', '/').TrimStart('/');
}

/// <summary>A folder containing skin.json (development, or an unpacked skin in the skins folder).</summary>
public sealed class DirectorySkinSource(string folder) : ISkinSource
{
    private readonly string _root = Path.GetFullPath(folder);

    public string Location => _root;

    public bool Exists(string path) => Resolve(path) is { } full && File.Exists(full);

    public Stream Open(string path) =>
        Resolve(path) is { } full ? File.OpenRead(full) : throw new FileNotFoundException($"'{path}' is outside {Location}.");

    public void Dispose()
    {
    }

    /// <summary>Full path, or null when <paramref name="path"/> escapes the skin folder.</summary>
    private string? Resolve(string path)
    {
        var full = Path.GetFullPath(Path.Combine(_root, ZipSkinSource.Normalize(path)));
        return full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}

/// <summary>The built-in default skin, embedded in the app (resources named "AudioTastic.Skins.Default/&lt;path&gt;").</summary>
public sealed class EmbeddedSkinSource : ISkinSource
{
    public const string Prefix = "AudioTastic.Skins.Default/";
    private readonly Assembly _assembly;
    private readonly Dictionary<string, string> _names;

    public EmbeddedSkinSource(Assembly? assembly = null)
    {
        _assembly = assembly ?? typeof(EmbeddedSkinSource).Assembly;
        _names = _assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(Prefix, StringComparison.Ordinal))
            .ToDictionary(n => ZipSkinSource.Normalize(n[Prefix.Length..]), n => n, StringComparer.OrdinalIgnoreCase);
    }

    public string Location => "built-in default skin";

    public bool Exists(string path) => _names.ContainsKey(ZipSkinSource.Normalize(path));

    public Stream Open(string path) =>
        _names.TryGetValue(ZipSkinSource.Normalize(path), out var name) && _assembly.GetManifestResourceStream(name) is { } s
            ? s
            : throw new FileNotFoundException($"'{path}' is not in the {Location}.");

    public void Dispose()
    {
    }
}
