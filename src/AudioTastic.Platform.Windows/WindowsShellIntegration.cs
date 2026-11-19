using System.Runtime.Versioning;
using Microsoft.Win32;

namespace AudioTastic.Platform.Windows;

/// <summary>
/// SI-05 on Windows: per-user verbs under <c>HKCU\Software\Classes\SystemFileAssociations\&lt;ext&gt;\shell</c>, shown for
/// those file types whatever program opens them. Each verb shows the program's own icon. Windows 11 lists such
/// classic verbs under "Show more options" (Shift+F10, or Shift+right-click). The extensions written are remembered
/// under <c>HKCU\Software\AudioTastic\ShellIntegration</c>, so entries for extensions no longer supported (a plugin
/// removed) are taken away again. Nothing needs administrator rights.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsShellIntegration : IShellIntegration
{
    /// <summary>Verb key names: Explorer sorts verbs by key name, so "Play" (Open) comes before "Queue" (Enqueue).</summary>
    public const string OpenVerb = "AudioTastic.Play";

    public const string EnqueueVerb = "AudioTastic.Queue";

    private const string ExtensionsValue = "Extensions";

    private readonly RegistryKey _root;
    private readonly string _classesPath;
    private readonly string _statePath;
    private readonly Lock _gate = new();

    /// <summary>The real location: the current user's classes.</summary>
    public WindowsShellIntegration()
        : this(Registry.CurrentUser, @"Software\Classes", @"Software\AudioTastic\ShellIntegration")
    {
    }

    /// <summary>For tests: writes below <paramref name="classesPath"/> instead of the user's real classes.</summary>
    public WindowsShellIntegration(RegistryKey root, string classesPath, string statePath)
    {
        _root = root;
        _classesPath = classesPath;
        _statePath = statePath;
    }

    public bool SupportsContextMenu => true;

    public void SetContextMenu(ContextMenuEntries? entries)
    {
        lock (_gate)
        {
            var wanted = entries is null
                ? []
                : entries.Extensions.Select(e => e.ToLowerInvariant()).Where(IsExtension).Distinct().Order(StringComparer.Ordinal).ToArray();
            foreach (var old in RegisteredExtensions().Except(wanted, StringComparer.OrdinalIgnoreCase))
            {
                Remove(old);
            }
            if (entries is not null)
            {
                foreach (var extension in wanted)
                {
                    Write(extension, OpenVerb, entries.OpenText, entries.ExecutablePath, $"\"{entries.ExecutablePath}\" --open \"%1\"");
                    Write(extension, EnqueueVerb, entries.EnqueueText, entries.ExecutablePath, $"\"{entries.ExecutablePath}\" --enqueue \"%1\"");
                }
            }
            if (wanted.Length == 0)
            {
                _root.DeleteSubKeyTree(_statePath, throwOnMissingSubKey: false);
            }
            else
            {
                using var state = _root.CreateSubKey(_statePath);
                state.SetValue(ExtensionsValue, wanted, RegistryValueKind.MultiString);
            }
        }
    }

    /// <summary>The extensions that currently have entries (as remembered when they were written).</summary>
    public IReadOnlyList<string> RegisteredExtensions()
    {
        using var state = _root.OpenSubKey(_statePath);
        return state?.GetValue(ExtensionsValue) as string[] ?? [];
    }

    private static bool IsExtension(string extension) =>
        extension.Length is > 1 and < 16 && extension[0] == '.' && extension.Skip(1).All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '_');

    private string ShellPath(string extension) => $@"{_classesPath}\SystemFileAssociations\{extension}\shell";

    private void Write(string extension, string verb, string text, string executable, string command)
    {
        using var key = _root.CreateSubKey($@"{ShellPath(extension)}\{verb}");
        key.SetValue("MUIVerb", text);
        key.SetValue("Icon", $"\"{executable}\",0");
        // "Player": the verb stays available when many files are selected (the default limit is 15).
        key.SetValue("MultiSelectModel", "Player");
        using var commandKey = key.CreateSubKey("command");
        commandKey.SetValue(string.Empty, command);
    }

    private void Remove(string extension)
    {
        var shell = ShellPath(extension);
        _root.DeleteSubKeyTree($@"{shell}\{OpenVerb}", throwOnMissingSubKey: false);
        _root.DeleteSubKeyTree($@"{shell}\{EnqueueVerb}", throwOnMissingSubKey: false);
        // Leave keys other programs use; remove the ones that are now empty.
        DeleteIfEmpty(shell);
        DeleteIfEmpty($@"{_classesPath}\SystemFileAssociations\{extension}");
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
}
