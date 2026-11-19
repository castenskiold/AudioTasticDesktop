using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace AudioTastic.Platform.Windows;

/// <summary>Windows implementation of <see cref="IPlatformServices"/>.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsPlatformServices : IPlatformServices
{
    private const int AsfwAny = -1;

    public string Name => "Windows";

    public void OpenFolder(string path) => ProcessLauncher.Start("explorer.exe", path);

    public void OpenFile(string path) => ProcessLauncher.ShellOpen(path);

    public IShellIntegration Shell { get; } = new WindowsShellIntegration();

    public void AllowOtherProcessToActivate() => AllowSetForegroundWindow(AsfwAny);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
