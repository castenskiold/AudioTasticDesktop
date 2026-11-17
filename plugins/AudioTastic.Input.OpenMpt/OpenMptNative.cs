using System.Runtime.InteropServices;
using AudioTastic.Sdk.Interop;

namespace AudioTastic.Input.OpenMpt;

/// <summary>FM-07: the parts of the libopenmpt C API (libopenmpt.h, API version 0.x) the plugin uses.</summary>
internal static unsafe partial class OpenMptNative
{
    private const string Library = "libopenmpt";

    static OpenMptNative()
    {
        // In the app the plugin's load context finds the library in the plugin folder; in tests and tools this
        // resolver finds it in runtimes/<rid>/native beside the executable.
        NativeLibraries.RegisterResolver(typeof(OpenMptNative).Assembly);
    }

    /// <summary>True when libopenmpt can be loaded on this machine.</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return GetString("library_version") is { Length: > 0 };
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                return false;
            }
        }
    }

    [LibraryImport(Library)]
    public static partial nint openmpt_module_create_from_memory2(byte* filedata, nuint filesize, nint logfunc, nint loguser,
        nint errfunc, nint erruser, int* error, byte** errorMessage, nint initialCtls);

    [LibraryImport(Library)]
    public static partial void openmpt_module_destroy(nint module);

    [LibraryImport(Library)]
    public static partial int openmpt_module_set_repeat_count(nint module, int repeatCount);

    [LibraryImport(Library)]
    public static partial double openmpt_module_get_duration_seconds(nint module);

    [LibraryImport(Library)]
    public static partial double openmpt_module_set_position_seconds(nint module, double seconds);

    [LibraryImport(Library)]
    public static partial nuint openmpt_module_read_interleaved_float_stereo(nint module, int sampleRate, nuint count, float* interleavedStereo);

    [LibraryImport(Library)]
    public static partial int openmpt_module_get_num_channels(nint module);

    [LibraryImport(Library)]
    private static partial byte* openmpt_module_get_metadata(nint module, byte* key);

    [LibraryImport(Library)]
    private static partial byte* openmpt_get_string(byte* key);

    [LibraryImport(Library)]
    public static partial void openmpt_free_string(byte* str);

    /// <summary>A log callback that ignores libopenmpt's messages (the default writes to stderr).</summary>
    public static nint SilentLogFunction { get; } = (nint)(delegate* unmanaged[Cdecl]<byte*, void*, void>)&IgnoreLog;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void IgnoreLog(byte* message, void* user)
    {
    }

    public static string? GetMetadata(nint module, string key) => TakeString(WithKey(key, k => openmpt_module_get_metadata(module, k)));

    public static string? GetString(string key) => TakeString(WithKey(key, openmpt_get_string));

    /// <summary>Copies a string returned by libopenmpt and frees it.</summary>
    public static string? TakeString(byte* text)
    {
        if (text == null)
        {
            return null;
        }
        try
        {
            var value = Marshal.PtrToStringUTF8((nint)text);
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        finally
        {
            openmpt_free_string(text);
        }
    }

    private delegate byte* KeyCall(byte* key);

    private static byte* WithKey(string key, KeyCall call)
    {
        var bytes = System.Text.Encoding.ASCII.GetBytes(key + "\0");
        fixed (byte* k = bytes)
        {
            return call(k);
        }
    }
}
