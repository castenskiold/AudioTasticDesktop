using System.Collections.Concurrent;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.Core.Audio;

/// <summary>
/// One deck of the <see cref="MixerGraph"/>: an <see cref="IAudioSource"/> decoded on the deck's own thread into a
/// lock-free ring buffer, read by the audio callback. P1 plays through deck A; deck B exists so gapless playback
/// (PB-01) and crossfading (XF-01/02) need no redesign.
/// <para>
/// Threading: all source access (load, seek, read, dispose) happens on the decoder thread. Commands from the
/// control thread are queued and complete asynchronously. While the ring is reset the deck is <c>Stalled</c>:
/// the audio thread ignores it, and the decoder thread waits for in-flight renders to finish first.
/// </para>
/// </summary>
public sealed class Deck : IDisposable
{
    private const int StateIdle = 0;
    private const int StatePlaying = 1;
    private const int StateStalled = 2;
    private const int StateEnded = 3;
    /// <summary>PB-01: loaded and buffered, waiting for the mixer to start it the moment the other deck ends.</summary>
    private const int StateArmed = 4;

    /// <summary>Decoded audio kept ahead of playback.</summary>
    private const double BufferSeconds = 0.5;
    /// <summary>Audio decoded before a load/seek completes, so playback starts without an underrun.</summary>
    private const double PrefillSeconds = 0.2;
    private const int ChunkFrames = 2048;

    private readonly MixerGraph _graph;
    private readonly ConcurrentQueue<(Action Work, TaskCompletionSource Done)> _commands = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly Thread _thread;
    private volatile bool _disposed;

    // Decoder thread only.
    private TimeStretchSource? _source;
    private float[] _decodeBuffer = [];
    private float[] _mapBuffer = [];

    // Shared (Volatile / Interlocked).
    private SpscRingBuffer _ring = new(1);
    private int _state = StateIdle;
    private int _sourceEnded;
    private long _consumedFrames; // written by the audio thread (or the decoder thread while stalled)
    private long _positionBase;
    private long _underruns;
    private Exception? _fault;

    // The source chain built by LoadAsync: source -> LoopSource (PB-08) -> TimeStretchSource (PB-06).
    private volatile LoopSource? _loop;
    private volatile TimeStretchSource? _stretch;
    private double _speed = 1;
    private bool _keepPitch = true;

    // Position checkpoints: after each chunk the decoder records (frames written so far, source position of the next
    // frame, source frames per output frame for that chunk). The position of what has been played is mapped from
    // them, which covers speed changes and loop wraps. A small ring: playback lags decoding by under 0.5 s.
    private const int Checkpoints = 64;
    private readonly long[] _cpWritten = new long[Checkpoints];
    private readonly long[] _cpPosition = new long[Checkpoints];
    private readonly double[] _cpSpeed = new double[Checkpoints];
    private int _cpCount;
    private long _framesWritten;

    internal Deck(string name, MixerGraph graph)
    {
        Name = name;
        _graph = graph;
        _thread = new Thread(DecoderLoop)
        {
            IsBackground = true,
            Name = $"AudioTastic deck {name} decoder",
            Priority = ThreadPriority.AboveNormal,
        };
        _thread.Start();
    }

    public string Name { get; }

    /// <summary>Per-deck gain (unity in P1; crossfades use it later).</summary>
    public GainStage Gain { get; } = new();

    /// <summary>Format of the loaded source as decoded (before channel mapping); null when idle.</summary>
    public AudioFormat? SourceFormat { get; private set; }

    /// <summary>True while a source is loaded and has not finished playing (or is armed to start).</summary>
    public bool IsActive => Volatile.Read(ref _state) is StatePlaying or StateStalled or StateArmed;

    /// <summary>PB-01: loaded and waiting for a gapless start.</summary>
    public bool IsArmed => Volatile.Read(ref _state) == StateArmed;

    /// <summary>Being played by the mixer.</summary>
    public bool IsPlaying => Volatile.Read(ref _state) == StatePlaying;

    /// <summary>True once the loaded source has been played to the end.</summary>
    public bool HasEnded => Volatile.Read(ref _state) == StateEnded;

