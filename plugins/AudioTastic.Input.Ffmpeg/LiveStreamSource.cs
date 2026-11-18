using AudioTastic.Sdk.Audio;

namespace AudioTastic.Input.Ffmpeg;

/// <summary>
/// FM-10: wraps a live internet-radio stream.
/// <list type="bullet">
/// <item>A reader thread decodes into a buffer; playback starts once <see cref="PrebufferSeconds"/> are buffered
/// (and again after the buffer runs dry), so short network stalls are not heard.</item>
/// <item>When the stream drops or ends, it reconnects up to <see cref="MaxReconnects"/> times with back-off
/// (0.5, 1, 2, 4, 8 s). After that the source ends, so the playlist moves on.</item>
/// <item><see cref="Read"/> never blocks for long: while (re)buffering it returns short stretches of silence, so
/// stop and skip stay responsive.</item>
/// </list>
/// </summary>
internal sealed class LiveStreamSource : IAudioSource, IStreamMetadata
{
    public const double PrebufferSeconds = 2;
    public const int MaxReconnects = 5;
    private const double CapacitySeconds = 8;
    private const int MaxWaitMilliseconds = 200;

    private readonly string _url;
    private readonly Func<string, IAudioSource> _open;
    private readonly object _lock = new();
    private readonly float[] _ring;
    private readonly int _prebufferSamples;
    private readonly int _silenceSamples;
    private readonly Thread _reader;
    private int _readIndex;
    private int _count;
    private bool _primed;
    private bool _ended;
    private volatile bool _disposed;
    private IAudioSource _current;

