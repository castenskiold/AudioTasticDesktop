using System.Reflection;
using System.Runtime.Loader;
using AudioTastic.Sdk.Interop;

namespace AudioTastic.Plugins.Host;

/// <summary>
/// PG-03: one collectible load context per plugin. The SDK, Avalonia/Skia (UI controls returned by plugins must be
/// the host's types) and the logging abstractions come from the player; everything else from the plugin's folder,
/// so plugin dependencies cannot clash. Managed assemblies are loaded from memory, so their files are not locked
/// (uninstall can delete them). Native libraries are resolved from the plugin folder and its runtimes/&lt;rid&gt;/native.
/// </summary>
internal sealed class PluginLoadContext : AssemblyLoadContext
{
    private static readonly string[] SharedPrefixes =
        ["AudioTastic.Sdk", "Avalonia", "SkiaSharp", "HarfBuzzSharp", "Microsoft.Extensions.Logging.Abstractions"];

    private readonly string _folder;
    private readonly AssemblyDependencyResolver? _resolver;

    public PluginLoadContext(string pluginId, string entryAssemblyPath)
        : base($"plugin:{pluginId}", isCollectible: true)
    {
        _folder = Path.GetDirectoryName(entryAssemblyPath)!;
        try
        {
            _resolver = new AssemblyDependencyResolver(entryAssemblyPath);
        }
        catch (InvalidOperationException)
        {
            _resolver = null; // no .deps.json: fall back to probing the folder
        }
    }

    public static bool IsShared(string? name) =>
        name is not null && SharedPrefixes.Any(p => name.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>Loads an assembly of the plugin from memory (no file lock), with its .pdb when present.</summary>
    public Assembly LoadFromFile(string path)
    {
        using var image = new MemoryStream(File.ReadAllBytes(path));
        var pdb = Path.ChangeExtension(path, ".pdb");
        if (File.Exists(pdb))
        {
            using var symbols = new MemoryStream(File.ReadAllBytes(pdb));
            return LoadFromStream(image, symbols);
        }
        return LoadFromStream(image);
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (IsShared(assemblyName.Name))
        {
            return null; // use the player's copy
        }
        var path = _resolver?.ResolveAssemblyToPath(assemblyName) ?? Probe(assemblyName.Name + ".dll");
        return path is not null && File.Exists(path) ? LoadFromFile(path) : null;
    }

    protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
    {
        var path = _resolver?.ResolveUnmanagedDllToPath(unmanagedDllName);
        if (path is null)
        {
            foreach (var name in NativeLibraries.PlatformFileNames(unmanagedDllName))
            {
                path = Probe(name) ?? Probe(Path.Combine("runtimes", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "native", name));
                if (path is not null)
                {
                    break;
                }
            }
        }
        return path is not null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
    }

    private string? Probe(string relative)
    {
        var candidate = Path.Combine(_folder, relative);
        return File.Exists(candidate) ? candidate : null;
    }
}
