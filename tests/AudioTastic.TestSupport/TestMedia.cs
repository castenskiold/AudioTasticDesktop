using Xunit;

namespace AudioTastic.TestSupport;

/// <summary>
/// Locates the generated test media (scripts/generate_test_media.py → tests/media/generated).
/// Tests call <see cref="Require"/>, which skips the test when the media has not been generated.
/// </summary>
public static class TestMedia
{
    /// <summary>Repository root (the folder containing AudioTastic.slnx).</summary>
    public static string RepoRoot { get; } = FindRepoRoot();

    public static string GeneratedFolder => Path.Combine(RepoRoot, "tests", "media", "generated");

    /// <summary>Full path of a generated file; skips the calling test if it does not exist.</summary>
    public static string Require(string fileName)
    {
        var path = Path.Combine(GeneratedFolder, fileName);
        Assert.SkipUnless(File.Exists(path),
            $"Test media '{fileName}' missing. Run: python scripts/generate_test_media.py");
        return path;
    }

    private static string FindRepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "AudioTastic.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("Repository root (AudioTastic.slnx) not found above " + AppContext.BaseDirectory);
    }
}
