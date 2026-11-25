using AudioTastic.App.Skins;
using AudioTastic.App.Ui;
using AudioTastic.App.Ui.Controls;
using AudioTastic.App.Ui.Playlist;
using AudioTastic.Core.Playlists;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Metadata;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Headless;
using Avalonia.Input;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.App.Tests;

/// <summary>
/// TL-04: what screen readers and keyboard users get from the custom-drawn skin controls: roles and patterns of
/// their automation peers, names and shortcut keys, and keyboard handling. Runs the controls on Avalonia's headless
/// platform (no window system).
/// </summary>
public sealed class AccessibilityTests : IDisposable
{
    private static readonly Lazy<HeadlessUnitTestSession> Session = new(() => HeadlessUnitTestSession.StartNew(typeof(HeadlessApp)));

    private readonly string _dir = Directory.CreateTempSubdirectory("audiotastic-a11y-").FullName;
    private readonly Skin _skin;

    public AccessibilityTests()
    {
        var paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, _dir, _dir, null, null, [AppPaths.PortableSwitch])).EnsureCreated();
        _skin = new SkinManager(paths, new SettingsStore(paths), NullLogger<SkinManager>.Instance).Current;
    }

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    /// <summary>Runs <paramref name="test"/> on the headless UI thread.</summary>
    private static Task Ui(Action test) => Session.Value.Dispatch(test, TestContext.Current.CancellationToken);

    private static SkinElement Element(string type, string? bind, string? tooltip = null) => new() { Type = type, Bind = bind, Tooltip = tooltip, Width = 20, Height = 10 };

    private static void Press(Avalonia.Controls.Control control, Key key) =>
        control.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = key, Source = control });

    [Fact]
    public void Names_come_from_the_action_and_the_key_from_the_key_map()
    {
        Assert.Equal("Previous track", Accessible.NameFor(Element("button", "previous", "Previous (Z)")));
        Assert.Equal("Some custom thing", Accessible.NameFor(Element("button", "custom", "Some custom thing (Q)")));
        Assert.Equal("button", Accessible.NameFor(Element("button", null)));
        Assert.Equal("menu", KeyMap.ActionFor(Key.F, KeyModifiers.Alt));
        Assert.Equal("Alt+F", KeyMap.GestureFor("menu"));
    }

    [Fact]
    public Task A_skin_button_is_a_button_that_Enter_and_screen_readers_press() => Ui(() =>
    {
        var button = new SkinButton(_skin, Element("button", "next", "Next (B)"), 1);
        var clicks = 0;
        button.Clicked += (_, _) => clicks++;
        var peer = ControlAutomationPeer.CreatePeerForElement(button);
        Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
        Assert.Equal("Next track", peer.GetName());
        Assert.Equal("B", AutomationProperties.GetAcceleratorKey(button));
        Assert.Equal("Next (B)", peer.GetHelpText());
        Assert.True(button.Focusable);
        Assert.Null(peer.GetProvider<IToggleProvider>());
        peer.GetProvider<IInvokeProvider>()!.Invoke();
        Press(button, Key.Enter);
        Press(button, Key.Space);
        Assert.Equal(3, clicks);

        button.SetAvailable(false); // a feature that does not exist here
        Press(button, Key.Enter);
        Assert.Equal(3, clicks);
        Assert.False(peer.IsEnabled());
    });

    [Fact]
    public Task A_skin_toggle_reports_its_state() => Ui(() =>
    {
        var toggle = new SkinButton(_skin, Element("toggle", "shuffle", "Shuffle (S)"), 1);
        var clicks = 0;
        toggle.Clicked += (_, _) => clicks++;
        var peer = ControlAutomationPeer.CreatePeerForElement(toggle);
        var provider = peer.GetProvider<IToggleProvider>()!;
        Assert.Equal(ToggleState.Off, provider.ToggleState);
        toggle.SetToggle(true);
        Assert.Equal(ToggleState.On, provider.ToggleState);
        provider.Toggle();
        Assert.Equal(1, clicks); // the player decides; the shell sets the state back through SetToggle
    });

    [Fact]
    public Task A_skin_slider_moves_with_the_keys_and_a_screen_reader() => Ui(() =>
    {
        var slider = new SkinSlider(_skin, Element("slider", "volume", "Volume (Up/Down, wheel)")) { Value = 0.5, SmallStep = 0.02, LargeStep = 0.1 };
        var committed = new List<double>();
        slider.ValueCommitted += (_, v) => committed.Add(Math.Round(v, 3));
        var peer = ControlAutomationPeer.CreatePeerForElement(slider);
        Assert.Equal(AutomationControlType.Slider, peer.GetAutomationControlType());
        Assert.Equal("Volume", peer.GetName());
        var range = peer.GetProvider<IRangeValueProvider>()!;
        Assert.Equal(50, range.Value);
        Assert.Equal("50 %", peer.GetProvider<IValueProvider>()!.Value?.Replace('\u00A0', ' '));

        Press(slider, Key.Up);
        Press(slider, Key.Left);
        Press(slider, Key.PageUp);
        Press(slider, Key.End);
        Press(slider, Key.Home);
        range.SetValue(30);
        Assert.Equal([0.52, 0.5, 0.6, 1, 0, 0.3], committed);

        slider.DescribeValue = v => $"{v * 10:0} of 10";
        Assert.Equal("3 of 10", peer.GetProvider<IValueProvider>()!.Value);
        slider.SetAvailable(false);
        Assert.True(range.IsReadOnly);
        Press(slider, Key.Up);
        Assert.Equal(6, committed.Count);
    });

    [Fact]
    public Task Playlist_tabs_and_rows_read_as_what_they_are() => Ui(() =>
    {
        var shown = 0;
        var tab = new AccessibleBorder("Playlist Party, shown", () => shown++);
        var peer = ControlAutomationPeer.CreatePeerForElement(tab);
        Assert.Equal(AutomationControlType.Button, peer.GetAutomationControlType());
        Assert.Equal("Playlist Party, shown", peer.GetName());
        Press(tab, Key.Enter);
        peer.GetProvider<IInvokeProvider>()!.Invoke();
        Assert.Equal(2, shown);

        var entry = new PlaylistEntry(@"C:\music\song.flac", tags: new TrackTags(Title: "Song", Artist: "Band", Duration: TimeSpan.FromSeconds(185)));
        Assert.Equal("3. Band – Song, 3:05", new RowItem(entry, 3).ToString());
    });

    [Fact]
    public Task Text_drawn_by_the_controls_is_read_and_empty_text_is_skipped() => Ui(() =>
    {
        var marquee = new MarqueeText(_skin, Element("marquee", null)) { Text = "Band – Song (3:05)" };
        var peer = ControlAutomationPeer.CreatePeerForElement(marquee);
        Assert.Equal(AutomationControlType.Text, peer.GetAutomationControlType());
        Assert.Equal("Band – Song (3:05)", peer.GetName());
        Assert.Equal(AutomationLiveSetting.Polite, AutomationProperties.GetLiveSetting(marquee)); // new titles are announced

        var badges = new IndicatorBar(_skin, Element("indicators", "state"));
        badges.SetItems([]);
        Assert.False(ControlAutomationPeer.CreatePeerForElement(badges).IsControlElement());
        badges.SetItems(["FLAC", "44.1 KHZ"]);
        Assert.Equal("flac, 44.1 kilohertz", ControlAutomationPeer.CreatePeerForElement(badges).GetName());
    });

    /// <summary>The headless platform for these tests.</summary>
    public sealed class HeadlessApp : Application
    {
        public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<HeadlessApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
    }
}
