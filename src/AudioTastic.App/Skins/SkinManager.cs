using AudioTastic.Data;
using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Skins;

/// <summary>An installed skin as listed in Settings › Interface › Skin and Main menu › Skins.</summary>
/// <param name="Id">"default", or the file/folder name in the skins folder.</param>
public sealed record SkinInfo(string Id, string Name, string? Author, string? Version, bool IsDefault);

public sealed class SkinChangedEventArgs(Skin skin) : EventArgs
{
    public Skin Skin { get; } = skin;
}

/// <summary>
/// UI-03: lists, installs, loads and switches skins. The built-in default skin is always available; installed skins
/// are .atskin files or folders (with skin.json) in the data folder's skins folder. A skin that fails to load never
/// replaces the current one; at start-up the default skin is used instead. UI thread only.
/// </summary>
public sealed class SkinManager : IDisposable
{
    public const string DefaultSkinId = "default";
    public const string PackageExtension = ".atskin";

    private readonly AppPaths _paths;
    private readonly SettingsStore _settings;
    private readonly ILogger<SkinManager> _logger;
    private Skin? _current;

    public SkinManager(AppPaths paths, SettingsStore settings, ILogger<SkinManager> logger)
    {
        _paths = paths;
        _settings = settings;
        _logger = logger;
    }

    /// <summary>The skin in use; loads the configured skin on first access.</summary>
    public Skin Current => _current ??= LoadStartupSkin();

    /// <summary>Problems with the configured skin found at start-up (it was replaced by the default skin).</summary>
    public IReadOnlyList<string> StartupErrors { get; private set; } = [];

    /// <summary>Raised after <see cref="Current"/> changed (live switching).</summary>
    public event EventHandler<SkinChangedEventArgs>? SkinChanged;

    public IReadOnlyList<SkinInfo> GetInstalledSkins()
    {
        List<SkinInfo> skins;
        using (var builtIn = OpenSource(DefaultSkinId))
        {
            skins = [Info(DefaultSkinId, SkinLoader.Load(builtIn), isDefault: true)];
        }
        if (!Directory.Exists(_paths.SkinsFolder))
        {
            return skins;
        }
        var candidates = Directory.EnumerateFiles(_paths.SkinsFolder, "*" + PackageExtension)
            .Concat(Directory.EnumerateDirectories(_paths.SkinsFolder).Where(d => File.Exists(Path.Combine(d, SkinLoader.SkinFile))));
        foreach (var path in candidates.Order(StringComparer.OrdinalIgnoreCase))
        {
            var id = Path.GetFileName(path);
            try
            {
                using var source = OpenSource(id);
                skins.Add(Info(id, SkinLoader.Load(source), isDefault: false));
            }
            catch (Exception ex) when (ex is SkinException or IOException or InvalidDataException)
            {
                _logger.LogWarning("Skipping invalid skin {Skin}: {Error}", id, ex.Message);
            }
        }
        return skins;
    }

    /// <summary>Switches to <paramref name="id"/> and saves the choice. Throws <see cref="SkinException"/> (current skin kept).</summary>
    public void Apply(string id)
    {
        var skin = Load(id);
        var previous = _current;
        _current = skin;
        _settings.Update(SettingsGroup.Interface, s => s.Interface.Skin = id);
        _logger.LogInformation("Skin changed to {Skin} ({Name})", id, skin.Name);
        SkinChanged?.Invoke(this, new SkinChangedEventArgs(skin));
        previous?.Dispose();
    }

    /// <summary>
    /// Validates <paramref name="packageFile"/> (.atskin) and copies it into the skins folder, replacing a skin with
    /// the same file name. Returns the new skin's id. Throws <see cref="SkinException"/> when invalid.
    /// </summary>
    public string Install(string packageFile)
    {
        ArgumentException.ThrowIfNullOrEmpty(packageFile);
        var name = Path.GetFileName(packageFile);
        if (!name.EndsWith(PackageExtension, StringComparison.OrdinalIgnoreCase) || !SkinLoader.SafeName().IsMatch(name))
        {
            throw new SkinException(packageFile, [$"Skins are installed from {PackageExtension} files with a plain file name."]);
        }
        try
        {
            using var source = new ZipSkinSource(packageFile);
            SkinLoader.Load(source);
        }
        catch (InvalidDataException ex)
        {
            throw new SkinException(packageFile, [$"Not a valid zip archive: {ex.Message}"]);
        }
        Directory.CreateDirectory(_paths.SkinsFolder);
        File.Copy(packageFile, Path.Combine(_paths.SkinsFolder, name), overwrite: true);
        _logger.LogInformation("Installed skin {Skin}", name);
        return name;
    }

    /// <summary>Loads and validates a skin without switching to it. Throws <see cref="SkinException"/>.</summary>
    public Skin Load(string id)
    {
        var source = OpenSource(id);
        try
        {
            return new Skin(id, SkinLoader.Load(source), source);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public void Dispose() => _current?.Dispose();

    private Skin LoadStartupSkin()
    {
        var id = _settings.Current.Interface.Skin ?? DefaultSkinId;
        try
        {
            return Load(id);
        }
        catch (Exception ex) when (ex is SkinException or IOException or InvalidDataException && id != DefaultSkinId)
        {
            StartupErrors = ex is SkinException se ? se.Errors : [ex.Message];
            _logger.LogWarning("Skin {Skin} could not be loaded, using the default skin: {Error}", id, ex.Message);
            return Load(DefaultSkinId);
        }
    }

    private ISkinSource OpenSource(string id)
    {
        if (id == DefaultSkinId)
        {
            return new EmbeddedSkinSource();
        }
        if (!SkinLoader.SafeName().IsMatch(id))
        {
            throw new SkinException(id, ["Invalid skin name."]);
        }
        var path = Path.Combine(_paths.SkinsFolder, id);
        if (File.Exists(path))
        {
            try
            {
                return new ZipSkinSource(path);
            }
            catch (InvalidDataException ex)
            {
                throw new SkinException(id, [$"Not a valid zip archive: {ex.Message}"]);
            }
        }
        if (Directory.Exists(path))
        {
            return new DirectorySkinSource(path);
        }
        throw new SkinException(id, ["The skin is not installed."]);
    }

    private static SkinInfo Info(string id, SkinDefinition d, bool isDefault) => new(id, d.Name, d.Author, d.Version, isDefault);
}
