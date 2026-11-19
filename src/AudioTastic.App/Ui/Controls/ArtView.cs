using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AudioTastic.App.Skins;

namespace AudioTastic.App.Ui.Controls;

/// <summary>
/// MD-02: skin element <c>art</c>: the playing track's cover (or the logo), uniformly scaled and centred, with the
/// element's <c>cornerRadius</c>. With <paramref name="click"/> (the main window's MW2) a click opens the Art &amp;
/// Lyrics window.
/// </summary>
public sealed class ArtView : Border
{
    private readonly ArtCoordinator _art;
    private readonly Bitmap _logo;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };

    public ArtView(SkinElement element, ArtCoordinator art, Bitmap logo, Action? click)
    {
        ArgumentNullException.ThrowIfNull(element);
        _art = art;
        _logo = logo;
        Background = Brushes.Transparent; // the whole area is clickable
        ClipToBounds = true;
        CornerRadius = new CornerRadius(element.CornerRadius ?? 0);
        Child = _image;
        Avalonia.Automation.AutomationProperties.SetName(this, "Album art");
        if (click is not null)
        {
            Cursor = new Cursor(StandardCursorType.Hand);
            ToolTip.SetTip(this, element.Tooltip ?? "Album art: click for the Art & Lyrics window (Alt+L)");
            PointerPressed += (_, e) =>
            {
                if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
                {
                    e.Handled = true;
                    click();
                }
            };
        }
        Show();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _art.Changed += OnChanged;
        Show();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _art.Changed -= OnChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnChanged(object? sender, EventArgs e) => Show();

    private void Show()
    {
        _image.Source = _art.Image ?? (IImage)_logo;
        _image.Opacity = _art.Image is null ? 0.9 : 1;
    }
}
