using System.Runtime.Versioning;

namespace AudioTastic.Platform.Linux;

/// <summary>Linux implementation of <see cref="IPlatformServices"/>.</summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxPlatformServices : IPlatformServices
{
    public string Name => "Linux";

    public void OpenFolder(string path) => ProcessLauncher.Start("xdg-open", path);

    public void OpenFile(string path) => ProcessLauncher.Start("xdg-open", path);
}
