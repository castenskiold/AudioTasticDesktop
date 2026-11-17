namespace AudioTastic.App;

/// <summary>
/// Command-line switches understood so far. The full CLI (SI-08) arrives in P10.
/// --portable is handled by AppPaths (TL-05). Arguments that are not switches are files to play (P1).
/// </summary>
public sealed record StartupOptions(bool CrashTest, IReadOnlyList<string> Files)
{
    /// <summary>Throws an exception on the UI thread after start-up, to test crash reporting (TL-03).</summary>
    public const string CrashTestSwitch = "--crash-test";

    public static StartupOptions Parse(IReadOnlyList<string> args) => new(
        CrashTest: args.Any(a => string.Equals(a, CrashTestSwitch, StringComparison.OrdinalIgnoreCase)),
        Files: [.. args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).Select(Path.GetFullPath)]);
}
