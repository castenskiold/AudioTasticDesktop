using System.Runtime.InteropServices;
using AudioTastic.Sdk.Interop;

namespace AudioTastic.Input.OptimFrog;

/// <summary>
/// FM-06: the OptimFROG SDK 1.600 C interface (OptimFROG.h). The library exists for x64 only (Windows, Linux, macOS).
/// Structures are packed (pragma pack 1 / __attribute__((packed)) in the header).
/// </summary>
internal static unsafe partial class OptimFrogNative
{
    private const string Library = "OptimFROG";

    static OptimFrogNative()
    {
        // In the app the plugin's load context finds the library in the plugin folder; in tests this resolver finds
        // it in runtimes/<rid>/native beside the executable.
        NativeLibraries.RegisterResolver(typeof(OptimFrogNative).Assembly);
    }

    /// <summary>True when the OptimFROG library can be loaded on this machine.</summary>
    public static bool IsAvailable
    {
        get
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
            {
                return false;
            }
            try
            {
                return OptimFROG_getVersion() > 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                return false;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct ReadInterface
    {
        public delegate* unmanaged[Cdecl]<nint, byte> Close;
        public delegate* unmanaged[Cdecl]<nint, void*, uint, int> Read;
        public delegate* unmanaged[Cdecl]<nint, byte> Eof;
        public delegate* unmanaged[Cdecl]<nint, byte> Seekable;
        public delegate* unmanaged[Cdecl]<nint, long> Length;
        public delegate* unmanaged[Cdecl]<nint, long> GetPos;
        public delegate* unmanaged[Cdecl]<nint, long, byte> Seek;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Info
    {
        public uint Channels;
        public uint SampleRate;
        public uint BitsPerSample;
        public uint Bitrate;
        public uint Version;
        public byte* Method;
        public byte* Speedup;
        public long NoPoints;
        public long OriginalSize;
        public long CompressedSize;
        public long LengthMs;
        public byte* SampleType;
        public byte* ChannelConfig;
    }

    /// <summary>OptimFROG_Tags: keyCount followed by 64 key pointers and 64 value pointers (packed).</summary>
    public const int TagsSize = 4 + (2 * 64 * 8);

    [LibraryImport(Library)]
    public static partial uint OptimFROG_getVersion();

    [LibraryImport(Library)]
    public static partial nint OptimFROG_createInstance();

    [LibraryImport(Library)]
    public static partial void OptimFROG_destroyInstance(nint decoder);

    [LibraryImport(Library)]
    public static partial byte OptimFROG_openExt(nint decoder, ReadInterface* readInterface, nint readerInstance, byte readTags);

    [LibraryImport(Library)]
    public static partial byte OptimFROG_close(nint decoder);

    [LibraryImport(Library)]
    public static partial byte OptimFROG_seekable(nint decoder);

    [LibraryImport(Library)]
    public static partial byte OptimFROG_seekPoint(nint decoder, long point);

    [LibraryImport(Library)]
    public static partial long OptimFROG_getPos(nint decoder);

    [LibraryImport(Library)]
    public static partial int OptimFROG_read(nint decoder, void* data, uint noPoints, byte max16bit);

    [LibraryImport(Library)]
    public static partial byte OptimFROG_getInfo(nint decoder, Info* info);

    [LibraryImport(Library)]
    public static partial byte OptimFROG_getTags(nint decoder, void* tags);

    [LibraryImport(Library)]
    public static partial void OptimFROG_freeTags(void* tags);
}