    public LiveStreamSource(string url, IAudioSource first, Func<string, IAudioSource>? open = null, double backoffScale = 1)
    {
        _url = url;
        _current = first;
        _open = open ?? (u => FfmpegAudioSource.Open(u));
        BackoffScale = backoffScale;
        Info = first.Info;
        Format = first.Format;
        Tags = first.Tags;
        var samplesPerSecond = Format.SampleRate * Format.Channels;
        _ring = new float[(int)(samplesPerSecond * CapacitySeconds)];
        _prebufferSamples = (int)(samplesPerSecond * PrebufferSeconds);
        _silenceSamples = Format.SampleRate / 100 * Format.Channels; // 10 ms
        Attach(first);
        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "AudioTastic stream reader" };
        _reader.Start();
    }

    /// <summary>Scales the reconnect back-off (tests use a small value).</summary>
    public double BackoffScale { get; }

    /// <summary>How many times the stream has been reopened.</summary>
    public int Reconnects { get; private set; }

    public AudioStreamInfo Info { get; }
    public AudioFormat Format { get; }
    public long? LengthFrames => null;
    public long PositionFrames { get; private set; }
    public bool CanSeek => false;
    public IReadOnlyDictionary<string, string> Tags { get; }

    public string? StreamTitle => (_current as FfmpegAudioSource)?.StreamTitle;
    public string? StationName => (_current as FfmpegAudioSource)?.StationName;
    public event EventHandler<string?>? StreamTitleChanged;

    /// <summary>True while waiting for the buffer to fill (start, stall or reconnect).</summary>
    public bool IsBuffering
    {
        get
        {
            lock (_lock)
            {
                return !_primed && !_ended;
            }
        }
    }

    public int Read(Span<float> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var channels = Format.Channels;
        if (buffer.Length % channels != 0)
        {
            throw new ArgumentException($"Buffer length must be a multiple of the channel count ({channels}).", nameof(buffer));
        }
        int samples;
        lock (_lock)
        {
            var deadline = Environment.TickCount64 + MaxWaitMilliseconds;
            while (!_ended && !(_primed ? _count > 0 : _count >= _prebufferSamples))
            {
                var wait = deadline - Environment.TickCount64;
                if (wait <= 0)
                {
                    break;
                }
                Monitor.Wait(_lock, (int)wait);
            }
            if (_count == 0 && _ended)
            {
                return 0;
            }
            if (_count == 0 || (!_primed && _count < _prebufferSamples && !_ended))
            {
                // (Re)buffering: a short stretch of silence keeps the deck running without blocking it.
                _primed = false;
                samples = Math.Min(buffer.Length, _silenceSamples);
                buffer[..samples].Clear();
            }
            else
            {
                _primed = true;
                samples = Math.Min(buffer.Length, _count);
                var first = Math.Min(samples, _ring.Length - _readIndex);
                _ring.AsSpan(_readIndex, first).CopyTo(buffer);
                _ring.AsSpan(0, samples - first).CopyTo(buffer[first..]);
                _readIndex = (_readIndex + samples) % _ring.Length;
                _count -= samples;
                Monitor.PulseAll(_lock); // room for the reader
            }
        }
        var frames = samples / channels;
        PositionFrames += frames;
        return frames;
    }

    public void Seek(long frame) => throw new NotSupportedException("Live streams cannot seek.");

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        lock (_lock)
        {
            Monitor.PulseAll(_lock);
        }
        _reader.Join(TimeSpan.FromSeconds(15)); // a blocked network read ends at the rw_timeout
    }

    private void ReadLoop()
    {
        var chunk = new float[4096 * Format.Channels];
        var source = _current;
        var failures = 0;
        try
        {
            while (!_disposed)
            {
                int frames;
                try
                {
                    frames = source.Read(chunk);
                }
                catch (AudioSourceException)
                {
                    frames = 0;
                }
                if (frames > 0)
                {
                    failures = 0;
                    Write(chunk.AsSpan(0, frames * Format.Channels));
                    continue;
                }

                // The stream dropped or ended: reconnect with back-off.
                Detach(source);
                source.Dispose();
                IAudioSource? next = null;
                while (next is null && !_disposed && failures < MaxReconnects)
                {
                    Sleep(TimeSpan.FromSeconds(0.5 * Math.Pow(2, failures) * BackoffScale));
                    failures++;
                    if (_disposed)
                    {
                        break;
                    }
                    try
                    {
                        var candidate = _open(_url);
                        if (candidate.Format == Format)
                        {
                            next = candidate;
                        }
                        else
                        {
                            candidate.Dispose(); // the station changed format: treat as the end
                            failures = MaxReconnects;
                        }
                    }
                    catch (AudioSourceException)
                    {
                    }
                }
                if (next is null)
                {
                    return;
                }
                source = next;
                Attach(source);
                Reconnects++;
            }
        }
        finally
        {
            if (_disposed)
            {
                source.Dispose();
            }
            lock (_lock)
            {
                _ended = true;
                Monitor.PulseAll(_lock);
            }
        }
    }

    private void Write(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            while (!_disposed && _ring.Length - _count < samples.Length)
            {
                Monitor.Wait(_lock, 100);
            }
            if (_disposed)
            {
                return;
            }
            var writeIndex = (_readIndex + _count) % _ring.Length;
            var first = Math.Min(samples.Length, _ring.Length - writeIndex);
            samples[..first].CopyTo(_ring.AsSpan(writeIndex));
            samples[first..].CopyTo(_ring);
            _count += samples.Length;
            Monitor.PulseAll(_lock);
        }
    }

    private void Sleep(TimeSpan delay)
    {
        lock (_lock)
        {
            var deadline = Environment.TickCount64 + (long)delay.TotalMilliseconds;
            while (!_disposed && Environment.TickCount64 < deadline)
            {
                Monitor.Wait(_lock, (int)Math.Max(1, deadline - Environment.TickCount64));
            }
        }
    }

    private void Attach(IAudioSource source)
    {
        _current = source;
        if (source is FfmpegAudioSource metadata)
        {
            metadata.StreamTitleChanged += OnTitleChanged;
            if (metadata.StreamTitle is not null)
            {
                StreamTitleChanged?.Invoke(this, metadata.StreamTitle);
            }
        }
    }

    private void Detach(IAudioSource source)
    {
        if (source is FfmpegAudioSource metadata)
        {
            metadata.StreamTitleChanged -= OnTitleChanged;
        }
    }

    private void OnTitleChanged(object? sender, string? title) => StreamTitleChanged?.Invoke(this, title);
}
