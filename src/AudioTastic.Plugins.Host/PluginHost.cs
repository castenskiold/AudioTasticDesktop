using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Playlists;
using AudioTastic.Sdk.Plugins;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Plugins.Host;

public sealed class PluginDisabledEventArgs(PluginRecord plugin, string reason) : EventArgs
{
    public PluginRecord Plugin { get; } = plugin;
    public string Reason { get; } = reason;
}

/// <summary>
/// PG-03 / PG-04 / PG-05: finds, loads, isolates and manages plugins.
/// <list type="bullet">
/// <item>Search order: the built-in folder (next to the program), then the user's plugins folder (installed
/// .atplugin packages). A user plugin with the same id replaces the built-in one.</item>
/// <item>Each plugin gets a collectible <see cref="PluginLoadContext"/>; its classes implementing <see cref="IPlugin"/>
/// are created and initialised with an <see cref="IPluginContext"/>.</item>
/// <item>Faults: exceptions from plugin code are reported through <see cref="ReportFault"/>; 3 within a minute
/// disable the plugin for this session (status Failed) and raise <see cref="PluginDisabled"/>.</item>
/// </list>
/// Thread-safe; the lists returned are snapshots.
/// </summary>
public sealed class PluginHost : IDisposable
{
    public const string PackageExtension = ".atplugin";
    private const int FaultLimit = 3;
    private static readonly TimeSpan FaultWindow = TimeSpan.FromMinutes(1);

    private readonly IReadOnlyList<string> _builtInFolders;
    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<PluginHost> _logger;
    private readonly Lock _lock = new();
    private List<PluginRecord> _plugins = [];
    private IReadOnlyList<IAudioSourceFactory> _sourceFactories = [];
    private IReadOnlyList<IPlaylistFormat> _playlistFormats = [];
    private IPlayerApi? _player;

    public PluginHost(AppPaths paths, SettingsStore settings, ILoggerFactory loggerFactory, IEnumerable<string>? builtInFolders = null)
    {
        _paths = paths;
        _settings = settings;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<PluginHost>();
        _builtInFolders = builtInFolders?.ToArray() ?? [Path.Combine(AppContext.BaseDirectory, "plugins")];
    }

    public IReadOnlyList<PluginRecord> Plugins
    {
        get
        {
            lock (_lock)
            {
                return [.. _plugins];
            }
        }
    }

    /// <summary>Decoders of all enabled input plugins, each wrapped in a fault guard. Changes raise <see cref="PluginsChanged"/>.</summary>
    public IReadOnlyList<IAudioSourceFactory> SourceFactories => Volatile.Read(ref _sourceFactories);

    /// <summary>Playlist formats offered by enabled input plugins.</summary>
    public IReadOnlyList<IPlaylistFormat> PlaylistFormats => Volatile.Read(ref _playlistFormats);

    /// <summary>
    /// A live view of <see cref="SourceFactories"/> for services created once (engine, tag reader): every
    /// enumeration sees the plugins enabled at that moment.
    /// </summary>
    public IEnumerable<IAudioSourceFactory> LiveSourceFactories => new LiveView<IAudioSourceFactory>(() => SourceFactories);

    /// <summary>A live view of <see cref="PlaylistFormats"/>.</summary>
    public IEnumerable<IPlaylistFormat> LivePlaylistFormats => new LiveView<IPlaylistFormat>(() => PlaylistFormats);

    private sealed class LiveView<T>(Func<IReadOnlyList<T>> current) : IEnumerable<T>
    {
        public IEnumerator<T> GetEnumerator() => current().GetEnumerator();
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    /// <summary>A plugin was loaded, enabled, disabled, installed or removed.</summary>
    public event EventHandler? PluginsChanged;

    /// <summary>A plugin was disabled because of faults (PG-03 notification).</summary>
    public event EventHandler<PluginDisabledEventArgs>? PluginDisabled;

    /// <summary>The player API given to plugins; set before <see cref="LoadAll"/>.</summary>
    public void AttachPlayer(IPlayerApi player) => _player = player;

    /// <summary>Enabled instances of a plugin type, e.g. <c>Instances&lt;IGeneralPlugin&gt;()</c>.</summary>
    public IReadOnlyList<T> Instances<T>() where T : class =>
        [.. Plugins.Where(p => p.Status == PluginStatus.Enabled).SelectMany(p => p.Instances).OfType<T>()];

