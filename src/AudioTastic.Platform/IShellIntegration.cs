namespace AudioTastic.Platform;

/// <summary>
/// SI-05: the file manager's context menu entries for audio and playlist files.
/// <see cref="ExecutablePath"/> is started with <c>--open</c> and the file ("Open in …", which adds and plays it) or
/// with <c>--enqueue</c> and the file ("Enqueue in …", which only adds it).
/// </summary>
/// <param name="ExecutablePath">The program to start; its own icon is shown next to the entries.</param>
/// <param name="Extensions">File extensions with a leading dot, e.g. ".flac".</param>
/// <param name="OpenText">Text of the entry that adds and plays the files.</param>
/// <param name="EnqueueText">Text of the entry that only adds the files.</param>
public sealed record ContextMenuEntries(string ExecutablePath, IReadOnlyCollection<string> Extensions, string OpenText, string EnqueueText);

/// <summary>
/// SI-05: AudioTastic as a program that can open these file types. Opening such a file starts
/// <see cref="ExecutablePath"/> with the file (no switch), so Settings › System integration decides whether it is
/// played or enqueued (SI-06). Registering does not make AudioTastic the default: the user chooses that in the OS.
/// </summary>
public sealed record FileAssociations(string ExecutablePath, IReadOnlyCollection<string> AudioExtensions, IReadOnlyCollection<string> PlaylistExtensions);

/// <summary>SI-05: registers AudioTastic with the OS file manager.</summary>
public interface IShellIntegration
{
    /// <summary>True when this OS supports the context menu entries (Windows so far).</summary>
    bool SupportsContextMenu { get; }

    /// <summary>True when this OS supports registering file types (Windows so far).</summary>
    bool SupportsFileAssociations { get; }

    /// <summary>
    /// Adds or updates the context menu entries for exactly <paramref name="entries"/>' extensions and removes the
    /// ones registered earlier for other extensions; null removes every entry.
    /// </summary>
    void SetContextMenu(ContextMenuEntries? entries);

    /// <summary>
    /// Registers AudioTastic for exactly <paramref name="associations"/>' file types (removing types registered earlier
    /// and no longer given); null removes the registration. Returns true when anything changed.
    /// </summary>
    bool SetFileAssociations(FileAssociations? associations);

    /// <summary>Opens the OS page where the user makes AudioTastic the default for file types.</summary>
    void OpenDefaultAppsSettings();

    /// <summary>True when this OS has a Start menu shortcut to manage (Windows).</summary>
    bool SupportsStartMenuShortcut { get; }

    /// <summary>
    /// SI-02 / SI-07: creates or updates the Start menu shortcut to <paramref name="executablePath"/> with the app
    /// identity <paramref name="appId"/> (Windows uses it to name the app in media controls and notifications);
    /// null removes it.
    /// </summary>
    void SetStartMenuShortcut(string? executablePath, string appId);
}

/// <summary>Shell integration for systems that have none yet (macOS and Linux until they get their own).</summary>
public sealed class NoShellIntegration : IShellIntegration
{
    public static NoShellIntegration Instance { get; } = new();

    public bool SupportsContextMenu => false;

    public bool SupportsFileAssociations => false;

    public void SetContextMenu(ContextMenuEntries? entries)
    {
    }

    public bool SetFileAssociations(FileAssociations? associations) => false;

    public void OpenDefaultAppsSettings()
    {
    }

    public bool SupportsStartMenuShortcut => false;

    public void SetStartMenuShortcut(string? executablePath, string appId)
    {
    }
}
