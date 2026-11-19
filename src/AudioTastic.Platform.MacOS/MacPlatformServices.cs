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

    public void AllowOtherProcessToActivate()
    {
    }

    public void AttachToParentConsole()
    {
    }
}
