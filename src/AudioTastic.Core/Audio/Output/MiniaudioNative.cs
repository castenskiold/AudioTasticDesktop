using System.Runtime.InteropServices;

namespace AudioTastic.Core.Audio.Output;

/// <summary>
/// P/Invoke declarations for the AudioTastic miniaudio shim (native/miniaudio/at_miniaudio.h), built into the
/// "miniaudio" native library by native/CMakeLists.txt. Keep in step with <see cref="ApiVersionExpected"/>.
/// </summary>
internal static unsafe partial class MiniaudioNative
{
    public const int ApiVersionExpected = 2;
    public const int MaxFormats = 64;
    public const int IdBytes = 512;
    public const int NameBytes = 256;

    public const int NotifyStarted = 0;
    public const int NotifyStopped = 1;
    public const int NotifyRerouted = 2;
    public const int NotifyInterruptionBegan = 3;
    public const int NotifyInterruptionEnded = 4;

    private const string Library = "miniaudio";

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfo
    {
        public fixed byte Id[IdBytes];
        public fixed byte Name[NameBytes];
        public int IsDefault;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct NativeFormat
    {
        public int Format;
        public uint Channels;
        public uint SampleRate;
        public int ExclusiveOnly;
    }

    [LibraryImport(Library, EntryPoint = "at_ma_device_formats")]
    public static partial int DeviceFormats(nint context, byte* deviceId, NativeFormat* formats, uint capacity, out uint total);

    [LibraryImport(Library, EntryPoint = "at_ma_device_internal_format")]
    public static partial int DeviceInternalFormat(nint device);

    [LibraryImport(Library, EntryPoint = "at_ma_api_version")]
    public static partial int ApiVersion();

    [LibraryImport(Library, EntryPoint = "at_ma_context_create")]
    public static partial int ContextCreate(int useNullBackend, out nint context);

    [LibraryImport(Library, EntryPoint = "at_ma_context_destroy")]
    public static partial void ContextDestroy(nint context);

    [LibraryImport(Library, EntryPoint = "at_ma_context_backend_name")]
    public static partial nint ContextBackendName(nint context);

    [LibraryImport(Library, EntryPoint = "at_ma_enumerate_playback")]
    public static partial int EnumeratePlayback(nint context, DeviceInfo* infos, uint capacity, out uint total);

    [LibraryImport(Library, EntryPoint = "at_ma_device_open")]
    public static partial int DeviceOpen(nint context, byte* deviceId, uint sampleRate, uint channels, int format, int exclusive,
        uint bufferMilliseconds, delegate* unmanaged<nint, void*, uint, void> dataProc, delegate* unmanaged<nint, int, void> notifyProc,
        nint user, out nint device);

    [LibraryImport(Library, EntryPoint = "at_ma_device_start")]
    public static partial int DeviceStart(nint device);

    [LibraryImport(Library, EntryPoint = "at_ma_device_stop")]
    public static partial int DeviceStop(nint device);

    [LibraryImport(Library, EntryPoint = "at_ma_device_close")]
    public static partial void DeviceClose(nint device);

    [LibraryImport(Library, EntryPoint = "at_ma_device_internal_sample_rate")]
    public static partial uint DeviceInternalSampleRate(nint device);

    [LibraryImport(Library, EntryPoint = "at_ma_device_period_frames")]
    public static partial uint DevicePeriodFrames(nint device);

    [LibraryImport(Library, EntryPoint = "at_ma_device_name")]
    public static partial int DeviceName(nint device, byte* buffer, uint bufferBytes);
}
