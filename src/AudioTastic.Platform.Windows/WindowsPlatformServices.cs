using System.Runtime.Versioning;

namespace AudioTastic.Platform.Windows;

/// <summary>Windows implementation of <see cref="IPlatformServices"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsPlatformServices : IPlatformServices
{
    public string Name => "Windows";

    public void OpenFolder(string path) => ProcessLauncher.Start("explorer.exe", path);

    public void OpenFile(string path) => ProcessLauncher.ShellOpen(path);
}
