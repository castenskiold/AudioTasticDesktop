using System.Runtime.InteropServices;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Interop;

namespace AudioTastic.Core.Audio;

/// <summary>
/// PB-06: playback speed 0.5–2.0× through SoundTouch (LGPL, dynamically linked SoundTouchDLL). With "keep pitch" the
/// tempo changes and the pitch stays (time-stretch); without it speed and pitch change together (rate). At exactly
/// 1.0× SoundTouch is bypassed, so normal playback is untouched (bit-perfect). Runs on the deck's decoder thread; the
/// speed can be changed from any thread and applies from the next read. <see cref="PositionFrames"/> is the source
/// position of the next frame returned (SoundTouch's internal buffers taken into account).
/// </summary>
public sealed class TimeStretchSource : IAudioSource
{
    private const int ChunkFrames = 2048;

    private readonly IAudioSource _inner;
    private readonly int _channels;
    private readonly float[] _input;
    private nint _handle;
    private double _requestedSpeed = 1;
    private bool _requestedKeepPitch = true;
    private int _requestVersion;
    private int _appliedVersion = -1;
    private double _speed = 1;
    private bool _active;
    private bool _flushed;

    static TimeStretchSource() => NativeLibraries.RegisterResolver(typeof(TimeStretchSource).Assembly);

    public TimeStretchSource(IAudioSource inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _channels = inner.Format.Channels;
        _input = new float[ChunkFrames * _channels];
    }

    /// <summary>True when the SoundTouch library can be loaded.</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return SoundTouchNative.soundtouch_getVersionId() > 0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                return false;
            }
        }
    }

    public IAudioSource Inner => _inner;
    public AudioStreamInfo Info => _inner.Info;
    public AudioFormat Format => _inner.Format;
    public long? LengthFrames => _inner.LengthFrames;
    public bool CanSeek => _inner.CanSeek;
    public IReadOnlyDictionary<string, string> Tags => _inner.Tags;

    /// <summary>Source frames per output frame right now (1 when bypassed).</summary>
    public double Speed => _active ? _speed : 1;

    public long PositionFrames
    {
        get
        {
            if (!_active || _handle == 0)
            {
                return _inner.PositionFrames;
            }
            var pending = SoundTouchNative.soundtouch_numUnprocessedSamples(_handle) + (SoundTouchNative.soundtouch_numSamples(_handle) * _speed);
            return Math.Max(0, _inner.PositionFrames - (long)pending);
        }
    }

    /// <summary>Sets the speed (0.5–2.0) and whether the pitch is kept; takes effect on the next read.</summary>
    public void SetSpeed(double speed, bool keepPitch)
    {
        Volatile.Write(ref _requestedSpeed, Math.Clamp(speed, 0.5, 2.0));
        Volatile.Write(ref _requestedKeepPitch, keepPitch);
        Interlocked.Increment(ref _requestVersion);
    }

    public int Read(Span<float> buffer)
    {
        Apply();
        if (!_active)
        {
            return _inner.Read(buffer);
        }
        var frames = buffer.Length / _channels;
        var written = 0;
        while (written < frames)
        {
            var received = ReceiveInto(buffer[(written * _channels)..], frames - written);
            written += received;
            if (written >= frames)
            {
                break;
            }
            if (_flushed)
            {
                break; // everything drained
            }
            var n = _inner.Read(_input);
            unsafe
            {
                fixed (float* p = _input)
                {
                    if (n > 0)
                    {
                        _ = SoundTouchNative.soundtouch_putSamples(_handle, p, (uint)n);
                    }
                    else
                    {
                        _ = SoundTouchNative.soundtouch_flush(_handle);
                        _flushed = true;
                    }
                }
            }
        }
        return written;
    }

    public void Seek(long frame)
    {
        _inner.Seek(frame);
        if (_handle != 0)
        {
            SoundTouchNative.soundtouch_clear(_handle);
        }
        _flushed = false;
    }

    public void Dispose()
    {
        if (_handle != 0)
        {
            SoundTouchNative.soundtouch_destroyInstance(_handle);
            _handle = 0;
        }
        _inner.Dispose();
    }

    private void Apply()
    {
        var version = Volatile.Read(ref _requestVersion);
        if (version == _appliedVersion)
        {
            return;
        }
        _appliedVersion = version;
        var speed = Volatile.Read(ref _requestedSpeed);
        var keepPitch = Volatile.Read(ref _requestedKeepPitch);
        var wasActive = _active;
        var active = Math.Abs(speed - 1) > 1e-6;
        if (!active)
        {
            if (wasActive && _handle != 0)
            {
                // Back to 1.0×: continue exactly where the output is (drop SoundTouch's buffered audio).
                var resume = PositionFramesWhileActive();
                SoundTouchNative.soundtouch_clear(_handle);
                if (_inner.CanSeek)
                {
                    _inner.Seek(resume);
                }
            }
            _active = false;
            _speed = 1;
            return;
        }
        _active = true;
        _speed = speed;
        if (_handle == 0)
        {
            _handle = SoundTouchNative.soundtouch_createInstance();
            if (SoundTouchNative.soundtouch_setChannels(_handle, (uint)_channels) == 0 ||
                SoundTouchNative.soundtouch_setSampleRate(_handle, (uint)_inner.Format.SampleRate) == 0)
            {
                throw new AudioSourceException($"SoundTouch does not support {_channels} channels at {_inner.Format.SampleRate} Hz.");
            }
        }
        if (keepPitch)
        {
            SoundTouchNative.soundtouch_setRate(_handle, 1f);
            SoundTouchNative.soundtouch_setTempo(_handle, (float)speed);
        }
        else
        {
            SoundTouchNative.soundtouch_setTempo(_handle, 1f);
            SoundTouchNative.soundtouch_setRate(_handle, (float)speed);
        }
        _flushed = false;
    }

    private long PositionFramesWhileActive()
    {
        var pending = SoundTouchNative.soundtouch_numUnprocessedSamples(_handle) + (SoundTouchNative.soundtouch_numSamples(_handle) * _speed);
        return Math.Max(0, _inner.PositionFrames - (long)pending);
    }

    private unsafe int ReceiveInto(Span<float> buffer, int maxFrames)
    {
        fixed (float* p = buffer)
        {
            return (int)SoundTouchNative.soundtouch_receiveSamples(_handle, p, (uint)maxFrames);
        }
    }
}

