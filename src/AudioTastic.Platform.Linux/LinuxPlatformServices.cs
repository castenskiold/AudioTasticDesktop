using System.Runtime.Versioning;

namespace AudioTastic.Platform.Linux;

/// <summary>Linux implementation of <see cref="IPlatformServices"/>.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPlatformServices : IPlatformServices
{
    public string Name => "Linux";

    public void OpenFolder(string path) => ProcessLauncher.Start("xdg-open", path);

    public void OpenFile(string path) => ProcessLauncher.Start("xdg-open", path);

    /// <summary>SI-05 for this OS comes later (see the feature card).</summary>
    public IShellIntegration Shell => NoShellIntegration.Instance;

    public void AllowOtherProcessToActivate()
    {
    }

    public void AttachToParentConsole()
    {
    }
}
