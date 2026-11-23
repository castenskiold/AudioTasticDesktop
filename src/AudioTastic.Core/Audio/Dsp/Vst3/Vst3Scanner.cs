using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AudioTastic.Core.Audio.Dsp.Vst3;

/// <summary>An audio effect class in a VST3 module.</summary>
public sealed record Vst3Class(string Id, string Name, string Vendor, string Version, string SubCategories);

/// <summary>
/// The result of scanning one VST3 module. <paramref name="Problem"/> is set when the module is blacklisted: it
/// crashed or hung while being scanned, or is not a VST3 module; it is not scanned again until the file changes.
/// </summary>
public sealed record Vst3Module(string Path, DateTime Modified, IReadOnlyList<Vst3Class> Classes, string? Problem = null)
{
    [JsonIgnore]
    public bool IsBlacklisted => Problem is not null;
}

/// <summary>
/// EQ-06: finds VST3 modules in the standard folders (and the user's own) and lists their effect classes. Each module
/// is scanned in a separate process (the native at_vst3scan tool), so a plug-in that crashes or hangs while loading
/// cannot take the player down: it is blacklisted instead.
/// </summary>
public sealed class Vst3Scanner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Vst3Scanner(string? scannerPath = null)
    {
        ScannerPath = scannerPath ?? FindScanner();
    }

    /// <summary>The native scanner, or null when it is not installed (VST3 is then unavailable).</summary>
    public string? ScannerPath { get; }

    /// <summary>The standard VST3 folders of this operating system (spec EQ-06).</summary>
    public static IReadOnlyList<string> DefaultFolders()
    {
        if (OperatingSystem.IsWindows())
        {
            return
            [
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonProgramFiles), "VST3"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Common", "VST3"),
            ];
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return OperatingSystem.IsMacOS()
            ? ["/Library/Audio/Plug-Ins/VST3", System.IO.Path.Combine(home, "Library", "Audio", "Plug-Ins", "VST3")]
            : [System.IO.Path.Combine(home, ".vst3"), "/usr/lib/vst3", "/usr/local/lib/vst3"];
    }

    /// <summary>
    /// The .vst3 modules in <paramref name="folders"/> and their subfolders: single files, or bundles (folders named
    /// *.vst3), which are not searched inside.
    /// </summary>
    public static IReadOnlyList<string> FindModules(IEnumerable<string> folders)
    {
        ArgumentNullException.ThrowIfNull(folders);
        var found = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        void Walk(string folder, int depth)
        {
            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(folder);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return;
            }
            foreach (var entry in entries)
            {
                if (entry.EndsWith(".vst3", StringComparison.OrdinalIgnoreCase))
                {
                    if (seen.Add(System.IO.Path.GetFullPath(entry)))
                    {
                        found.Add(entry);
                    }
                }
                else if (depth < 6 && Directory.Exists(entry))
                {
                    Walk(entry, depth + 1);
                }
            }
        }
        foreach (var folder in folders.Where(Directory.Exists))
        {
            Walk(folder, 0);
        }
        return found;
    }

    /// <summary>When the module at <paramref name="path"/> last changed (a bundle: its newest file).</summary>
    public static DateTime ModifiedTime(string path)
    {
        if (File.Exists(path))
        {
            return File.GetLastWriteTimeUtc(path);
        }
        if (!Directory.Exists(path))
        {
            return DateTime.MinValue;
        }
        var newest = Directory.GetLastWriteTimeUtc(path);
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            var time = File.GetLastWriteTimeUtc(file);
            if (time > newest)
            {
                newest = time;
            }
        }
        return newest;
    }

    /// <summary>Scans one module in a separate process; crashes, hangs and broken modules come back blacklisted.</summary>
    public async Task<Vst3Module> ScanAsync(string path, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        var modified = ModifiedTime(path);
        if (ScannerPath is null)
        {
            return new Vst3Module(path, modified, [], "The VST3 scanner is not installed.");
        }
        EnsureExecutable(ScannerPath);
        var start = new ProcessStartInfo(ScannerPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(path);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The VST3 scanner did not start.");
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new Vst3Module(path, modified, [], $"Did not finish loading within {(timeout ?? DefaultTimeout).TotalSeconds:0} s.");
        }
        var text = await output.ConfigureAwait(false);
        var message = (await error.ConfigureAwait(false)).Trim();
        if (process.ExitCode == 2)
        {
            return new Vst3Module(path, modified, [], string.IsNullOrEmpty(message) ? "Not a VST3 module." : message);
        }
        if (process.ExitCode != 0)
        {
            return new Vst3Module(path, modified, [], $"Crashed while loading (exit code 0x{process.ExitCode:X8}).");
        }
        try
        {
            var classes = JsonSerializer.Deserialize<ScanOutput>(text, Json)?.Classes ?? [];
            return new Vst3Module(path, modified, [.. classes.Where(IsEffect)]);
        }
        catch (JsonException ex)
        {
            return new Vst3Module(path, modified, [], $"The scanner's answer could not be read: {ex.Message}");
        }
    }

    private sealed record ScanOutput(List<Vst3Class> Classes);

    /// <summary>
    /// Synths and effects share the VST3 audio module category; the sub-categories tell them apart. An instrument
    /// that is not also marked as an effect ("Fx") has no place in the DSP chain.
    /// </summary>
    public static bool IsEffect(Vst3Class plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        var categories = plugin.SubCategories.Split('|', StringSplitOptions.TrimEntries);
        return categories.Contains("Fx", StringComparer.OrdinalIgnoreCase) || !categories.Contains("Instrument", StringComparer.OrdinalIgnoreCase);
    }

    private static string? FindScanner()
    {
        var name = OperatingSystem.IsWindows() ? "at_vst3scan.exe" : "at_vst3scan";
        return AudioTastic.Sdk.Interop.NativeLibraries.FindDirectoryContaining(name) is { } folder ? System.IO.Path.Combine(folder, name) : null;
    }

    /// <summary>Downloaded build artifacts lose the execute bit on macOS and Linux: put it back.</summary>
    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var mode = File.GetUnixFileMode(path);
        if ((mode & UnixFileMode.UserExecute) == 0)
        {
            File.SetUnixFileMode(path, mode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
        }
    }
}
