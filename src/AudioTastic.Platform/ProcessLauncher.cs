using System.Diagnostics;

namespace AudioTastic.Platform;

/// <summary>Small helper for starting OS tools without a console window.</summary>
public static class ProcessLauncher
{
    /// <summary>Starts <paramref name="fileName"/> with a single argument (passed unquoted-safe via ArgumentList).</summary>
    public static void Start(string fileName, string argument)
    {
        var info = new ProcessStartInfo(fileName) { UseShellExecute = false, CreateNoWindow = true };
        info.ArgumentList.Add(argument);
        using var _ = Process.Start(info);
    }

    /// <summary>Opens a path through the OS shell (default app / file manager).</summary>
    public static void ShellOpen(string path)
    {
        using var _ = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
    }
}
