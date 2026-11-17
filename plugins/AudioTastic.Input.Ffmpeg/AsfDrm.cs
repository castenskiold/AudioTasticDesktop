namespace AudioTastic.Input.Ffmpeg;

/// <summary>
/// FM-04: detects DRM-protected WMA/ASF files, which FFmpeg cannot decode, so the user gets a clear message instead
/// of a decoder error. Looks for the ASF Content Encryption objects in the header.
/// </summary>
internal static class AsfDrm
{
    private const int HeaderBytes = 256 * 1024;

    // ASF GUIDs in file byte order.
    private static readonly byte[] HeaderObject = new Guid("75B22630-668E-11CF-A6D9-00AA0062CE6C").ToByteArray();
    private static readonly byte[] ContentEncryption = new Guid("2211B3FB-BD23-11D2-B4B7-00A0C955FC6E").ToByteArray();
    private static readonly byte[] ExtendedContentEncryption = new Guid("298AE614-2622-4C17-B935-DAE07EE9289C").ToByteArray();

    public static bool IsProtected(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".wma", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".asf", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
        try
        {
            using var file = File.OpenRead(path);
            var buffer = new byte[(int)Math.Min(HeaderBytes, file.Length)];
            file.ReadExactly(buffer);
            return IsProtected(buffer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false; // FFmpeg reports the real error
        }
    }

    public static bool IsProtected(ReadOnlySpan<byte> header) =>
        header.StartsWith(HeaderObject) && (header.IndexOf(ContentEncryption) >= 0 || header.IndexOf(ExtendedContentEncryption) >= 0);
}