    /// <summary>Playback position in source frames (what has been handed to the output).</summary>
    public long PositionFrames
    {
        get
        {
            var consumed = Volatile.Read(ref _consumedFrames);
            var count = Volatile.Read(ref _cpCount);
            if (count == 0)
            {
                return Volatile.Read(ref _positionBase) + consumed;
            }
            // The newest checkpoint at or before the consumed frame; the chunk after it ran at the next one's speed.
            for (var k = count - 1; k >= Math.Max(0, count - Checkpoints); k--)
            {
                var i = k % Checkpoints;
                if (_cpWritten[i] <= consumed)
                {
                    var speed = k + 1 < count ? _cpSpeed[(k + 1) % Checkpoints] : _cpSpeed[i];
                    return _cpPosition[i] + (long)Math.Round((consumed - _cpWritten[i]) * speed);
                }
            }
            return Volatile.Read(ref _positionBase) + consumed;
        }
    }

    /// <summary>
    /// PB-08: loops source frames [<paramref name="a"/>, <paramref name="b"/>): decoding stops exactly at B and continues
    /// from A without resetting the buffer, so the loop is seamless.
    /// </summary>
    public void SetLoop(long a, long b) => _loop?.SetRegion(a, b);

    public void ClearLoop() => _loop?.ClearRegion();

    /// <summary>PB-06: playback speed (0.5–2.0) and keep-pitch; applies from the next decoded chunk.</summary>
    public void SetSpeed(double speed, bool keepPitch)
    {
        Volatile.Write(ref _speed, speed);
        Volatile.Write(ref _keepPitch, keepPitch);
        _stretch?.SetSpeed(speed, keepPitch);
    }

    /// <summary>Number of audio callbacks that found the buffer empty before the end of the source.</summary>
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>Error raised while decoding (playback of this deck stops); cleared on the next load.</summary>
    public Exception? Fault => Volatile.Read(ref _fault);

    /// <summary>Samples decoded and waiting in the ring buffer.</summary>
    internal int BufferedSamples => _ring.AvailableToRead;

    /// <summary>True once the source has returned its last frame (the ring may still hold audio).</summary>
    internal bool SourceEnded => Volatile.Read(ref _sourceEnded) != 0;

    /// <summary>
    /// Loads <paramref name="source"/> (the deck takes ownership) and starts feeding it, from the source's current
    /// position. Completes when enough audio is buffered to start. The source must match the graph's sample rate.
    /// With <paramref name="armed"/> the deck waits until the mixer starts it (gapless hand-over, PB-01).
    /// </summary>
    public Task LoadAsync(IAudioSource source, bool armed = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Post(() =>
        {
            Stall();
            _source?.Dispose();
            var loop = new LoopSource(source);
            var stretch = new TimeStretchSource(loop);
            stretch.SetSpeed(Volatile.Read(ref _speed), Volatile.Read(ref _keepPitch));
            _loop = loop;
            _stretch = stretch;
            _source = stretch;
            SourceFormat = source.Format;
            Volatile.Write(ref _fault, null);
            PrepareBuffers(source.Format);
            ResetPosition(source.PositionFrames);
            Prefill();
            Volatile.Write(ref _state, armed ? StateArmed : StatePlaying);
        });
    }

    /// <summary>Audio thread: starts an armed deck (gapless hand-over). Returns false when it was not armed.</summary>
    internal bool StartArmed() => Interlocked.CompareExchange(ref _state, StatePlaying, StateArmed) == StateArmed;

    /// <summary>Seeks the loaded source; completes when audio from the new position is buffered.</summary>
    public Task SeekAsync(long frame) => Post(() =>
    {
        if (_source is null)
        {
            return;
        }
        Stall();
        _source.Seek(frame);
        ResetPosition(_source.PositionFrames);
        Prefill();
        Volatile.Write(ref _state, StatePlaying);
    });

    /// <summary>Stops the deck and disposes its source.</summary>
    public Task UnloadAsync() => Post(() =>
    {
        Stall();
        _source?.Dispose();
        _source = null;
        _loop = null;
        _stretch = null;
        SourceFormat = null;
        ResetPosition(0);
        Volatile.Write(ref _state, StateIdle);
    });

