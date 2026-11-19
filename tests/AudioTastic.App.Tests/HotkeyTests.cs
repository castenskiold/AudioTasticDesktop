using AudioTastic.App.Integration;
using AudioTastic.App.Ui;
using AudioTastic.Data.Settings;
using AudioTastic.Platform;
using AudioTastic.Platform.Windows;
using Avalonia.Input;

namespace AudioTastic.App.Tests;

/// <summary>SI-01 global hotkeys.</summary>
public sealed class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Alt+Home", "Ctrl+Alt+Home")]
    [InlineData("alt + ctrl + pageup", "Ctrl+Alt+PageUp")]
    [InlineData("Win+Shift+F24", "Shift+Win+F24")]
    [InlineData("Control+Alt+5", "Ctrl+Alt+5")]
    public void Hotkeys_parse_in_any_order_and_format_canonically(string text, string expected) =>
        Assert.Equal(expected, Hotkey.Parse(text)!.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("Home")]                 // no modifier
    [InlineData("Shift+Home")]           // Shift alone would take over typing
    [InlineData("Ctrl+Alt")]             // no key
    [InlineData("Ctrl+Alt+Home+End")]    // two keys
    [InlineData("Ctrl+Alt+Banana")]      // unknown key
    public void Invalid_hotkeys_are_rejected(string text) => Assert.Null(Hotkey.Parse(text));

    [Fact]
    public void Every_key_name_has_a_windows_key_code_and_an_avalonia_key()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var name in Hotkey.KeyNames)
            {
                Assert.NotNull(WindowsHotkeys.VirtualKey(name));
            }
            Assert.Equal(0x24, WindowsHotkeys.VirtualKey("Home"));
            Assert.Equal(0x41, WindowsHotkeys.VirtualKey("A"));
            Assert.Equal(0x87, WindowsHotkeys.VirtualKey("F24"));
            Assert.Equal(0x65, WindowsHotkeys.VirtualKey("NumPad5"));
        }
        Assert.Equal("PageUp", HotkeyBox.KeyName(Key.PageUp));
        Assert.Equal("7", HotkeyBox.KeyName(Key.D7));
        Assert.Equal("NumPad3", HotkeyBox.KeyName(Key.NumPad3));
        Assert.Null(HotkeyBox.KeyName(Key.LeftCtrl));
        Assert.All(new[] { Key.A, Key.F5, Key.Home, Key.OemPlus, Key.Divide }, key => Assert.Contains(HotkeyBox.KeyName(key)!, Hotkey.KeyNames));
    }

    [Fact]
    public void Settings_give_the_default_hotkeys_and_nothing_when_switched_off()
    {
        var settings = new SystemIntegrationSettings();
        var (hotkeys, invalid) = HotkeyManager.Plan(settings);
        Assert.Equal(6, hotkeys.Count);
        Assert.Equal("Ctrl+Alt+Home", hotkeys["playPause"].ToString());
        Assert.True(hotkeys["volumeUp"].Repeat);
        Assert.False(hotkeys["next"].Repeat);
        Assert.Empty(invalid);

        settings.Hotkeys["mute"] = "Shift+M";
        Assert.Equal(["mute"], HotkeyManager.Plan(settings).Invalid);

        settings.GlobalHotkeys = false;
        Assert.Empty(HotkeyManager.Plan(settings).Hotkeys);
    }

    [Fact]
    public void Windows_refuses_a_combination_another_registration_already_has()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Windows RegisterHotKey");
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var first = new WindowsHotkeys();
        using var second = new WindowsHotkeys();
        var rare = new Dictionary<string, Hotkey> { ["test"] = new(HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift, "F24") };
        Assert.Empty(first.Register(rare));
        Assert.Equal(["test"], second.Register(rare)); // taken
        Assert.Empty(first.Register(new Dictionary<string, Hotkey>())); // released
        Assert.Empty(second.Register(rare));
    }
}
