using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AudioTastic.Platform.Windows;

/// <summary>
/// SI-05 on Windows, all per user (HKCU, no administrator rights):
/// <list type="bullet">
/// <item>Context menu: verbs under <c>Software\Classes\SystemFileAssociations\&lt;ext&gt;\shell</c>, shown for those file
/// types whatever program opens them, with the program's own icon. Windows 11 lists such classic verbs under
/// "Show more options".</item>
/// <item>File associations: the ProgIDs <c>AudioTastic.Audio</c> and <c>AudioTastic.Playlist</c>, each extension's
/// <c>OpenWithProgids</c>, <c>Applications\AudioTastic.exe</c> and the <c>Capabilities</c> listed under
/// <c>RegisteredApplications</c>. That puts AudioTastic in "Open with" and in Settings › Default apps; Windows
/// lets only the user make it the default.</item>
/// </list>
/// The extensions written are remembered under <c>Software\AudioTastic\ShellIntegration</c>, so entries for
/// extensions no longer supported (a plugin removed) are taken away again.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsShellIntegration : IShellIntegration
{
    /// <summary>Verb key names: Explorer sorts verbs by key name, so "Play" (Open) comes before "Queue" (Enqueue).</summary>
    public const string OpenVerb = "AudioTastic.Play";

    public const string EnqueueVerb = "AudioTastic.Queue";

    public const string AudioProgId = "AudioTastic.Audio";

    public const string PlaylistProgId = "AudioTastic.Playlist";

    /// <summary>The name under RegisteredApplications; also used by the Default apps link.</summary>
    public const string ApplicationName = "AudioTastic";

    private const string ExtensionsValue = "Extensions";
    private const string AssociationsValue = "AssociatedExtensions";
    private const string AssociationsSignatureValue = "AssociationsSignature";

    private readonly RegistryKey _root;
    private readonly string _classesPath;
    private readonly string _statePath;
    private readonly string _capabilitiesPath;
    private readonly string _registeredApplicationsPath;
    private readonly bool _notifyShell;
    private readonly Lock _gate = new();

    /// <summary>The real location: the current user's registry.</summary>
    public WindowsShellIntegration()
        : this(Registry.CurrentUser, "Software", notifyShell: true)
    {
    }

    /// <summary>For tests: everything is written below <paramref name="softwarePath"/> instead of HKCU\Software.</summary>
    public WindowsShellIntegration(RegistryKey root, string softwarePath, bool notifyShell = false)
    {
        _root = root;
        _classesPath = softwarePath + @"\Classes";
        _statePath = softwarePath + @"\AudioTastic\ShellIntegration";
        _capabilitiesPath = softwarePath + @"\AudioTastic\Capabilities";
        _registeredApplicationsPath = softwarePath + @"\RegisteredApplications";
        _notifyShell = notifyShell;
    }

    public bool SupportsContextMenu => true;

    public bool SupportsFileAssociations => true;

    // ---- context menu -------------------------------------------------------------------------------------

    public void SetContextMenu(ContextMenuEntries? entries)
    {
        lock (_gate)
        {
            var wanted = entries is null ? [] : Clean(entries.Extensions);
            foreach (var old in RegisteredExtensions().Except(wanted, StringComparer.OrdinalIgnoreCase))
            {
                RemoveVerbs(old);
            }
            if (entries is not null)
            {
                foreach (var extension in wanted)
                {
                    WriteVerb(extension, OpenVerb, entries.OpenText, entries.ExecutablePath, $"\"{entries.ExecutablePath}\" --open \"%1\"");
                    WriteVerb(extension, EnqueueVerb, entries.EnqueueText, entries.ExecutablePath, $"\"{entries.ExecutablePath}\" --enqueue \"%1\"");
                }
            }
            SetState(ExtensionsValue, wanted);
        }
    }

    /// <summary>The extensions that currently have context menu entries (as remembered when they were written).</summary>
    public IReadOnlyList<string> RegisteredExtensions() => GetState(ExtensionsValue);

    private string ShellPath(string extension) => $@"{_classesPath}\SystemFileAssociations\{extension}\shell";

    private void WriteVerb(string extension, string verb, string text, string executable, string command)
    {
        using var key = _root.CreateSubKey($@"{ShellPath(extension)}\{verb}");
        key.SetValue("MUIVerb", text);
        key.SetValue("Icon", $"\"{executable}\",0");
        // "Player": the verb stays available when many files are selected (the default limit is 15).
        key.SetValue("MultiSelectModel", "Player");
        using var commandKey = key.CreateSubKey("command");
        commandKey.SetValue(string.Empty, command);
    }

    private void RemoveVerbs(string extension)
    {
        var shell = ShellPath(extension);
        _root.DeleteSubKeyTree($@"{shell}\{OpenVerb}", throwOnMissingSubKey: false);
        _root.DeleteSubKeyTree($@"{shell}\{EnqueueVerb}", throwOnMissingSubKey: false);
        // Leave keys other programs use; remove the ones that are now empty.
        DeleteIfEmpty(shell);
        DeleteIfEmpty($@"{_classesPath}\SystemFileAssociations\{extension}");
    }

    // ---- file associations --------------------------------------------------------------------------------

    public bool SetFileAssociations(FileAssociations? associations)
    {
        lock (_gate)
        {
            var playlists = associations is null ? [] : Clean(associations.PlaylistExtensions);
            var audio = associations is null ? [] : Clean(associations.AudioExtensions).Except(playlists, StringComparer.Ordinal).ToArray();
            var signature = associations is null ? "" : associations.ExecutablePath + "|" + string.Join(' ', audio) + "|" + string.Join(' ', playlists);
            if ((GetStateText(AssociationsSignatureValue) ?? "") == signature && (associations is not null || GetState(AssociationsValue).Count == 0))
            {
                return false; // unchanged: no writes, no shell refresh
            }

            var all = audio.Concat(playlists).ToArray();
            foreach (var old in GetState(AssociationsValue).Except(all, StringComparer.OrdinalIgnoreCase))
            {
                RemoveOpenWith(old);
            }
            if (associations is null)
            {
                _root.DeleteSubKeyTree($@"{_classesPath}\{AudioProgId}", throwOnMissingSubKey: false);
                _root.DeleteSubKeyTree($@"{_classesPath}\{PlaylistProgId}", throwOnMissingSubKey: false);
                _root.DeleteSubKeyTree($@"{_classesPath}\Applications\{ExecutableName(null)}", throwOnMissingSubKey: false);
                _root.DeleteSubKeyTree(_capabilitiesPath, throwOnMissingSubKey: false);
                using (var registered = _root.OpenSubKey(_registeredApplicationsPath, writable: true))
                {
                    registered?.DeleteValue(ApplicationName, throwOnMissingValue: false);
                }
                SetState(AssociationsValue, []);
                SetStateText(AssociationsSignatureValue, null);
            }
            else
            {
                var exe = associations.ExecutablePath;
                WriteProgId(AudioProgId, "Audio file (AudioTastic)", exe);
                WriteProgId(PlaylistProgId, "Playlist (AudioTastic)", exe);
                using (var app = _root.CreateSubKey($@"{_classesPath}\Applications\{ExecutableName(exe)}"))
                {
                    app.SetValue("FriendlyAppName", ApplicationName);
                    using (var command = app.CreateSubKey(@"shell\open\command"))
                    {
                        command.SetValue(string.Empty, OpenCommand(exe));
                    }
                    app.DeleteSubKeyTree("SupportedTypes", throwOnMissingSubKey: false);
                    using var types = app.CreateSubKey("SupportedTypes");
                    foreach (var extension in all)
                    {
                        types.SetValue(extension, string.Empty);
                    }
                }
                _root.DeleteSubKeyTree(_capabilitiesPath, throwOnMissingSubKey: false);
                using (var capabilities = _root.CreateSubKey(_capabilitiesPath))
                {
                    capabilities.SetValue("ApplicationName", ApplicationName);
                    capabilities.SetValue("ApplicationDescription", "A Winamp-style audio player with DJ-style crossfading.");
                    capabilities.SetValue("ApplicationIcon", $"\"{exe}\",0");
                    using var files = capabilities.CreateSubKey("FileAssociations");
                    foreach (var extension in audio)
                    {
                        files.SetValue(extension, AudioProgId);
                    }
                    foreach (var extension in playlists)
                    {
                        files.SetValue(extension, PlaylistProgId);
                    }
                }
                using (var registered = _root.CreateSubKey(_registeredApplicationsPath))
                {
                    registered.SetValue(ApplicationName, _capabilitiesPath);
                }
                foreach (var extension in audio)
                {
                    WriteOpenWith(extension, AudioProgId);
                }
                foreach (var extension in playlists)
                {
                    WriteOpenWith(extension, PlaylistProgId);
                }
                SetState(AssociationsValue, all);
                SetStateText(AssociationsSignatureValue, signature);
            }
            if (_notifyShell)
            {
                SHChangeNotify(ShcneAssocChanged, 0, IntPtr.Zero, IntPtr.Zero); // Explorer refreshes icons and "Open with"
            }
            return true;
        }
    }

    /// <summary>The extensions AudioTastic is registered for (as remembered when they were written).</summary>
    public IReadOnlyList<string> AssociatedExtensions() => GetState(AssociationsValue);

    public void OpenDefaultAppsSettings() =>
        ProcessLauncher.ShellOpen($"ms-settings:defaultapps?registeredAppUser={ApplicationName}");

    private static string OpenCommand(string exe) => $"\"{exe}\" \"%1\"";

    private static string ExecutableName(string? exe) => exe is null ? "AudioTastic.exe" : Path.GetFileName(exe);

    private void WriteProgId(string progId, string description, string exe)
    {
        using var key = _root.CreateSubKey($@"{_classesPath}\{progId}");
        key.SetValue(string.Empty, description);
        key.SetValue("FriendlyTypeName", description);
        using (var icon = key.CreateSubKey("DefaultIcon"))
        {
            icon.SetValue(string.Empty, $"\"{exe}\",0");
        }
        using (var open = key.CreateSubKey(@"shell\open"))
        {
            open.SetValue("FriendlyAppName", ApplicationName);
            // "Player": selecting many files and pressing Enter starts AudioTastic for each; the running player batches them.
            open.SetValue("MultiSelectModel", "Player");
        }
        using var command = key.CreateSubKey(@"shell\open\command");
        command.SetValue(string.Empty, OpenCommand(exe));
    }

    private void WriteOpenWith(string extension, string progId)
    {
        using var key = _root.CreateSubKey($@"{_classesPath}\{extension}\OpenWithProgids");
        // The other ProgID may be left from a type that moved between audio and playlist.
        key.DeleteValue(progId == AudioProgId ? PlaylistProgId : AudioProgId, throwOnMissingValue: false);
        key.SetValue(progId, Array.Empty<byte>(), RegistryValueKind.None);
    }

    private void RemoveOpenWith(string extension)
    {
        var path = $@"{_classesPath}\{extension}\OpenWithProgids";
        using (var key = _root.OpenSubKey(path, writable: true))
        {
            key?.DeleteValue(AudioProgId, throwOnMissingValue: false);
            key?.DeleteValue(PlaylistProgId, throwOnMissingValue: false);
        }
        DeleteIfEmpty(path);
        DeleteIfEmpty($@"{_classesPath}\{extension}");
    }

    // ---- helpers ------------------------------------------------------------------------------------------

    private static string[] Clean(IEnumerable<string> extensions) =>
        extensions.Select(e => e.ToLowerInvariant()).Where(IsExtension).Distinct().Order(StringComparer.Ordinal).ToArray();

    private static bool IsExtension(string extension) =>
        extension.Length is > 1 and < 16 && extension[0] == '.' && extension.Skip(1).All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '_');

    private IReadOnlyList<string> GetState(string name)
    {
        using var state = _root.OpenSubKey(_statePath);
        return state?.GetValue(name) as string[] ?? [];
    }

    private string? GetStateText(string name)
    {
        using var state = _root.OpenSubKey(_statePath);
        return state?.GetValue(name) as string;
    }

    private void SetState(string name, string[] values)
    {
        using (var state = _root.CreateSubKey(_statePath))
        {
            if (values.Length == 0)
            {
                state.DeleteValue(name, throwOnMissingValue: false);
            }
            else
            {
                state.SetValue(name, values, RegistryValueKind.MultiString);
            }
        }
        DeleteIfEmpty(_statePath);
    }

    private void SetStateText(string name, string? value)
    {
        using (var state = _root.CreateSubKey(_statePath))
        {
            if (value is null)
            {
                state.DeleteValue(name, throwOnMissingValue: false);
            }
            else
            {
                state.SetValue(name, value);
            }
        }
        DeleteIfEmpty(_statePath);
    }

    private void DeleteIfEmpty(string path)
    {
        using (var key = _root.OpenSubKey(path))
        {
            if (key is null || key.SubKeyCount > 0 || key.ValueCount > 0)
            {
                return;
            }
        }
        _root.DeleteSubKey(path, throwOnMissingSubKey: false);
    }

    private const int ShcneAssocChanged = 0x08000000;

    [LibraryImport("shell32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