    /// <summary>
    /// Audio thread: reads up to <paramref name="output"/>.Length samples, applies the deck gain and adds them to
    /// <paramref name="output"/>. <paramref name="scratch"/> must be at least as long as <paramref name="output"/>.
    /// </summary>
    internal int MixInto(Span<float> output, Span<float> scratch)
    {
        if (Volatile.Read(ref _state) != StatePlaying)
        {
            return 0;
        }
        var ended = Volatile.Read(ref _sourceEnded) != 0; // read before the ring: all final writes are then visible
        var buffer = scratch[..output.Length];
        var n = _ring.Read(buffer);
        if (n < output.Length)
        {
            if (ended && _ring.AvailableToRead == 0)
            {
                Interlocked.CompareExchange(ref _state, StateEnded, StatePlaying);
            }
            else
            {
                Interlocked.Increment(ref _underruns);
            }
        }
        if (n == 0)
        {
            return 0;
        }
        var channels = _graph.Format.Channels;
        Gain.Process(buffer[..n], channels);
        var mixed = output[..n];
        for (var i = 0; i < mixed.Length; i++)
        {
            mixed[i] += buffer[i];
        }
        Volatile.Write(ref _consumedFrames, _consumedFrames + (n / channels));
        return n;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _wake.Release();
        _thread.Join(TimeSpan.FromSeconds(2));
        _source?.Dispose();
        _source = null;
        _wake.Dispose();
    }

    private Task Post(Action work)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _commands.Enqueue((work, done));
        _wake.Release();
        return done.Task;
    }

    private void DecoderLoop()
    {
        while (!_disposed)
        {
            while (_commands.TryDequeue(out var command))
            {
                try
                {
                    command.Work();
                    command.Done.SetResult();
                }
#pragma warning disable CA1031 // The command's caller receives the exception.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    command.Done.SetException(ex);
                }
            }

            if (Volatile.Read(ref _state) is StatePlaying or StateArmed && _source is not null && Volatile.Read(ref _sourceEnded) == 0 &&
                _ring.AvailableToWrite >= _mapBuffer.Length)
            {
                FillOnce();
                continue;
            }
            _wake.Wait(5);
        }
    }

    /// <summary>Decodes one chunk into the ring. Assumes space for a full chunk.</summary>
    private void FillOnce()
    {
        int frames;
        try
        {
            frames = _source!.Read(_decodeBuffer);
        }
#pragma warning disable CA1031 // A broken file ends this deck; the engine reports the fault.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Volatile.Write(ref _fault, ex);
            frames = 0;
        }
        if (frames == 0)
        {
            Volatile.Write(ref _sourceEnded, 1);
            return;
        }
        var outChannels = _graph.Format.Channels;
        ChannelMapper.Map(_decodeBuffer, _source!.Format.Channels, _mapBuffer, outChannels, frames);
        _ring.Write(_mapBuffer.AsSpan(0, frames * outChannels));
        _framesWritten += frames;
        AddCheckpoint();
    }

    private void AddCheckpoint()
    {
        var count = _cpCount;
        var i = count % Checkpoints;
        _cpWritten[i] = _framesWritten;
        _cpPosition[i] = _source!.PositionFrames;
        _cpSpeed[i] = _stretch?.Speed ?? 1;
        Volatile.Write(ref _cpCount, count + 1);
    }

    private void Prefill()
    {
        var target = (int)(PrefillSeconds * _graph.Format.SampleRate) * _graph.Format.Channels;
        while (_ring.AvailableToRead < target && Volatile.Read(ref _sourceEnded) == 0 && _ring.AvailableToWrite >= _mapBuffer.Length)
        {
            FillOnce();
        }
    }

    private void PrepareBuffers(AudioFormat sourceFormat)
    {
        var format = _graph.Format;
        if (sourceFormat.SampleRate != format.SampleRate)
        {
            throw new InvalidOperationException(
                $"Deck {Name}: source rate {sourceFormat.SampleRate} Hz differs from the graph's {format.SampleRate} Hz.");
        }
        _decodeBuffer = new float[ChunkFrames * sourceFormat.Channels];
        _mapBuffer = new float[ChunkFrames * format.Channels];
        var capacity = (int)(BufferSeconds * format.SampleRate * format.Channels);
        if (_ring.Capacity < capacity)
        {
            _ring = new SpscRingBuffer(capacity); // safe: the deck is stalled
        }
        Gain.Configure(format.SampleRate);
    }

    private void ResetPosition(long frame)
    {
        _ring.Reset();
        Volatile.Write(ref _sourceEnded, 0);
        Volatile.Write(ref _positionBase, frame);
        Volatile.Write(ref _consumedFrames, 0);
        Volatile.Write(ref _cpCount, 0);
        _framesWritten = 0;
        if (_source is not null)
        {
            AddCheckpoint();
        }
        Interlocked.Exchange(ref _underruns, 0);
    }

    /// <summary>Stops the audio thread from reading this deck and waits until any render in progress has finished.</summary>
    private void Stall()
    {
        var previous = Interlocked.Exchange(ref _state, StateStalled);
        if (previous is StatePlaying or StateArmed)
        {
            _graph.WaitForRenderBoundary();
        }
    }
}
