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

/// <summary>
/// PB-04 / PG-01: an audio output (miniaudio in the app; output plugins implement it too). Exclusive mode comes in P6 (ASIO is optional and deferred).
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

    /// <summary>
    /// Opens <paramref name="device"/> (null = system default, following default-device changes) for
    /// <paramref name="format"/> with a total buffer of about <paramref name="bufferMilliseconds"/>.
    /// </summary>
    void Open(AudioDeviceInfo? device, AudioFormat format, int bufferMilliseconds, IAudioRenderer renderer);

    /// <summary>Starts pulling audio from the renderer.</summary>
    void StartOutput();

    /// <summary>Stops pulling audio; the device stays open.</summary>
    void StopOutput();

    void Close();
}
