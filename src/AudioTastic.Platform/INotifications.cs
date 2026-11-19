namespace AudioTastic.Platform;

/// <summary>SI-07: native notifications (Windows toasts so far).</summary>
public interface INotifications
{
    bool IsSupported { get; }

    /// <summary>
    /// Shows a short, silent notification that replaces the previous one (so only the latest track stays in the
    /// notification centre). <paramref name="imagePath"/> is a local image file, or null.
    /// </summary>
    void ShowNowPlaying(string title, string? text, string? imagePath);
}

/// <summary>Notifications for systems that have none yet (macOS and Linux until they get their own).</summary>
public sealed class NoNotifications : INotifications
{
    public static NoNotifications Instance { get; } = new();

    public bool IsSupported => false;

    public void ShowNowPlaying(string title, string? text, string? imagePath)
    {
    }
}
