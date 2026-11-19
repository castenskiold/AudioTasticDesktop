namespace AudioTastic.Platform;

/// <summary>
/// CO-01: entry point to everything that differs per operating system.
/// Later phases add members for media controls (SI-02), hotkeys (SI-01), taskbar (SI-03),
/// notifications (SI-07), and CD drives (FM-11).
/// </summary>
public interface IPlatformServices
{
    /// <summary>Short name of the platform, e.g. "Windows".</summary>
    string Name { get; }

    /// <summary>Opens a folder in the OS file manager.</summary>
    void OpenFolder(string path);

    /// <summary>Opens a file with its default application.</summary>
    void OpenFile(string path);

    /// <summary>SI-05: file manager integration (context menu entries).</summary>
    IShellIntegration Shell { get; }

    /// <summary>
    /// SI-06: called by a second start before it hands its files to the running player, so that the player may
    /// bring its window to the front (Windows only lets the process the user just started do that).
    /// </summary>
    void AllowOtherProcessToActivate();
}
