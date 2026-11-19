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

/// <summary>SI-05: registers AudioTastic with the OS file manager.</summary>
public interface IShellIntegration
{
    /// <summary>True when this OS supports the context menu entries (Windows so far).</summary>
    bool SupportsContextMenu { get; }

    /// <summary>
    /// Adds or updates the context menu entries for exactly <paramref name="entries"/>' extensions and removes the
    /// ones registered earlier for other extensions; null removes every entry.
    /// </summary>
    void SetContextMenu(ContextMenuEntries? entries);
}

/// <summary>Shell integration for systems that have none yet (macOS and Linux until they get their own).</summary>
public sealed class NoShellIntegration : IShellIntegration
{
    public static NoShellIntegration Instance { get; } = new();

    public bool SupportsContextMenu => false;

    public void SetContextMenu(ContextMenuEntries? entries)
    {
    }
}
