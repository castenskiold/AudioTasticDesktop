using System.Reflection;
using System.Runtime.InteropServices;

namespace AudioTastic.Sdk.Interop;

/// <summary>
/// Finds native libraries (FFmpeg, miniaudio, ...) in the two places the build puts them:
/// next to the application (publish for one runtime identifier) or in <c>runtimes/&lt;rid&gt;/native</c>
/// (plain build, which copies every RID). Plugins with native code use the same helper.
/// </summary>
public static class NativeLibraries
{
    private static readonly Lock RegisterLock = new();
    private static readonly HashSet<Assembly> Registered = [];

    /// <summary>Directories searched, in order. Only existing directories are returned.</summary>
    public static IReadOnlyList<string> SearchDirectories { get; } = BuildSearchDirectories(AppContext.BaseDirectory);

    /// <summary>
    /// The first search directory that contains <paramref name="fileName"/>, or null.
    /// <paramref name="fileName"/> is a real file name such as "avcodec-62.dll" or "libavcodec.so.62".
    /// </summary>
    public static string? FindDirectoryContaining(string fileName) =>
        SearchDirectories.FirstOrDefault(d => File.Exists(Path.Combine(d, fileName)));

    /// <summary>
    /// Installs a <see cref="DllImportAttribute"/> resolver for <paramref name="assembly"/> that probes
    /// <see cref="SearchDirectories"/> using the platform's naming (name.dll, libname.so, libname.dylib).
    /// Safe to call more than once.
    /// </summary>
    public static void RegisterResolver(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (RegisterLock)
        {
            if (!Registered.Add(assembly))
            {
                return;
            }
            NativeLibrary.SetDllImportResolver(assembly, Resolve);
        }
    }

    /// <summary>Candidate file names for a library base name on the current OS.</summary>
    public static IEnumerable<string> PlatformFileNames(string libraryName)
    {
        if (OperatingSystem.IsWindows())
        {
            yield return libraryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ? libraryName : libraryName + ".dll";
        }
        else if (OperatingSystem.IsMacOS())
        {
            yield return "lib" + libraryName + ".dylib";
            yield return libraryName + ".dylib";
        }
        else
        {
            yield return "lib" + libraryName + ".so";
            yield return libraryName + ".so";
        }
        yield return libraryName;
    }

    internal static IReadOnlyList<string> BuildSearchDirectories(string baseDirectory)
    {
        var rid = RuntimeInformation.RuntimeIdentifier;
        var candidates = new[]
        {
            baseDirectory,
            Path.Combine(baseDirectory, "runtimes", rid, "native"),
            Path.Combine(baseDirectory, "runtimes", GetPortableRid(), "native"),
        };
        return candidates.Distinct(StringComparer.OrdinalIgnoreCase).Where(Directory.Exists).ToArray();
    }

    /// <summary>Portable RID such as win-x64 even when the runtime reports a distro-specific one.</summary>
    public static string PortableRid { get; } = GetPortableRid();

    private static string GetPortableRid()
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            var other => other.ToString().ToLowerInvariant(),
        };
        return $"{os}-{arch}";
    }

    private static IntPtr Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        foreach (var dir in SearchDirectories)
        {
            foreach (var file in PlatformFileNames(libraryName))
            {
                var path = Path.Combine(dir, file);
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out var handle))
                {
                    return handle;
                }
            }
        }
        // Fall back to the default probing (system libraries, LD_LIBRARY_PATH, ...).
        return IntPtr.Zero;
    }
}
