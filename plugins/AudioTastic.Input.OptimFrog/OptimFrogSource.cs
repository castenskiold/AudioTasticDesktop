using System.Buffers.Binary;
using System.Runtime.InteropServices;
using AudioTastic.Sdk.Audio;
using static AudioTastic.Input.OptimFrog.OptimFrogNative;

namespace AudioTastic.Input.OptimFrog;

/// <summary>
/// FM-06: decodes OptimFROG Lossless (.ofr) and DualStream (.ofs) files with the OptimFROG SDK. The file is read
/// through the SDK's stream interface over a .NET <see cref="FileStream"/>, so any path (Unicode) works.
/// Samples come back packed as in the original file (8/16/24/32-bit integer or 32-bit float) and are converted to
/// float exactly like the other decoders (integer / 2^(bits-1)), so lossless output is bit-identical to the WAV.
/// </summary>
internal sealed unsafe class OptimFrogSource : IAudioSource
{
    private readonly FileStream _file;
    private GCHandle _fileHandle;
    private ReadInterface* _readInterface;
    private nint _decoder;
    private readonly int _bytesPerSample;
    private readonly SampleKind _kind;
    private byte[] _raw = new byte[16384];

    internal enum SampleKind { Signed, Unsigned, Float }

    private OptimFrogSource(string path)
    {
        try
        {
            _file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AudioSourceException($"Could not open '{path}': {ex.Message}", ex);
        }
        try
        {
            _fileHandle = GCHandle.Alloc(_file);
            _readInterface = (ReadInterface*)NativeMemory.AllocZeroed((nuint)sizeof(ReadInterface));
            *_readInterface = new ReadInterface
            {
                Close = &StreamClose,
                Read = &StreamRead,
                Eof = &StreamEof,
                Seekable = &StreamSeekable,
                Length = &StreamLength,
                GetPos = &StreamGetPos,
                Seek = &StreamSeek,
            };
            _decoder = OptimFROG_createInstance();
            if (_decoder == 0)
            {
                throw new AudioSourceException("OptimFROG could not create a decoder (out of memory).");
            }
            if (OptimFROG_openExt(_decoder, _readInterface, GCHandle.ToIntPtr(_fileHandle), 1) == 0)
            {
                throw new AudioSourceException($"Not a valid OptimFROG file: '{path}'.");
            }
            Info info;
            if (OptimFROG_getInfo(_decoder, &info) == 0)
            {
                throw new AudioSourceException($"OptimFROG could not read the stream info of '{path}'.");
            }
            var sampleType = Marshal.PtrToStringAnsi((nint)info.SampleType) ?? "";
            (_kind, _bytesPerSample) = ParseSampleType(sampleType, (int)info.BitsPerSample);
            var channels = (int)info.Channels;
            var rate = (int)info.SampleRate;
            if (channels < 1 || rate < 1)
            {
                throw new AudioSourceException($"Unsupported OptimFROG stream ({channels} ch, {rate} Hz).");
            }
            Info = new AudioStreamInfo(Path.GetExtension(path).Equals(".ofs", StringComparison.OrdinalIgnoreCase) ? "optimfrog-dualstream" : "optimfrog",
                rate, channels, (int)info.BitsPerSample, info.CompressedSize > 0 && info.LengthMs > 0 ? info.CompressedSize * 8000 / info.LengthMs : 0,
                IsLossless: !Path.GetExtension(path).Equals(".ofs", StringComparison.OrdinalIgnoreCase));
            LengthFrames = info.NoPoints;
            CanSeek = OptimFROG_seekable(_decoder) != 0;
            Tags = ReadTags();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public static OptimFrogSource Open(string path) => new(path);

    public AudioStreamInfo Info { get; } = null!;
    public AudioFormat Format => Info.OutputFormat;
    public long? LengthFrames { get; }
    public long PositionFrames { get; private set; }
    public bool CanSeek { get; }
    public IReadOnlyDictionary<string, string> Tags { get; } = new Dictionary<string, string>();

    public int Read(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(_decoder == 0, this);
        var channels = Format.Channels;
        if (buffer.Length % channels != 0)
        {
            throw new ArgumentException($"Buffer length must be a multiple of the channel count ({channels}).", nameof(buffer));
        }
        var points = buffer.Length / channels;
        var bytes = points * channels * _bytesPerSample;
        if (_raw.Length < bytes)
        {
            _raw = new byte[bytes];
        }
        int read;
        fixed (byte* raw = _raw)
        {
            read = OptimFROG_read(_decoder, raw, (uint)points, 0);
        }
        if (read < 0)
        {
            throw new AudioSourceException("OptimFROG reported a decoding error.");
        }
        Convert(_raw.AsSpan(0, read * channels * _bytesPerSample), buffer[..(read * channels)]);
        PositionFrames += read;
        return read;
    }

    public void Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(_decoder == 0, this);
        if (!CanSeek)
        {
            throw new NotSupportedException("This OptimFROG stream cannot seek.");
        }
        var target = Math.Clamp(frame, 0, LengthFrames ?? long.MaxValue);
        if (OptimFROG_seekPoint(_decoder, target) == 0)
        {
            throw new AudioSourceException($"OptimFROG could not seek to frame {target}.");
        }
        PositionFrames = target;
    }

    public void Dispose()
    {
        if (_decoder != 0)
        {
            OptimFROG_close(_decoder);
            OptimFROG_destroyInstance(_decoder);
            _decoder = 0;
        }
        if (_readInterface != null)
        {
            NativeMemory.Free(_readInterface);
            _readInterface = null;
        }
        if (_fileHandle.IsAllocated)
        {
            _fileHandle.Free();
        }
        _file?.Dispose();
    }

    internal static (SampleKind Kind, int Bytes) ParseSampleType(string sampleType, int bits)
    {
        var kind = sampleType.StartsWith("FLOAT", StringComparison.OrdinalIgnoreCase) ? SampleKind.Float
            : sampleType.StartsWith("UINT", StringComparison.OrdinalIgnoreCase) ? SampleKind.Unsigned
            : SampleKind.Signed;
        var bytes = bits switch
        {
            8 => 1,
            16 => 2,
            24 => 3,
            32 => 4,
            _ => throw new AudioSourceException($"Unsupported OptimFROG sample type {sampleType} ({bits} bit)."),
        };
        if (kind == SampleKind.Float && bytes != 4)
        {
            throw new AudioSourceException($"Unsupported OptimFROG sample type {sampleType}.");
        }
        return (kind, bytes);
    }

    private void Convert(ReadOnlySpan<byte> raw, Span<float> destination)
    {
        var scale = 1.0 / (1L << ((_bytesPerSample * 8) - 1));
        for (var i = 0; i < destination.Length; i++)
        {
            var s = raw.Slice(i * _bytesPerSample, _bytesPerSample);
            destination[i] = _kind switch
            {
                SampleKind.Float => BinaryPrimitives.ReadSingleLittleEndian(s),
                SampleKind.Signed => _bytesPerSample switch
                {
                    1 => (sbyte)s[0] / 128f,
                    2 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                    3 => ((s[0] | (s[1] << 8) | (s[2] << 16)) << 8 >> 8) / 8388608f,
                    _ => (float)(BinaryPrimitives.ReadInt32LittleEndian(s) * scale),
                },
                _ => _bytesPerSample switch
                {
                    1 => (s[0] - 128) / 128f,
                    2 => (BinaryPrimitives.ReadUInt16LittleEndian(s) - 32768) / 32768f,
                    3 => ((s[0] | (s[1] << 8) | (s[2] << 16)) - 8388608) / 8388608f,
                    _ => (float)((BinaryPrimitives.ReadUInt32LittleEndian(s) - 2147483648.0) * scale),
                },
            };
        }
    }

    private Dictionary<string, string> ReadTags()
    {
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var block = NativeMemory.AllocZeroed(TagsSize);
        try
        {
            if (OptimFROG_getTags(_decoder, block) == 0)
            {
                return tags;
            }
            var count = Math.Min(64, *(uint*)block);
            var keys = (nint*)((byte*)block + 4);
            var values = keys + 64;
            for (var i = 0; i < count; i++)
            {
                if (Marshal.PtrToStringUTF8(keys[i]) is { Length: > 0 } key && Marshal.PtrToStringUTF8(values[i]) is { } value)
                {
                    tags.TryAdd(key.ToLowerInvariant(), value);
                }
            }
            OptimFROG_freeTags(block);
        }
        finally
        {
            NativeMemory.Free(block);
        }
        return tags;
    }

    // ---- SDK stream interface over the FileStream (called on the decoder thread, inside OptimFROG calls) ----

    private static FileStream StreamOf(nint instance) => (FileStream)GCHandle.FromIntPtr(instance).Target!;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static byte StreamClose(nint instance) => 1; // the source closes the file itself

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static int StreamRead(nint instance, void* destination, uint count)
    {
        try
        {
            return StreamOf(instance).Read(new Span<byte>(destination, (int)Math.Min(count, int.MaxValue)));
        }
        catch (IOException)
        {
            return -1;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static byte StreamEof(nint instance)
    {
        var file = StreamOf(instance);
        return (byte)(file.Position >= file.Length ? 1 : 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static byte StreamSeekable(nint instance) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static long StreamLength(nint instance) => StreamOf(instance).Length;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static long StreamGetPos(nint instance) => StreamOf(instance).Position;

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static byte StreamSeek(nint instance, long position)
    {
        try
        {
            StreamOf(instance).Position = position;
            return 1;
        }
        catch (IOException)
        {
            return 0;
        }
    }
}
