using AudioTastic.Core.Audio;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.App.Integration;

/// <summary>
/// CN-09 audio and video: the engine's output while its mix goes to a TV (OutputRouter.Divert). Instead of a sound
/// card pulling the audio, the cast stream pulls it at real-time pace (<see cref="Pull"/>) at <see cref="StreamRate"/>:
/// the rate the engine first opened it with (usually the track's, 44.1 or 48 kHz). If the engine later reopens it at
/// another rate, that audio is resampled to the stream rate, so the stream keeps one rate. (Resampling on every pull
/// is too slow to be the normal path: a pull of 33 ms took 8–46 ms in a Debug build, against 0.1 ms without.)
/// While the engine has stopped the output (paused, stopped) the stream gets silence, so the TV keeps playing.
/// Shared mode only: exclusive and DoP requests are refused, and the engine falls back.
/// </summary>
public sealed class NetworkAudioOutput(string deviceName) : IAudioOutput
{
    /// <summary>The rate before the engine has opened the output.</summary>
    public const int DefaultStreamRate = 48_000;

    private int _streamRate;

    /// <summary>The stream's sample rate: fixed by the first Open, or by <see cref="LockStreamRate"/>.</summary>
    public int StreamRate => _streamRate > 0 ? _streamRate : DefaultStreamRate;

    /// <summary>Fixes the stream rate now (a stream is starting): the open format's rate, or the default.</summary>
    public int LockStreamRate()
    {
        lock (_gate)
        {
            if (_streamRate == 0)
            {
                _streamRate = _format?.SampleRate ?? DefaultStreamRate;
            }
            return _streamRate;
        }
    }

    private readonly Lock _gate = new();
    private IAudioSource? _source;
    private AudioFormat? _format;
    private volatile bool _running;

    public string BackendName => $"Cast to {deviceName}";

    public IReadOnlyList<AudioDeviceInfo> GetDevices() => [];

    public event EventHandler? DevicesChanged
    {
        add { }
        remove { }
    }

    public event EventHandler<AudioOutputLostEventArgs>? OutputLost
    {
        add { }
        remove { }
    }

    public bool IsOpen => _format is not null;

    public bool IsRunning => _running;

    public string? DeviceName => _format is null ? null : BackendName;

    public AudioFormat? Format => _format;

    public void Open(AudioDeviceInfo? device, AudioFormat format, OutputOptions options, IAudioRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(renderer);
        if (options.Exclusive)
        {
            throw new InvalidOperationException("Casting to a TV uses shared mode only.");
        }
        lock (_gate)
        {
            if (_streamRate == 0)
            {
                _streamRate = format.SampleRate;
            }
            IAudioSource source = new RendererSource(renderer, format);
            if (format.SampleRate != _streamRate)
            {
                source = new ResamplingSource(source, _streamRate);
            }
            _source?.Dispose();
            _source = source;
            _format = format;
            _running = false;
        }
    }

    public void StartOutput() => _running = true;

    public void StopOutput() => _running = false;

    public void Close()
    {
        lock (_gate)
        {
            _running = false;
            _source?.Dispose();
            _source = null;
            _format = null;
        }
    }

    /// <summary>Fills <paramref name="stereo"/> (interleaved stereo at <see cref="StreamRate"/>) with the mix, or silence.</summary>
    public void Pull(Span<float> stereo)
    {
        lock (_gate)
        {
            if (!_running || _source is not { } source || source.Format.Channels != 2)
            {
                stereo.Clear();
                return;
            }
            var filled = 0;
            while (filled < stereo.Length)
            {
                var frames = source.Read(stereo[filled..]);
                if (frames <= 0)
                {
                    stereo[filled..].Clear();
                    return;
                }
                filled += frames * 2;
            }
        }
    }

    public void Dispose() => Close();

    /// <summary>The engine's renderer as a never-ending source (so the resampler can read it).</summary>
    private sealed class RendererSource(IAudioRenderer renderer, AudioFormat format) : IAudioSource
    {
        private long _position;

        public AudioStreamInfo Info { get; } = new("cast mix", format.SampleRate, format.Channels, 32, 0, true);

        public AudioFormat Format => format;

        public long? LengthFrames => null;

        public long PositionFrames => _position;

        public bool CanSeek => false;

        public IReadOnlyDictionary<string, string> Tags { get; } = new Dictionary<string, string>();

        public int Read(Span<float> buffer)
        {
            var frames = buffer.Length / format.Channels;
            renderer.Render(buffer[..(frames * format.Channels)]);
            _position += frames;
            return frames;
        }

        public void Seek(long frame) => throw new NotSupportedException();

        public void Dispose()
        {
        }
    }
}
