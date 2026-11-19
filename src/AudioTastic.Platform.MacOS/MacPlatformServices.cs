using System.Runtime.Versioning;

namespace AudioTastic.Platform.MacOS;

/// <summary>macOS implementation of <see cref="IPlatformServices"/>.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacPlatformServices : IPlatformServices
{
    public string Name => "macOS";

    public void OpenFolder(string path) => ProcessLauncher.Start("open", path);

    public void OpenFile(string path) => ProcessLauncher.Start("open", path);

    /// <summary>SI-05 for this OS comes later (see the feature card).</summary>
    public IShellIntegration Shell => NoShellIntegration.Instance;

    /// <summary>SI-02 for this OS comes later (Now Playing on macOS, MPRIS on Linux).</summary>
    public IMediaControls MediaControls => NoMediaControls.Instance;

    /// <summary>SI-01 for this OS comes later.</summary>
    public IHotkeyService Hotkeys => NoHotkeys.Instance;

    /// <summary>SI-03 is Windows only (a macOS Dock menu may come later).</summary>
    public ITaskbar Taskbar => NoTaskbar.Instance;

    public void AllowOtherProcessToActivate()
    {
    }

    public void AttachToParentConsole()
    {
    }

    public void SetAppIdentity(string appId, string displayName, string iconPath, bool writeToSystem)
    {
    }
}
