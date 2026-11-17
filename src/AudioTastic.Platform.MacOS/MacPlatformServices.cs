using System.Runtime.Versioning;

namespace AudioTastic.Platform.MacOS;

/// <summary>macOS implementation of <see cref="IPlatformServices"/>.</summary>
[SupportedOSPlatform("macos")]
public sealed class MacPlatformServices : IPlatformServices
{
    public string Name => "macOS";

    public void OpenFolder(string path) => ProcessLauncher.Start("open", path);

    public void OpenFile(string path) => ProcessLauncher.Start("open", path);
}
