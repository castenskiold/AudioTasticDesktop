namespace AudioTastic.Metadata;

/// <summary>
/// What counts as "TagLib could not read (or write) this file". TagLib parses whatever users have on disk, and on damaged
/// data it fails with many exception types, not only its own CorruptFileException (P14 fuzzing found
/// KeyNotFoundException from a damaged Ogg file). One definition for every place that calls TagLib, so a bad file is
/// skipped (no tags, no art) instead of failing the caller. Programming errors in our own code (NullReference from our
/// side, OutOfMemory) are not listed on purpose... except where TagLib itself raises them on bad input.
/// </summary>
internal static class TagLibErrors
{
    public static bool IsFileProblem(Exception ex) =>
        ex is TagLib.UnsupportedFormatException or TagLib.CorruptFileException
            or IOException or UnauthorizedAccessException
            or NotImplementedException or NotSupportedException
            or ArgumentException or IndexOutOfRangeException or OverflowException
            or KeyNotFoundException or InvalidOperationException or InvalidCastException or FormatException
            or NullReferenceException or ArithmeticException or System.Text.DecoderFallbackException;
}