    /// <summary>Discovers every plugin and loads the enabled ones.</summary>
    public void LoadAll()
    {
        RemovePendingFolders();
        var found = new Dictionary<string, PluginRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (root, builtIn) in _builtInFolders.Select(f => (f, true)).Append((_paths.PluginsFolder, false)))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }
            foreach (var folder in Directory.EnumerateDirectories(root).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (ReadRecord(folder, builtIn) is { } record)
                {
                    if (found.TryGetValue(record.Id, out var existing))
                    {
                        _logger.LogInformation("Plugin {Id} in {Folder} replaces the one in {Existing}", record.Id, folder, existing.Folder);
                    }
                    found[record.Id] = record;
                }
            }
        }
        lock (_lock)
        {
            _plugins = [.. found.Values.OrderBy(p => p.Manifest.Name, StringComparer.OrdinalIgnoreCase)];
        }
        var disabled = _settings.Current.Plugins.Disabled;
        foreach (var record in Plugins.Where(p => p.Status != PluginStatus.Incompatible))
        {
            if (disabled.Contains(record.Id, StringComparer.OrdinalIgnoreCase))
            {
                record.Status = PluginStatus.Disabled;
            }
            else
            {
                Load(record);
            }
        }
        Rebuild();
    }

    /// <summary>Switches a plugin on (and remembers the choice).</summary>
    public void Enable(string id)
    {
        var record = Find(id);
        _settings.Update(SettingsGroup.Plugins, s => s.Plugins.Disabled.RemoveAll(d => string.Equals(d, id, StringComparison.OrdinalIgnoreCase)));
        if (record.Status is PluginStatus.Disabled or PluginStatus.Failed)
        {
            record.Error = null;
            record.Faults.Clear();
            Load(record);
            Rebuild();
        }
    }

    /// <summary>Switches a plugin off (and remembers the choice); unloads it.</summary>
    public void Disable(string id)
    {
        var record = Find(id);
        _settings.Update(SettingsGroup.Plugins, s =>
        {
            if (!s.Plugins.Disabled.Contains(id, StringComparer.OrdinalIgnoreCase))
            {
                s.Plugins.Disabled.Add(id);
            }
        });
        Unload(record, PluginStatus.Disabled, null);
        Rebuild();
    }

    /// <summary>
    /// PG-04: installs a .atplugin (zip with plugin.json at its root) into the user's plugins folder and loads it.
    /// An installed plugin with the same id is replaced. Throws <see cref="InvalidDataException"/> for invalid packages.
    /// </summary>
    public PluginRecord Install(string packagePath)
    {
        PluginManifest manifest;
        using (var zip = ZipFile.OpenRead(packagePath))
        {
            var entry = zip.GetEntry(PluginManifest.FileName) ?? throw new InvalidDataException("The package has no plugin.json at its root.");
            using (var reader = new StreamReader(entry.Open()))
            {
                manifest = ParseManifest(reader.ReadToEnd());
            }
            var errors = manifest.Validate();
            if (errors.Count > 0)
            {
                throw new InvalidDataException("Invalid plugin.json: " + string.Join("; ", errors));
            }
            if (zip.GetEntry(manifest.EntryAssembly.Replace('\\', '/')) is null)
            {
                throw new InvalidDataException($"The package does not contain {manifest.EntryAssembly}.");
            }

            var target = Path.Combine(_paths.PluginsFolder, manifest.Id);
            if (Plugins.FirstOrDefault(p => string.Equals(p.Id, manifest.Id, StringComparison.OrdinalIgnoreCase)) is { } existing)
            {
                Unload(existing, PluginStatus.Disabled, null);
                lock (_lock)
                {
                    _plugins.Remove(existing);
                }
            }
            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
            var root = Path.GetFullPath(target) + Path.DirectorySeparatorChar;
            foreach (var file in zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)))
            {
                var destination = Path.GetFullPath(Path.Combine(target, file.FullName));
                if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"The package contains an unsafe path: {file.FullName}");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                file.ExtractToFile(destination, overwrite: true);
            }
        }

        var record = ReadRecord(Path.Combine(_paths.PluginsFolder, manifest.Id), isBuiltIn: false)
            ?? throw new InvalidDataException("The installed plugin could not be read.");
        lock (_lock)
        {
            _plugins.RemoveAll(p => string.Equals(p.Id, record.Id, StringComparison.OrdinalIgnoreCase));
            _plugins.Add(record);
        }
        _settings.Update(SettingsGroup.Plugins, s => s.Plugins.Disabled.RemoveAll(d => string.Equals(d, record.Id, StringComparison.OrdinalIgnoreCase)));
        Load(record);
        Rebuild();
        _logger.LogInformation("Installed plugin {Id} {Version}", record.Id, manifest.Version);
        return record;
    }

    /// <summary>PG-04: unloads and deletes an installed plugin (built-in plugins can only be disabled).</summary>
    public void Uninstall(string id)
    {
        var record = Find(id);
        if (record.IsBuiltIn)
        {
            throw new InvalidOperationException("Built-in plugins cannot be uninstalled; disable them instead.");
        }
        Unload(record, PluginStatus.Disabled, null);
        lock (_lock)
        {
            _plugins.Remove(record);
        }
        try
        {
            Directory.Delete(record.Folder, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Native libraries stay locked until the process exits: delete at the next start.
            _settings.Update(SettingsGroup.Plugins, s => s.Plugins.PendingRemoval.Add(record.Folder));
            _logger.LogInformation("Plugin folder {Folder} is in use; it will be removed at the next start", record.Folder);
        }
        _settings.Update(SettingsGroup.Plugins, s =>
        {
            s.Plugins.Data.Remove(record.Id);
            s.Plugins.Disabled.RemoveAll(d => string.Equals(d, record.Id, StringComparison.OrdinalIgnoreCase));
        });
        Rebuild();
    }

    /// <summary>
    /// PG-03: records an exception thrown by plugin code. The third fault within a minute disables the plugin
    /// for this session (status Failed) and raises <see cref="PluginDisabled"/>.
    /// </summary>
    public void ReportFault(PluginRecord plugin, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        _logger.LogWarning(exception, "Plugin {Id} failed", plugin.Id);
        bool disable;
        lock (plugin.Faults)
        {
            var now = DateTime.UtcNow;
            plugin.Faults.Enqueue(now);
            while (plugin.Faults.Count > 0 && now - plugin.Faults.Peek() > FaultWindow)
            {
                plugin.Faults.Dequeue();
            }
            disable = plugin.Faults.Count >= FaultLimit && plugin.Status == PluginStatus.Enabled;
        }
        if (!disable)
        {
            return;
        }
        var reason = $"Disabled after {FaultLimit} errors within a minute. Last error: {exception.Message}";
        Unload(plugin, PluginStatus.Failed, reason);
        Rebuild();
        _logger.LogError("Plugin {Id} disabled: {Reason}", plugin.Id, reason);
        PluginDisabled?.Invoke(this, new PluginDisabledEventArgs(plugin, reason));
    }

    /// <summary>Runs plugin code, reporting any exception as a fault; returns false when it threw.</summary>
    public bool Guard(PluginRecord plugin, Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        try
        {
            action();
            return true;
        }
#pragma warning disable CA1031 // A plugin fault must never escape into the player.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            ReportFault(plugin, ex);
            return false;
        }
    }

    /// <summary>The plugin record a plugin instance belongs to.</summary>
    public PluginRecord? RecordOf(object instance) => Plugins.FirstOrDefault(p => p.Instances.Contains(instance));

    /// <summary>Unloads every plugin (player exit).</summary>
    public void Dispose()
    {
        foreach (var record in Plugins)
        {
            if (record.Status == PluginStatus.Enabled)
            {
                Unload(record, PluginStatus.Disabled, null);
            }
        }
    }

    /// <summary>For tests: the load context of a plugin (to check it was collected after unloading).</summary>
    internal static WeakReference? LoadContextReference(PluginRecord record) => record.LoadContext is { } c ? new WeakReference(c) : null;

    // ---- internals ------------------------------------------------------------------------------------

    private PluginRecord? ReadRecord(string folder, bool isBuiltIn)
    {
        var manifestPath = Path.Combine(folder, PluginManifest.FileName);
        if (!File.Exists(manifestPath))
        {
            return null;
        }
        PluginManifest manifest;
        try
        {
            manifest = ParseManifest(File.ReadAllText(manifestPath));
        }
        catch (InvalidDataException ex)
        {
            var broken = new PluginManifest { Id = Path.GetFileName(folder), Name = Path.GetFileName(folder) };
            return new PluginRecord(broken, folder, isBuiltIn) { Status = PluginStatus.Incompatible, Error = ex.Message };
        }
        var record = new PluginRecord(manifest, folder, isBuiltIn);
        var errors = manifest.Validate().ToList();
        if (errors.Count == 0 && !File.Exists(Path.Combine(folder, manifest.EntryAssembly)))
        {
            errors.Add($"{manifest.EntryAssembly} not found");
        }
        if (errors.Count > 0)
        {
            record.Status = PluginStatus.Incompatible;
            record.Error = string.Join("; ", errors);
            _logger.LogWarning("Plugin in {Folder} is incompatible: {Errors}", folder, record.Error);
        }
        return record;
    }

    private static PluginManifest ParseManifest(string json)
    {
        try
        {
            return PluginManifest.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"plugin.json is not valid: {ex.Message}", ex);
        }
    }

    private void Load(PluginRecord record)
    {
        var entryPath = Path.Combine(record.Folder, record.Manifest.EntryAssembly);
        var context = new PluginLoadContext(record.Id, entryPath);
        var instances = new List<IPlugin>();
        record.PlayerScope = new ScopedPlayerApi(
            _player ?? throw new InvalidOperationException("AttachPlayer must be called before plugins are loaded."), record, this);
        try
        {
            var assembly = context.LoadFromFile(entryPath);
            var types = assembly.GetExportedTypes()
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IPlugin).IsAssignableFrom(t) && t.GetConstructor(Type.EmptyTypes) is not null)
                .ToArray();
            if (types.Length == 0)
            {
                throw new InvalidOperationException($"{record.Manifest.EntryAssembly} contains no public plugin class (IPlugin with a parameterless constructor).");
            }
            foreach (var type in types)
            {
                var plugin = (IPlugin)Activator.CreateInstance(type)!;
                instances.Add(plugin);
                plugin.Initialize(CreateContext(record));
            }
            record.LoadContext = context;
            record.Instances = instances;
            record.Status = PluginStatus.Enabled;
            record.Error = null;
            _logger.LogInformation("Loaded plugin {Id} {Version} ({Types})", record.Id, record.Manifest.Version, string.Join(", ", types.Select(t => t.Name)));
        }