/// <summary>PB-06: the SoundTouchDLL C API (float sample build).</summary>
internal static unsafe partial class SoundTouchNative
{
    private const string Library = "SoundTouchDLL";

    [LibraryImport(Library)]
    public static partial nint soundtouch_createInstance();

    [LibraryImport(Library)]
    public static partial void soundtouch_destroyInstance(nint h);

    [LibraryImport(Library)]
    public static partial uint soundtouch_getVersionId();

    [LibraryImport(Library)]
    public static partial void soundtouch_setRate(nint h, float rate);

    [LibraryImport(Library)]
    public static partial void soundtouch_setTempo(nint h, float tempo);

    [LibraryImport(Library)]
    public static partial int soundtouch_setChannels(nint h, uint channels);

    [LibraryImport(Library)]
    public static partial int soundtouch_setSampleRate(nint h, uint rate);

    [LibraryImport(Library)]
    public static partial int soundtouch_flush(nint h);

    [LibraryImport(Library)]
    public static partial int soundtouch_putSamples(nint h, float* samples, uint frames);

    [LibraryImport(Library)]
    public static partial void soundtouch_clear(nint h);

    [LibraryImport(Library)]
    public static partial uint soundtouch_numUnprocessedSamples(nint h);

    [LibraryImport(Library)]
    public static partial uint soundtouch_receiveSamples(nint h, float* output, uint maxFrames);

    [LibraryImport(Library)]
    public static partial uint soundtouch_numSamples(nint h);
}
