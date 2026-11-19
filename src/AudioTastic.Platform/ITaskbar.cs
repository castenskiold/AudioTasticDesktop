namespace AudioTastic.Platform;

/// <summary>SI-03: a thumbnail toolbar button under the taskbar preview.</summary>
public enum TaskbarButton
{
    Previous,
    PlayPause,
    Next,
}

/// <summary>SI-03: what the taskbar button's progress shows.</summary>
public enum TaskbarProgress
{
    /// <summary>No progress (stopped, or a stream without a length).</summary>
    None,

    /// <summary>Green: playing.</summary>
    Normal,

    /// <summary>Yellow: paused.</summary>
    Paused,
}

/// <summary>An icon as straight (not premultiplied) BGRA pixels, top row first.</summary>
public sealed record TaskbarImage(int Width, int Height, byte[] Bgra);

/// <summary>The thumbnail toolbar's images.</summary>
public sealed record TaskbarImages(TaskbarImage Previous, TaskbarImage Play, TaskbarImage Pause, TaskbarImage Next);

/// <summary>
/// SI-03 [Windows only]: thumbnail toolbar buttons (previous, play/pause, next) under the taskbar preview and playback
/// progress on the taskbar button. The app passes the window messages of its windows to
/// <see cref="HandleMessage"/>, because the button clicks arrive as WM_COMMAND.
/// </summary>
public interface ITaskbar : IDisposable
{
    bool IsSupported { get; }

    /// <summary>A thumbnail button was clicked (on the UI thread, from <see cref="HandleMessage"/>).</summary>
    event EventHandler<TaskbarButton>? ButtonClicked;

    /// <summary>Adds the buttons to <paramref name="windowHandle"/>'s taskbar button (now, or once the taskbar created it).</summary>
    void Attach(nint windowHandle, TaskbarImages images);

    /// <summary>Handles a window message; true when it was a taskbar message for us.</summary>
    bool HandleMessage(nint windowHandle, uint message, nint wParam, nint lParam);

    /// <summary>Shows the pause image (playing) or the play image.</summary>
    void SetPlaying(bool playing);

    /// <summary>Shows <paramref name="state"/> and <paramref name="fraction"/> (0–1) on every attached window's button.</summary>
    void SetProgress(TaskbarProgress state, double fraction);

    /// <summary>Why the last taskbar call failed (for the log), or null.</summary>
    string? LastFailure => null;
}

/// <summary>Taskbar integration for systems without one (macOS gets a Dock menu later; Linux has none).</summary>
public sealed class NoTaskbar : ITaskbar
{
    public static NoTaskbar Instance { get; } = new();

    public bool IsSupported => false;

    public event EventHandler<TaskbarButton>? ButtonClicked
    {
        add { }
        remove { }
    }

    public void Attach(nint windowHandle, TaskbarImages images)
    {
    }

    public bool HandleMessage(nint windowHandle, uint message, nint wParam, nint lParam) => false;

    public void SetPlaying(bool playing)
    {
    }

    public void SetProgress(TaskbarProgress state, double fraction)
    {
    }

    public void Dispose()
    {
    }
}