#pragma warning disable CA1031 // Any load failure marks the plugin Failed; the player keeps running.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            var reason = ex is TargetInvocationException { InnerException: { } inner } ? inner.Message : ex.Message;
            foreach (var plugin in instances)
            {
                SafeDispose(record, plugin);
            }
            record.Instances = [];
            record.LoadContext = null;
            record.PlayerScope.Dispose();
            record.PlayerScope = null;
            record.Status = PluginStatus.Failed;
            record.Error = reason;
            context.Unload();
            _logger.LogError(ex, "Plugin {Id} could not be loaded", record.Id);
        }
    }

    private void Unload(PluginRecord record, PluginStatus status, string? error)
    {
        foreach (var plugin in record.Instances)
        {
            SafeDispose(record, plugin);
        }
        record.Instances = [];
        record.PlayerScope?.Dispose();
        record.PlayerScope = null;
        record.Status = status;
        record.Error = error;
        record.LoadContext?.Unload();
        record.LoadContext = null;
    }

    private void SafeDispose(PluginRecord record, IPlugin plugin)
    {
        try
        {
            plugin.Dispose();
        }
#pragma warning disable CA1031 // A plugin fault must never escape into the player.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Plugin {Id} failed while unloading", record.Id);
        }
    }

    private PluginContext CreateContext(PluginRecord record) => new(
        record.Id,
        record.Folder,
        Path.Combine(_paths.PluginDataFolder, record.Id),
        new PluginSettingsStore(_settings, record.Id),
        record.PlayerScope!,
        _loggerFactory.CreateLogger($"Plugin.{record.Id}"));

    private void Rebuild()
    {
        var enabled = Plugins.Where(p => p.Status == PluginStatus.Enabled)
            .OrderByDescending(p => p.Manifest.Priority)
            .ThenBy(p => p.IsBuiltIn)
            .ToArray();
        var factories = new List<IAudioSourceFactory>();
        var formats = new List<IPlaylistFormat>();
        foreach (var record in enabled)
        {
            foreach (var input in record.Instances.OfType<IInputPlugin>())
            {
                Guard(record, () =>
                {
                    factories.AddRange(input.SourceFactories.Select(f => new GuardedSourceFactory(f, record, this)));
                    formats.AddRange(input.PlaylistFormats);
                });
            }
        }
        Volatile.Write(ref _sourceFactories, factories);
        Volatile.Write(ref _playlistFormats, formats);
        PluginsChanged?.Invoke(this, EventArgs.Empty);
    }

    private PluginRecord Find(string id) =>
        Plugins.FirstOrDefault(p => string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? throw new KeyNotFoundException($"No plugin with id '{id}'.");

    private void RemovePendingFolders()
    {
        var pending = _settings.Current.Plugins.PendingRemoval;
        if (pending.Count == 0)
        {
            return;
        }
        var remaining = new List<string>();
        foreach (var folder in pending)
        {
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, recursive: true);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                remaining.Add(folder);
            }
        }
        _settings.Update(SettingsGroup.Plugins, s => s.Plugins.PendingRemoval = remaining);
    }
}
