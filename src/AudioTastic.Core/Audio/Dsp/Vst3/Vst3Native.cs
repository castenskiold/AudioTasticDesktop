using System.Runtime.InteropServices;

namespace AudioTastic.Core.Audio.Dsp.Vst3;

/// <summary>
/// EQ-06: P/Invoke declarations for the AudioTastic VST3 host shim (native/vst3host/at_vst3host.h), built into the
/// "at_vst3host" native library by native/CMakeLists.txt. Keep in step with <see cref="ApiVersionExpected"/>.
/// </summary>
internal static unsafe partial class Vst3Native
{
    public const int ApiVersionExpected = 2;
    public const string Library = "at_vst3host";

    static Vst3Native() => AudioTastic.Sdk.Interop.NativeLibraries.RegisterResolver(typeof(Vst3Native).Assembly);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void ResizeCallback(nint user, int width, int height);

    [LibraryImport(Library, EntryPoint = "at_vst3_api_version")]
    public static partial int ApiVersion();

    [LibraryImport(Library, EntryPoint = "at_vst3_open", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Open(string path, string classId, byte* error, int errorCapacity);

    [LibraryImport(Library, EntryPoint = "at_vst3_setup")]
    public static partial int Setup(nint plugin, double sampleRate, int maxFrames);

    [LibraryImport(Library, EntryPoint = "at_vst3_process")]
    public static partial int Process(nint plugin, float* interleaved, int frames);

    [LibraryImport(Library, EntryPoint = "at_vst3_latency")]
    public static partial int Latency(nint plugin);

    [LibraryImport(Library, EntryPoint = "at_vst3_flush_output")]
    public static partial int FlushOutput(nint plugin);

    [LibraryImport(Library, EntryPoint = "at_vst3_take_restart_flags")]
    public static partial int TakeRestartFlags(nint plugin);

    [LibraryImport(Library, EntryPoint = "at_vst3_get_state")]
    public static partial int GetState(nint plugin, byte* buffer, int capacity);

    [LibraryImport(Library, EntryPoint = "at_vst3_set_state")]
    public static partial int SetState(nint plugin, byte* buffer, int length);

    [LibraryImport(Library, EntryPoint = "at_vst3_has_editor")]
    public static partial int HasEditor(nint plugin);

    [LibraryImport(Library, EntryPoint = "at_vst3_editor_open")]
    public static partial int EditorOpen(nint plugin, nint parent, out int width, out int height, nint resize, nint user);

    [LibraryImport(Library, EntryPoint = "at_vst3_editor_close")]
    public static partial void EditorClose(nint plugin);

    [LibraryImport(Library, EntryPoint = "at_vst3_close")]
    public static partial void Close(nint plugin);
}
