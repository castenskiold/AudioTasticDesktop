namespace AudioTastic.Sdk.Audio;

/// <summary>Produces audio on the audio thread. Implementations must not allocate, lock or block.</summary>
public interface IAudioRenderer
{
    /// <summary>Fills <paramref name="output"/> with interleaved float samples.</summary>
    void Render(Span<float> output);
}

/// <summary>A playback device as reported by the output backend.</summary>
/// <param name="Name">Display name; also what Settings › Output stores.</param>
/// <param name="IsDefault">True for the system default device.</param>
/// <param name="Id">Backend-specific identifier (opaque).</param>
public sealed record AudioDeviceInfo(string Name, bool IsDefault, ReadOnlyMemory<byte> Id)
{
    public override string ToString() => IsDefault ? $"{Name} (default)" : Name;
}

/// <summary>Why the output stopped on its own.</summary>
public sealed class AudioOutputLostEventArgs(string reason) : EventArgs
{
    public string Reason { get; } = reason;
}

/// <summary>PB-05: sample format sent to the device. Integer formats are converted by the output, exactly inverse to decoding.</summary>
public enum OutputSampleFormat
{
    F32,
    S16,
    /// <summary>24-bit packed (3 bytes per sample).</summary>
    S24,
    S32,
}

/// <summary>PB-04 / PB-05: how to open an output.</summary>
/// <param name="BufferMilliseconds">Total buffer, about two device periods.</param>
/// <param name="Exclusive">Exclusive (bit-perfect) mode: no OS mixer, the device gets the samples unchanged.</param>
/// <param name="SampleFormat">Sample format for the device (F32 in shared mode).</param>
/// <param name="Dither">TPDF dither when converting to an integer format (used when reducing bit depth).</param>
public sealed record OutputOptions(int BufferMilliseconds, bool Exclusive = false,
    OutputSampleFormat SampleFormat = OutputSampleFormat.F32, bool Dither = false);

/// <summary>PB-05: a format the device supports natively. 0 channels / 0 Hz mean "any".</summary>
public sealed record DeviceFormat(OutputSampleFormat SampleFormat, int Channels, int SampleRate, bool ExclusiveOnly);

/// <summary>
/// PB-04 / PG-01: an audio output (miniaudio in the app; output plugins implement it too). Shared or exclusive mode
/// (ASIO is optional and deferred).
/// </summary>
public interface IAudioOutput : IDisposable
{
    /// <summary>Backend name, e.g. "WASAPI", "Core Audio", "PulseAudio".</summary>
    string BackendName { get; }

    /// <summary>Current playback devices.</summary>
    IReadOnlyList<AudioDeviceInfo> GetDevices();

    /// <summary>Raised (on a background thread) when devices are added or removed or the default changes.</summary>
    event EventHandler? DevicesChanged;

    /// <summary>
    /// Raised (on a background thread) when the open device stops without being asked to, e.g. it was unplugged.
    /// The engine reacts by reopening on the default device.
    /// </summary>
    event EventHandler<AudioOutputLostEventArgs>? OutputLost;

    bool IsOpen { get; }
    bool IsRunning { get; }

    /// <summary>Name of the open device.</summary>
    string? DeviceName { get; }

    /// <summary>Format the device was opened with (what the renderer must produce).</summary>
    AudioFormat? Format { get; }

    /// <summary>Exclusive mode is in use.</summary>
    bool IsExclusive => false;

    /// <summary>Sample format sent to the device.</summary>
    OutputSampleFormat SampleFormat => OutputSampleFormat.F32;

    /// <summary>PB-05: the rate the hardware runs at (in shared mode the OS may resample); 0 when closed.</summary>
    int DeviceSampleRate => Format?.SampleRate ?? 0;

    /// <summary>PB-05: native formats of <paramref name="device"/> (null = default device); empty when unknown.</summary>
    IReadOnlyList<DeviceFormat> GetDeviceFormats(AudioDeviceInfo? device) => [];

    /// <summary>
    /// Opens <paramref name="device"/> (null = system default, following default-device changes) for
    /// <paramref name="format"/>. Throws <see cref="InvalidOperationException"/> when the device cannot be opened so
    /// (e.g. an exclusive format it does not support).
    /// </summary>
    void Open(AudioDeviceInfo? device, AudioFormat format, OutputOptions options, IAudioRenderer renderer);

    /// <summary>PB-05: switches TPDF dither for integer output on or off while open (per track).</summary>
    void SetDither(bool dither)
    {
    }

    /// <summary>Starts pulling audio from the renderer.</summary>
    void StartOutput();

    /// <summary>Stops pulling audio; the device stays open.</summary>
    void StopOutput();

    void Close();
}
