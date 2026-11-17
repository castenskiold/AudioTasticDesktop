using AudioTastic.Sdk.Audio;

namespace AudioTastic.Input.OpenMpt;

/// <summary>
/// FM-07: plays a tracker module (MOD, XM, IT, S3M, ...) through libopenmpt at 48 kHz stereo. The song is played
/// once (repeat count 0), so the playlist continues; interpolation and stereo separation use libopenmpt's defaults.
/// The codec shown is the module type plus its channel count, e.g. "XM 8ch".
/// </summary>
internal sealed unsafe class OpenMptSource : IAudioSource
{
    public const int SampleRate = 48_000;
    private nint _module;

    private OpenMptSource(nint module)
    {
        _module = module;
        _ = OpenMptNative.openmpt_module_set_repeat_count(module, 0); // play once (always succeeds for 0)
        var type = OpenMptNative.GetMetadata(module, "type")?.ToUpperInvariant() ?? "MOD";
        var channels = OpenMptNative.openmpt_module_get_num_channels(module);
        Info = new AudioStreamInfo($"{type} {channels}ch", SampleRate, 2, 0, 0, IsLossless: false);
        LengthFrames = (long)Math.Round(OpenMptNative.openmpt_module_get_duration_seconds(module) * SampleRate);
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, name) in new[] { ("title", "title"), ("artist", "artist"), ("tracker", "encoder"), ("type_long", "format") })
        {
            if (OpenMptNative.GetMetadata(module, key) is { } value)
            {
                tags[name] = value;
            }
        }
        Tags = tags;
    }

    /// <summary>Loads a module file; throws <see cref="AudioSourceException"/> when libopenmpt cannot read it.</summary>
    public static OpenMptSource Open(string path)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new AudioSourceException($"Could not read '{path}': {ex.Message}", ex);
        }
        int error;
        byte* message = null;
        nint module;
        fixed (byte* bytes = data)
        {
            module = OpenMptNative.openmpt_module_create_from_memory2(bytes, (nuint)data.Length, OpenMptNative.SilentLogFunction, 0, 0, 0,
                &error, &message, 0);
        }
        var text = OpenMptNative.TakeString(message);
        if (module == 0)
        {
            throw new AudioSourceException($"Not a playable module: {text ?? $"libopenmpt error {error}"}");
        }
        return new OpenMptSource(module);
    }

    public AudioStreamInfo Info { get; }
    public AudioFormat Format => Info.OutputFormat;
    /// <summary>libopenmpt's song duration; once the end is reached, the exact number of frames rendered (a little more: release tails).</summary>
    public long? LengthFrames { get; private set; }
    public long PositionFrames { get; private set; }
    public bool CanSeek => true;
    public IReadOnlyDictionary<string, string> Tags { get; }

    public int Read(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(_module == 0, this);
        if (buffer.Length % 2 != 0)
        {
            throw new ArgumentException("Buffer length must be a multiple of the channel count (2).", nameof(buffer));
        }
        nuint frames;
        fixed (float* p = buffer)
        {
            frames = OpenMptNative.openmpt_module_read_interleaved_float_stereo(_module, SampleRate, (nuint)(buffer.Length / 2), p);
        }
        PositionFrames += (long)frames;
        if (frames == 0 && PositionFrames > LengthFrames)
        {
            LengthFrames = PositionFrames;
        }
        return (int)frames;
    }

    /// <summary>libopenmpt seeks by song position (row accurate); the reported position is the requested one.</summary>
    public void Seek(long frame)
    {
        ObjectDisposedException.ThrowIf(_module == 0, this);
        var target = Math.Clamp(frame, 0, LengthFrames ?? long.MaxValue);
        OpenMptNative.openmpt_module_set_position_seconds(_module, (double)target / SampleRate);
        PositionFrames = target;
    }

    public void Dispose()
    {
        if (_module != 0)
        {
            OpenMptNative.openmpt_module_destroy(_module);
            _module = 0;
        }
    }
}
