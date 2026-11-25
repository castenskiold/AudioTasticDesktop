using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AudioTastic.App.Skins;

namespace AudioTastic.App.Ui;

/// <summary>
/// UI-01: a borderless player window whose whole content comes from the skin (main window or mini player).
/// Keys go through <see cref="KeyMap"/>, right-click opens the main menu, the mouse wheel changes the volume,
/// and files dropped on it are played.
/// </summary>
#pragma warning disable CA1001 // The skin view is disposed in OnClosed (windows are not IDisposable).
public sealed class SkinnedWindow : Window
#pragma warning restore CA1001
{
    private readonly ISkinHost _host;
    private SkinView? _view;
    private double _builtScale;

    public SkinnedWindow(string skinWindowName, ISkinHost host)
    {
        SkinWindowName = skinWindowName;
        _host = host;
        Title = "AudioTastic";
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://AudioTastic/Assets/audiotastic.ico")));
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;
        Background = Brushes.Transparent;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, OnDrop);
        AddHandler(ContextRequestedEvent, OnContextRequested);
        // TL-04: each window is announced by what it is.
        Avalonia.Automation.AutomationProperties.SetName(this, skinWindowName switch
        {
            "mini" => "AudioTastic mini player",
            "playlist" => "Playlist",
            "equalizer" => "Equalizer",
            "art" => "Art and lyrics",
            _ => "AudioTastic",
        });
    }

    /// <summary>"main" or "mini".</summary>
    public string SkinWindowName { get; }

    /// <summary>Rebuilds the content from <paramref name="skin"/> at <paramref name="scale"/> (2 = double size, UI-08).</summary>
    public void Build(Skin skin, SkinWindowDefinition definition, double scale)
    {
        _view?.Dispose();
        _view = new SkinView(skin, definition, _host, scale);
        Content = scale == 1
            ? _view
            : new LayoutTransformControl { LayoutTransform = new ScaleTransform(scale, scale), Child = _view };
        if (definition.Resizable)
        {
            // UI-01: resizable windows keep their size in layout units across skin and scale changes.
            CanResize = true;
            MinWidth = (definition.MinWidth ?? definition.Width) * scale;
            MinHeight = (definition.MinHeight ?? definition.Height) * scale;
            var (w, h) = _builtScale > 0 && !double.IsNaN(Width)
                ? (Width / _builtScale, Height / _builtScale)
                : (definition.Width, definition.Height);
            Width = Math.Max(w * scale, MinWidth);
            Height = Math.Max(h * scale, MinHeight);
        }
        else
        {
            Width = definition.Width * scale;
            Height = definition.Height * scale;
        }
        _builtScale = scale;
    }

    /// <summary>Refreshes toggles that depend on the shell (window toggles, settings).</summary>
    public void Refresh() => _view?.UpdateAll();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (!e.Handled && KeyMap.ActionFor(e.Key, e.KeyModifiers) is { } action)
        {
            e.Handled = true;
            _host.Execute(action, this);
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (e.Delta.Y != 0)
        {
            _host.Player.Volume += e.Delta.Y > 0 ? 2 : -2; // CO-05: mouse wheel over the main window
            e.Handled = true;
        }
    }

    private void OnContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        // Right-click anywhere opens the main menu, unless an element (volume → device menu) handled it first.
        if (!e.Handled)
        {
            e.Handled = true;
            _host.Execute("menu", this);
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _view?.Dispose();
        base.OnClosed(e);
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled)
        {
            return; // the playlist list handles drops on itself (insert position)
        }
        var files = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        if (files is { Length: > 0 })
        {
            e.Handled = true;
            _host.DropFiles(files, this);
        }
    }
}
