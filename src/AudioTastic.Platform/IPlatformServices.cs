namespace AudioTastic.Platform;

/// <summary>
/// CO-01: entry point to everything that differs per operating system.
/// Later phases add members for media controls (SI-02), hotkeys (SI-01), taskbar (SI-03),
/// shell integration (SI-05), notifications (SI-07), CD drives (FM-11) and ASIO (PB-04).
/// </summary>
public interface IPlatformServices
{
    /// <summary>Short name of the platform, e.g. "Windows".</summary>
    string Name { get; }

    /// <summary>Opens a folder in the OS file manager.</summary>
    void OpenFolder(string path);

    /// <summary>Opens a file with its default application.</summary>
    void OpenFile(string path);
}
