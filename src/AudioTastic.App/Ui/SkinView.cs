using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using AudioTastic.App.Skins;
using AudioTastic.App.Ui.Controls;
using AudioTastic.Core.Playback;
using AudioTastic.Data.Settings;

namespace AudioTastic.App.Ui;

/// <summary>What a skinned window needs from the player shell.</summary>
public interface ISkinHost
{
    PlayerViewModel Player { get; }

    /// <summary>Current interface settings (scroll title, scroll speed).</summary>
    InterfaceSettings UiSettings { get; }

    /// <summary>Runs a named action (button bind, menu item, key binding).</summary>
    void Execute(string action, Control source);

    /// <summary>Starts moving the window (and its docked group) with the pointer.</summary>
    void BeginDrag(Control source, PointerPressedEventArgs e);

    /// <summary>MW9: right-click on the volume slider opens the output device menu (PB-04).</summary>
    void ShowDeviceMenu(Control source);

    /// <summary>False for toggles of features that do not exist yet (drawn disabled).</summary>
    bool IsAvailable(string bind);

    /// <summary>True when the toggle's window or feature is on (EQ / PL windows; later XF, CAST).</summary>
    bool IsToggleOn(string bind);

    /// <summary>Files dropped on a player window (PL-04).</summary>
    void DropFiles(IReadOnlyList<string> paths, Window window);

    /// <summary>Creates elements the view does not know itself (playlist window parts); null to skip.</summary>
    Control? CreateElement(Skin skin, SkinElement element);
}

/// <summary>
/// UI-03: builds a window's content from a skin: a root border (background, border, corner radius) with every
/// element absolutely positioned, bound to the <see cref="PlayerViewModel"/> and to host actions. Dispose to
/// unsubscribe (on skin switch or window close).
/// </summary>
public sealed class SkinView : Border, IDisposable
{
    private readonly ISkinHost _host;
    private readonly PlayerViewModel _vm;
    private readonly List<Action> _updates = [];
    private readonly List<IDisposable> _disposables = [];
    private readonly Dictionary<string, SkinButton> _toggles = new(StringComparer.Ordinal);

    /// <summary>The logo, decoded once at a size that stays sharp in double size on high-DPI screens.</summary>
    private static readonly Lazy<Avalonia.Media.Imaging.Bitmap> Logo = new(() =>
        Avalonia.Media.Imaging.Bitmap.DecodeToWidth(Avalonia.Platform.AssetLoader.Open(new Uri("avares://AudioTastic/Assets/logo.png")), 256));

    public SkinView(Skin skin, SkinWindowDefinition window, ISkinHost host, double scale)
    {
        _host = host;
        _vm = host.Player;
        if (!window.Resizable)
        {
            Width = window.Width;
            Height = window.Height;
        }
        Background = skin.Brush(window.Background);
        BorderBrush = skin.Brush(window.Border);
        BorderThickness = new Thickness(window.BorderWidth);
        CornerRadius = new CornerRadius(window.CornerRadius);
        ClipToBounds = true;

        var canvas = new AnchorPanel(window.Width - (2 * window.BorderWidth), window.Height - (2 * window.BorderWidth));
        foreach (var element in window.Elements)
        {
            if (Create(skin, element, scale) is { } control)
            {
                // Element coordinates include the window border (they are window coordinates).
                AnchorPanel.Place(control, new Rect(element.X - window.BorderWidth, element.Y - window.BorderWidth, element.Width, element.Height), element.Anchor);
                canvas.Children.Add(control);
                if (control is IDisposable disposable)
                {
                    _disposables.Add(disposable);
                }
            }
        }
        Child = canvas;

        _vm.PropertyChanged += OnPlayerChanged;
        UpdateAll();
    }

    /// <summary>Re-reads toggle states that depend on the shell (EQ/PL windows) or settings.</summary>
    public void UpdateAll()
    {
        foreach (var update in _updates)
        {
            update();
        }
    }

    public void Dispose()
    {
        _vm.PropertyChanged -= OnPlayerChanged;
        foreach (var d in _disposables)
        {
            d.Dispose();
        }
    }

    private void OnPlayerChanged(object? sender, PropertyChangedEventArgs e) => UpdateAll();

