namespace AudioTastic.App;

/// <summary>SI-06 / SI-08: what to do with files given on the command line.</summary>
public enum FileAction
{
    /// <summary>Files opened from the OS (double-click): Settings › System integration decides (play or enqueue).</summary>
    Default,

    /// <summary>--open: add the files and play the first ("Open in AudioTastic").</summary>
    Open,

    /// <summary>--enqueue: only add the files ("Enqueue in AudioTastic").</summary>
    Enqueue,
}

/// <summary>
/// Command-line switches understood so far. The full CLI (SI-08) arrives later in P10.
/// --portable is handled by AppPaths (TL-05). Arguments that are not switches are files (P1). On Windows the
/// switches may also start with '/' (e.g. /enqueue); elsewhere '/' starts an absolute path.
/// </summary>
public sealed record StartupOptions(bool CrashTest, IReadOnlyList<string> Files, FileAction Action = FileAction.Default)
{
    /// <summary>Throws an exception on the UI thread after start-up, to test crash reporting (TL-03).</summary>
    public const string CrashTestSwitch = "--crash-test";

    public const string OpenSwitch = "--open";

    public const string EnqueueSwitch = "--enqueue";

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        var crashTest = false;
        var action = FileAction.Default;
        var files = new List<string>();
        foreach (var arg in args)
        {
            if (SwitchName(arg) is { } name)
            {
                switch (name)
                {
                    case "crash-test":
                        crashTest = true;
                        break;
                    case "open":
                        action = FileAction.Open;
                        break;
                    case "enqueue":
                        action = FileAction.Enqueue;
                        break;
                }
                continue;
            }
            files.Add(Path.GetFullPath(arg));
        }
        return new StartupOptions(crashTest, files, action);
    }

    /// <summary>The switch's name in lower case without its prefix, or null when the argument is a file.</summary>
    private static string? SwitchName(string arg)
    {
        if (arg.StartsWith("--", StringComparison.Ordinal))
        {
            return arg[2..].ToLowerInvariant();
        }
        // "/enqueue" style only on Windows, and only for names without a path separator or dot.
        if (OperatingSystem.IsWindows() && arg.Length > 1 && arg[0] == '/' && arg.Skip(1).All(c => char.IsAsciiLetter(c) || c == '-'))
        {
            return arg[1..].ToLowerInvariant();
        }
        return null;
    }
}
