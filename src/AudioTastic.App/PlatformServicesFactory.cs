using AudioTastic.Platform;
using AudioTastic.Platform.Linux;
using AudioTastic.Platform.MacOS;
using AudioTastic.Platform.Windows;

namespace AudioTastic.App;

/// <summary>CO-01: picks the IPlatformServices implementation for the running OS.</summary>
internal static class PlatformServicesFactory
{
    public static IPlatformServices Create()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsPlatformServices();
        }
        if (OperatingSystem.IsMacOS())
        {
            return new MacPlatformServices();
        }
        if (OperatingSystem.IsLinux())
        {
            return new LinuxPlatformServices();
        }
        throw new PlatformNotSupportedException("AudioTastic supports Windows, macOS and Linux.");
    }
}
