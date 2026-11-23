using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Core.Audio.Dsp.Vst3;

/// <summary>An effect the user can add: a class in a scanned module.</summary>
public sealed record Vst3Effect(string Path, Vst3Class Class)
{
    public string Name => Class.Name;
}

/// <summary>
/// EQ-06: the VST3 effects found on this computer. Scan results are kept in a cache file, so only new or changed
/// modules are scanned again; a module that crashed, hung or was not a VST3 module stays blacklisted until it
/// changes (or a full rescan). Scans run one module at a time, each in its own process (<see cref="Vst3Scanner"/>).
/// </summary>
public sealed class Vst3Library : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly Vst3Scanner _scanner;
    private readonly string _cacheFile;
    private readonly Func<IEnumerable<string>> _folders;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _scanning = new(1, 1);
    private IReadOnlyList<Vst3Module> _modules;

    /// <param name="folders">The folders to search: the standard ones plus the user's (read at each scan).</param>
    public Vst3Library(Vst3Scanner scanner, string cacheFile, Func<IEnumerable<string>> folders, ILogger logger)
    {
        _scanner = scanner;
        _cacheFile = cacheFile;
        _folders = folders;
        _logger = logger;
        _modules = Load();
    }

    /// <summary>True when the native host and scanner are installed (VST3 works on this computer).</summary>
    public bool IsAvailable => _scanner.ScannerPath is not null && Vst3Plugin.IsAvailable;

    public IReadOnlyList<Vst3Module> Modules => Volatile.Read(ref _modules);

    /// <summary>Every effect of every module that scanned cleanly, by name.</summary>
    public IReadOnlyList<Vst3Effect> Effects =>
        [.. Modules.Where(m => !m.IsBlacklisted).SelectMany(m => m.Classes.Select(c => new Vst3Effect(m.Path, c))).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)];

    public IReadOnlyList<Vst3Module> Blacklisted => [.. Modules.Where(m => m.IsBlacklisted)];

    /// <summary>True once a scan has run (or a cache was found).</summary>
    public bool HasScanned => File.Exists(_cacheFile) || Modules.Count > 0;

    /// <summary>Scan results changed.</summary>
    public event EventHandler? Changed;

    /// <summary>
    /// Finds the modules in the folders and scans those that are new or changed (<paramref name="full"/>: every
    /// module, also blacklisted ones). <paramref name="progress"/> gets the module being scanned.
    /// </summary>
    public async Task RescanAsync(bool full = false, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        await _scanning.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var known = Modules.ToDictionary(m => m.Path, StringComparer.OrdinalIgnoreCase);
            var result = new List<Vst3Module>();
            foreach (var path in Vst3Scanner.FindModules(_folders()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!full && known.TryGetValue(path, out var cached) && cached.Modified == Vst3Scanner.ModifiedTime(path))
                {
                    result.Add(cached);
                    continue;
                }
                progress?.Report(Path.GetFileName(path));
                var module = await _scanner.ScanAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (module.IsBlacklisted)
                {
                    _logger.LogWarning("VST3 module {Path} is blacklisted: {Problem}", path, module.Problem);
                }
                else
                {
                    _logger.LogInformation("VST3 module {Path}: {Count} effect(s)", path, module.Classes.Count);
                }
                result.Add(module);
            }
            Volatile.Write(ref _modules, result);
            Save(result);
        }
        finally
        {
            _scanning.Release();
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<Vst3Module> Load()
    {
        try
        {
            return File.Exists(_cacheFile) ? JsonSerializer.Deserialize<List<Vst3Module>>(File.ReadAllText(_cacheFile), Json) ?? [] : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The VST3 scan cache could not be read; the next scan starts afresh");
            return [];
        }
    }

    public void Dispose() => _scanning.Dispose();

    private void Save(IReadOnlyList<Vst3Module> modules)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
            var temporary = _cacheFile + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(modules, Json));
            File.Move(temporary, _cacheFile, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "The VST3 scan cache could not be saved");
        }
    }
}
