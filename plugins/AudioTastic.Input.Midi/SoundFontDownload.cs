using System.Security.Cryptography;

namespace AudioTastic.Input.Midi;

/// <summary>
/// FM-08: downloads GeneralUser GS v2.0.3 (S. Christian Collins) from AudioTastic's GitHub release when the user asks
/// for it on the settings page. It is no longer bundled (SignPath signing allows only open-source components in the
/// package; Anders, 19 Nov 2026). The file is checked against its SHA-256 before it replaces anything, and its
/// licence is saved next to it.
/// </summary>
internal sealed class SoundFontDownload(HttpClient http)
{
    public const string FileName = "GeneralUser-GS.sf2";
    public const string LicenseFileName = "LICENSE.txt";
    public const string ReleaseUrl = "https://github.com/castenskiold/AudioTasticDesktop/releases/download/soundfont-generaluser-gs-2.0.3/";
    public const string Sha256 = "9575028c7a1f589f5770fccc8cff2734566af40cd26ed836944e9a5152688cfe";
    public const long Size = 32_319_396;

    /// <summary>Downloads into <paramref name="folder"/>; reports progress 0–1. Throws <see cref="InvalidDataException"/> when the file does not match.</summary>
    public async Task DownloadAsync(string folder, IProgress<double>? progress, CancellationToken cancellationToken,
        string url = ReleaseUrl + FileName, string expectedSha256 = Sha256, string? licenseUrl = ReleaseUrl + LicenseFileName)
    {
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, FileName);
        var partial = target + ".part";
        try
        {
            using (var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var length = response.Content.Headers.ContentLength ?? Size;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                long done = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    hash.AppendData(buffer, 0, read);
                    done += read;
                    progress?.Report(Math.Min(1, done / (double)Math.Max(1, length)));
                }
                var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException($"The downloaded SoundFont is not the expected file (SHA-256 {actual}).");
                }
            }
            File.Move(partial, target, overwrite: true);
            if (licenseUrl is not null)
            {
                await File.WriteAllBytesAsync(Path.Combine(folder, LicenseFileName),
                    await http.GetByteArrayAsync(licenseUrl, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }
        }
    }
}
