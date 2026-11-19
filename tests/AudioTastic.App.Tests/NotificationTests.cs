using AudioTastic.App.Integration;

namespace AudioTastic.App.Tests;

/// <summary>SI-07 notifications.</summary>
public sealed class NotificationTests
{
    [Fact]
    public void Second_line_combines_artist_and_album()
    {
        Assert.Equal("Artist — Album", NotificationService.SecondLine("Artist", "Album"));
        Assert.Equal("Artist", NotificationService.SecondLine("Artist", null));
        Assert.Equal("Album", NotificationService.SecondLine(" ", "Album"));
        Assert.Null(NotificationService.SecondLine(null, ""));
    }

#if WINDOWS
    [Fact]
    public void Toast_xml_escapes_text_and_uses_a_file_uri()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            return;
        }
        var xml = AudioTastic.Platform.Windows.WindowsNotifications.ToastXml("Rock & <Roll>", "A \"B\"", @"C:\Temp\cover 1.jpg");
        Assert.Contains("<text>Rock &amp; &lt;Roll&gt;</text>", xml, StringComparison.Ordinal);
        Assert.Contains("<text>A &quot;B&quot;</text>", xml, StringComparison.Ordinal);
        Assert.Contains("src=\"file:///C:/Temp/cover%201.jpg\"", xml, StringComparison.Ordinal);
        Assert.Contains("<audio silent=\"true\"/>", xml, StringComparison.Ordinal);
        Assert.DoesNotContain("<image", AudioTastic.Platform.Windows.WindowsNotifications.ToastXml("T", null, null), StringComparison.Ordinal);
        var doc = new System.Xml.XmlDocument();
        doc.LoadXml(xml); // well-formed
    }
#endif
}
