using System.Runtime.InteropServices;
using System.Text;

namespace AudioTastic.Core.Audio.Dsp.Vst3;

/// <summary>A VST3 plug-in could not be loaded or set up.</summary>
public sealed class Vst3Exception : Exception
{
    public Vst3Exception()
    {
    }

    public Vst3Exception(string message) : base(message)
    {
    }

    public Vst3Exception(string message, Exception inner) : base(message, inner)
    {
    }
}

/// <summary>
/// EQ-06: one loaded VST3 effect (an instance of one class in a .vst3 module), through the native host shim.
/// Threads: <see cref="Process"/> on the audio thread (no allocation); <see cref="Setup"/>, state and the editor on a
/// control or UI thread, never while the audio thread is inside <see cref="Process"/> for setup and close (the DSP
/// chain waits for a render boundary first).
/// </summary>
public sealed unsafe class Vst3Plugin : IDisposable
{
    /// <summary>Largest block handed to the plug-in; longer blocks are processed in pieces.</summary>
    public const int MaxBlockFrames = 4096;

    private nint _handle;
    private GCHandle _self;
    private Action<int, int>? _resized;
    private static readonly Vst3Native.ResizeCallback ResizeThunk = OnResize; // kept alive for native code
    private static readonly nint ResizePointer = Marshal.GetFunctionPointerForDelegate(ResizeThunk);

    private Vst3Plugin(nint handle, string path, string classId)
    {
        _handle = handle;
        Path = path;
        ClassId = classId;
    }

    public string Path { get; }

    public string ClassId { get; }

    /// <summary>True when the native host library is present and of the expected version.</summary>
    public static bool IsAvailable
    {
        get
        {
            try
            {
                return Vst3Native.ApiVersion() == Vst3Native.ApiVersionExpected;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            {
                return false;
            }
        }
    }

    /// <summary>Loads <paramref name="classId"/> from the module at <paramref name="path"/>.</summary>
    public static Vst3Plugin Open(string path, string classId)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(classId);
        var error = stackalloc byte[512];
        error[0] = 0;
        var handle = Vst3Native.Open(path, classId, error, 512);
        if (handle == 0)
        {
            var message = Marshal.PtrToStringUTF8((nint)error);
            throw new Vst3Exception(string.IsNullOrEmpty(message) ? $"Could not load {System.IO.Path.GetFileName(path)}" : message);
        }
        return new Vst3Plugin(handle, path, classId);
    }

    /// <summary>Prepares stereo processing at <paramref name="sampleRate"/> and activates the plug-in.</summary>
    public void Setup(int sampleRate)
    {
        var result = Vst3Native.Setup(Handle, sampleRate, MaxBlockFrames);
        if (result != 0)
        {
            throw new Vst3Exception(result == -3
                ? "The plug-in does not support stereo (or mono) processing."
                : $"The plug-in refused to start processing (error {result}).");
        }
    }

    /// <summary>Processes interleaved stereo in place; false when the plug-in failed (the audio is then left as it was).</summary>
    public bool Process(Span<float> stereo)
    {
        var handle = _handle;
        if (handle == 0)
        {
            return false;
        }
        fixed (float* samples = stereo)
        {
            var frames = stereo.Length / 2;
            for (var done = 0; done < frames; done += MaxBlockFrames)
            {
                var count = Math.Min(MaxBlockFrames, frames - done);
                if (Vst3Native.Process(handle, samples + (done * 2), count) != 0)
                {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>The plug-in's processing latency in frames.</summary>
    public int Latency => Vst3Native.Latency(Handle);

    /// <summary>The plug-in's state (parameters, presets), to be stored with the chain.</summary>
    public byte[] GetState()
    {
        var size = Vst3Native.GetState(Handle, null, 0);
        if (size < 0)
        {
            throw new Vst3Exception("The plug-in could not save its state.");
        }
        var bytes = new byte[size];
        fixed (byte* buffer = bytes)
        {
            if (Vst3Native.GetState(Handle, buffer, size) != size)
            {
                throw new Vst3Exception("The plug-in's state changed size while it was saved.");
            }
        }
        return bytes;
    }

    public void SetState(byte[] state)
    {
        ArgumentNullException.ThrowIfNull(state);
        fixed (byte* buffer = state)
        {
            if (Vst3Native.SetState(Handle, buffer, state.Length) != 0)
            {
                throw new Vst3Exception("The plug-in did not accept its saved state.");
            }
        }
    }

    /// <summary>True when the plug-in has an editor that can be shown in this platform's windows.</summary>
    public bool HasEditor => Vst3Native.HasEditor(Handle) == 1;

    /// <summary>
    /// Opens the editor inside <paramref name="parent"/> (HWND / NSView* / X11 window id) and returns its size in
    /// pixels; <paramref name="resized"/> is called (UI thread) when the editor asks for another size.
    /// </summary>
    public (int Width, int Height) OpenEditor(nint parent, Action<int, int> resized)
    {
        _resized = resized;
        if (!_self.IsAllocated)
        {
            _self = GCHandle.Alloc(this);
        }
        var result = Vst3Native.EditorOpen(Handle, parent, out var width, out var height, ResizePointer, GCHandle.ToIntPtr(_self));
        if (result != 0)
        {
            throw new Vst3Exception(result == -3 ? "The plug-in has no editor for this platform." : "The plug-in's editor could not be opened.");
        }
        return (width, height);
    }

    public void CloseEditor()
    {
        if (_handle != 0)
        {
            Vst3Native.EditorClose(_handle);
        }
        _resized = null;
    }

    private static void OnResize(nint user, int width, int height)
    {
        if (GCHandle.FromIntPtr(user).Target is Vst3Plugin plugin)
        {
            plugin._resized?.Invoke(width, height);
        }
    }

    private nint Handle => _handle != 0 ? _handle : throw new ObjectDisposedException(nameof(Vst3Plugin));

    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);
        if (handle != 0)
        {
            Vst3Native.Close(handle);
        }
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    public override string ToString() => new StringBuilder(System.IO.Path.GetFileName(Path)).Append(" / ").Append(ClassId).ToString();
}
