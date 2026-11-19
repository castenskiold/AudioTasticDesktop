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

    public IHotkeyService Hotkeys { get; } = new WindowsHotkeys();

#if WINDOWS
    public IMediaControls MediaControls { get; } =
        OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763) ? new WindowsMediaControls() : NoMediaControls.Instance;
#else
    public IMediaControls MediaControls => NoMediaControls.Instance; // built without the Windows SDK (not on Windows)
#endif

    public void AllowOtherProcessToActivate() => AllowSetForegroundWindow(AsfwAny);

    public void AttachToParentConsole() => AttachConsole(AttachParentProcess);

    /// <summary>
    /// The process's AppUserModelID (taskbar grouping, media controls, toasts) and, per user, its name and icon under
    /// HKCU\Software\Classes\AppUserModelId\&lt;id&gt;, which is how Windows names an app without a package.
    /// </summary>
    public void SetAppIdentity(string appId, string displayName, string iconPath, bool writeToSystem)
    {
        if (writeToSystem)
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\AppUserModelId\{appId}");
            key.SetValue("DisplayName", displayName);
            key.SetValue("IconUri", iconPath);
        }
        Marshal.ThrowExceptionForHR(SetCurrentProcessExplicitAppUserModelID(appId));
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    private const int AttachParentProcess = -1;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(int processId);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int processId);
}
