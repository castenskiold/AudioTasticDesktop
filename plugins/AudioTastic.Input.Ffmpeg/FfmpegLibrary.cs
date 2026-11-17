using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Interop;
using FFmpeg.AutoGen;

namespace AudioTastic.Input.Ffmpeg;

/// <summary>
/// FM-01: locates and loads the FFmpeg shared libraries (LGPL build from native/fetch_ffmpeg.py) once per process.
/// </summary>
public static class FfmpegLibrary
{
    /// <summary>libavcodec major version the bindings were generated for (FFmpeg 8.x).</summary>
    public const int AvCodecMajor = 62;

    private static readonly Lock InitLock = new();
    private static string? _version;
    private static Exception? _loadError;

    /// <summary>FFmpeg version string, e.g. "n8.1-..."; loads the libraries on first use.</summary>
    public static string Version
    {
        get
        {
            EnsureLoaded();
            return _version!;
        }
    }

    /// <summary>True when the FFmpeg libraries can be loaded.</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                EnsureLoaded();
                return true;
            }
            catch (AudioSourceException)
            {
                return false;
            }
        }
    }

    /// <summary>Loads the libraries; throws <see cref="AudioSourceException"/> with a helpful message when they are missing.</summary>
    public static void EnsureLoaded()
    {
        lock (InitLock)
        {
            if (_version is not null)
            {
                return;
            }
            if (_loadError is not null)
            {
                throw new AudioSourceException(_loadError.Message, _loadError);
            }
            try
            {
                var dir = NativeLibraries.FindDirectoryContaining(AvCodecFileName());
                if (dir is not null)
                {
                    ffmpeg.RootPath = dir;
                }
                var major = (int)(ffmpeg.avcodec_version() >> 16);
                if (major != AvCodecMajor)
                {
                    throw new InvalidOperationException(
                        $"FFmpeg libavcodec {major} found, but AudioTastic needs {AvCodecMajor} (FFmpeg 8.x).");
                }
                ffmpeg.av_log_set_level(ffmpeg.AV_LOG_QUIET);
                _version = ffmpeg.av_version_info();
            }
#pragma warning disable CA1031 // Any load failure (DllNotFound, EntryPointNotFound, ...) becomes one clear error.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _loadError = new AudioSourceException(
                    "FFmpeg libraries could not be loaded. Run 'python native/fetch_ffmpeg.py --rid <rid>' and rebuild. " +
                    $"Searched: {string.Join(", ", NativeLibraries.SearchDirectories)}. {ex.Message}", ex);
                throw _loadError;
            }
        }
    }

    private static string AvCodecFileName() =>
        OperatingSystem.IsWindows() ? $"avcodec-{AvCodecMajor}.dll"
        : OperatingSystem.IsMacOS() ? $"libavcodec.{AvCodecMajor}.dylib"
        : $"libavcodec.so.{AvCodecMajor}";
}
