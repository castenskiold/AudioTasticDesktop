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

    /// <summary>SI-02: the OS media controls (now playing, media keys, overlay).</summary>
    IMediaControls MediaControls { get; }

    /// <summary>SI-01: system-wide hotkeys.</summary>
    IHotkeyService Hotkeys { get; }

    /// <summary>SI-03 [Windows only]: taskbar thumbnail buttons and progress.</summary>
    ITaskbar Taskbar { get; }

    /// <summary>
    /// SI-06: called by a second start before it hands its files to the running player, so that the player may
    /// bring its window to the front (Windows only lets the process the user just started do that).
    /// </summary>
    void AllowOtherProcessToActivate();

    /// <summary>
    /// SI-08: lets a GUI program print to the console it was started from (--help). Windows gives a GUI program no
    /// console of its own; elsewhere standard output already works.
    /// </summary>
    void AttachToParentConsole();

    /// <summary>
    /// SI-02 / SI-07: gives the process an app identity, so the OS media controls and notifications show
    /// <paramref name="displayName"/> and <paramref name="iconPath"/> instead of "Unknown app". Call before any window
    /// is created. <paramref name="writeToSystem"/> false sets the identity without writing anything to the system
    /// (portable mode).
    /// </summary>
    void SetAppIdentity(string appId, string displayName, string iconPath, bool writeToSystem);
}
