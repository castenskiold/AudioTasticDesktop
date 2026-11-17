namespace AudioTastic.Data.Tests;

/// <summary>A unique temporary folder, deleted on dispose.</summary>
internal sealed class TempDir : IDisposable
{
    public TempDir()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "audiotastic-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string Combine(params string[] parts) => System.IO.Path.Combine([Path, .. parts]);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
            // SQLite may still hold the file briefly on Windows; leftovers in %TEMP% are harmless.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
