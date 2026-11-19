#if WINDOWS
using System.Runtime.Versioning;
using System.Security;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace AudioTastic.Platform.Windows;

/// <summary>
/// SI-07 on Windows: a toast with the title, artist and cover, through the app's AppUserModelID (which needs the Start
/// menu shortcut AudioTastic keeps, SI-02). Silent, tagged so each one replaces the previous, and gone after a
/// minute; clicking it starts AudioTastic through the shortcut, which brings the running player to the front (SI-06).
/// </summary>
[SupportedOSPlatform("windows10.0.17763.0")]
public sealed class WindowsNotifications(string appId) : INotifications
{
    private const string Tag = "now-playing";
    private ToastNotifier? _notifier;

    public bool IsSupported => true;

    public void ShowNowPlaying(string title, string? text, string? imagePath)
    {
        var xml = new XmlDocument();
        xml.LoadXml(ToastXml(title, text, imagePath));
        var toast = new ToastNotification(xml)
        {
            Tag = Tag,
            Group = Tag,
            ExpirationTime = DateTimeOffset.Now.AddMinutes(1),
        };
        _notifier ??= ToastNotificationManager.CreateToastNotifier(appId);
        _notifier.Show(toast);
    }

    /// <summary>The toast XML (public for tests): two lines, an optional image, no sound.</summary>
    public static string ToastXml(string title, string? text, string? imagePath)
    {
        var image = imagePath is null
            ? string.Empty
            : $"<image placement=\"appLogoOverride\" src=\"{SecurityElement.Escape(new Uri(imagePath).AbsoluteUri)}\"/>";
        var second = string.IsNullOrWhiteSpace(text) ? string.Empty : $"<text>{SecurityElement.Escape(text)}</text>";
        return "<toast duration=\"short\"><visual><binding template=\"ToastGeneric\">"
            + $"<text>{SecurityElement.Escape(title)}</text>{second}{image}"
            + "</binding></visual><audio silent=\"true\"/></toast>";
    }
}
#endif