    private Control? Create(Skin skin, SkinElement e, double scale)
    {
        switch (e.Type)
        {
            case "rect":
                return new Border
                {
                    Background = skin.Brush(e.Fill),
                    BorderBrush = skin.Brush(e.Stroke),
                    BorderThickness = new Thickness(e.StrokeWidth ?? 0),
                    CornerRadius = new CornerRadius(e.CornerRadius ?? 0),
                    IsHitTestVisible = false,
                };

            case "image":
            {
                var graphic = new SkinGraphic { Margin = new Thickness(e.Padding) };
                graphic.Show(skin, e.ImageFor(SkinState.Normal), skin.Color(e.Color, Colors.White), scale);
                return graphic;
            }

            case "text":
                return new TextBlock
                {
                    Text = e.Text,
                    FontFamily = skin.FontFamily(e.Font),
                    FontWeight = skin.FontWeight(e.Font),
                    FontSize = skin.FontSize(e),
                    LetterSpacing = e.LetterSpacing ?? 0,
                    Foreground = skin.Brush(e.Color) ?? Brushes.White,
                    TextAlignment = TimeDisplay.Align(e.Align),
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false,
                };

            case "button":
            {
                var button = new SkinButton(skin, e, scale);
                var action = e.Bind!;
                button.Clicked += (_, _) => _host.Execute(action, button);
                if (action == "playPause")
                {
                    // 'active' while playing, so a skin can show a pause icon then (images.active).
                    _updates.Add(() => button.SetToggle(_vm.State == PlaybackState.Playing));
                }
                return button;
            }

            case "toggle":
            {
                var toggle = new SkinButton(skin, e, scale);
                var bind = e.Bind!;
                toggle.SetAvailable(_host.IsAvailable(bind));
                toggle.Clicked += (_, _) => _host.Execute(bind, toggle);
                _toggles[bind] = toggle;
                _updates.Add(() => toggle.SetToggle(
                    bind switch
                    {
                        "shuffle" => _vm.Shuffle,
                        "repeat" => _vm.Repeat != RepeatMode.Off,
                        "mute" => _vm.Muted,
                        _ => _host.IsToggleOn(bind),
                    },
                    alternate: bind == "repeat" && _vm.Repeat == RepeatMode.Track));
                return toggle;
            }

            case "slider" when e.Bind == "volume":
            {
                var slider = new SkinSlider(skin, e);
                slider.ValueChanging += (_, v) => _vm.Volume = (int)Math.Round(v * 100);
                slider.ValueCommitted += (_, v) => _vm.Volume = (int)Math.Round(v * 100);
                slider.ContextRequested += (_, args) =>
                {
                    args.Handled = true;
                    _host.ShowDeviceMenu(slider);
                };
                _updates.Add(() => slider.Value = _vm.Volume / 100.0);
                return slider;
            }

            case "slider" when e.Bind == "seek":
            {
                var slider = new SkinSlider(skin, e);
                slider.ValueCommitted += (_, v) =>
                {
                    if (_vm.Duration is { } d)
                    {
                        _ = _vm.Player.SeekAsync(d * v);
                    }
                };
                _updates.Add(() =>
                {
                    var canSeek = _vm.HasTrack && _vm.Duration is { } length && length > TimeSpan.Zero;
                    slider.SetAvailable(canSeek);
                    slider.Value = canSeek ? _vm.Position / _vm.Duration!.Value : 0;
                });
                return slider;
            }

            case "time" when e.Bind == "duration":
            {
                var length = new TimeDisplay(skin, e) { Cursor = Cursor.Default };
                ToolTip.SetTip(length, "Track length");
                _updates.Add(() => length.Text = _vm.Duration is { } d && _vm.Track is not null ? DisplayText.Time(d) : string.Empty);
                return length;
            }

            case "time":
            {
                var time = new TimeDisplay(skin, e);
                time.Clicked += (_, _) => _vm.ShowRemaining = !_vm.ShowRemaining;
                _updates.Add(() =>
                {
                    time.Text = _vm.TimeText;
                    time.SetBlinking(_vm.State == PlaybackState.Paused);
                });
                return time;
            }

            case "marquee":
            {
                var marquee = new MarqueeText(skin, e);
                _updates.Add(() =>
                {
                    marquee.Scroll = _host.UiSettings.ScrollTitle;
                    marquee.Speed = _host.UiSettings.ScrollSpeed > 0 ? _host.UiSettings.ScrollSpeed : e.Speed ?? 25;
                    marquee.Text = _vm.Status ?? _vm.Title;
                });
                return marquee;
            }

            case "indicators":
            {
                var bar = new IndicatorBar(skin, e);
                IReadOnlyList<string>? shown = null;
                _updates.Add(() =>
                {
                    if (!ReferenceEquals(shown, _vm.Indicators))
                    {
                        shown = _vm.Indicators;
                        bar.SetItems(shown);
                    }
                });
                return bar;
            }

            case "dragArea":
            {
                var area = new Border { Background = Brushes.Transparent };
                area.PointerPressed += (_, args) =>
                {
                    if (args.GetCurrentPoint(area).Properties.IsLeftButtonPressed && args.ClickCount == 1)
                    {
                        _host.BeginDrag(area, args);
                    }
                };
                area.DoubleTapped += (_, _) => _host.Execute("titleDoubleClick", area);
                return area;
            }

            case "art":
                // MD-02 (P9) shows album art here; until then the logo.
                return new Image
                {
                    Source = Logo.Value,
                    Stretch = Stretch.Uniform,
                    Opacity = 0.9,
                    IsHitTestVisible = false,
                };

            case "visualisation":
                // UI-07 (P7).
                return new Border { IsHitTestVisible = false };

            case "resizeGrip":
            {
                var grip = new Border { Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.BottomRightCorner) };
                grip.PointerPressed += (_, args) =>
                {
                    if (TopLevel.GetTopLevel(grip) is Window w && args.GetCurrentPoint(grip).Properties.IsLeftButtonPressed)
                    {
                        w.BeginResizeDrag(WindowEdge.SouthEast, args);
                        args.Handled = true;
                    }
                };
                ToolTip.SetTip(grip, "Resize");
                return grip;
            }

            default:
                return _host.CreateElement(skin, e);
        }
    }
}
