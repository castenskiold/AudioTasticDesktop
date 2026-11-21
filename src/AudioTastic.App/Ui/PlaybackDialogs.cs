using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AudioTastic.Core.Playback;

namespace AudioTastic.App.Ui;

/// <summary>PB-06: Main menu › Playback › Speed…: speed slider (0.5–2.0×), Keep pitch and Reset; changes apply at once.</summary>
public sealed class SpeedWindow : Window
{
    public SpeedWindow(IPlayerController player)
    {
        ArgumentNullException.ThrowIfNull(player);
        Title = "Speed";
        Width = 340;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var value = new TextBlock { FontSize = 20, FontWeight = FontWeight.Light, HorizontalAlignment = HorizontalAlignment.Center };
        var slider = new Slider { Minimum = 0.5, Maximum = 2.0, SmallChange = 0.05, LargeChange = 0.25, TickFrequency = 0.05, IsSnapToTickEnabled = true, Value = player.Speed };
        Avalonia.Automation.AutomationProperties.SetName(slider, "Playback speed");
        var keepPitch = new CheckBox { Content = "Keep pitch (time-stretch)", IsChecked = player.KeepPitch };
        var reset = new Button { Content = "Reset (1.0×)", HorizontalAlignment = HorizontalAlignment.Left };
        void Show() => value.Text = string.Create(CultureInfo.InvariantCulture, $"{slider.Value:0.00}×");
        Show();
        slider.ValueChanged += (_, _) =>
        {
            player.Speed = slider.Value;
            Show();
        };
        keepPitch.IsCheckedChanged += (_, _) => player.KeepPitch = keepPitch.IsChecked == true;
        reset.Click += (_, _) => slider.Value = 1.0;
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 80 };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                value,
                slider,
                keepPitch,
                new TextBlock
                {
                    Text = "Without Keep pitch, speed and pitch change together, like a turntable. At 1.0× the sound is untouched.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.Gray,
                },
                new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { reset, Column(close) } },
            },
        };
    }

    private static Button Column(Button button)
    {
        Grid.SetColumn(button, 1);
        return button;
    }
}

/// <summary>PB-10: Main menu › Sleep timer…: stop after a time, at the end of the track or of the playlist.</summary>
public sealed class SleepTimerWindow : Window
{
    private static readonly int[] Minutes = [15, 30, 45, 60, 90];

    public SleepTimerWindow(SleepTimerService timer)
    {
        ArgumentNullException.ThrowIfNull(timer);
        Title = "Sleep timer";
        Width = 340;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var options = new List<RadioButton>();
        RadioButton Option(string text)
        {
            var radio = new RadioButton { Content = text, GroupName = "sleep" };
            options.Add(radio);
            return radio;
        }
        var panel = new StackPanel { Spacing = 6 };
        foreach (var m in Minutes)
        {
            panel.Children.Add(Option($"After {m} minutes"));
        }
        var custom = Option("After");
        var customMinutes = new NumericUpDown { Minimum = 1, Maximum = 600, Value = 120, FormatString = "0", Width = 110 };
        Avalonia.Automation.AutomationProperties.SetName(customMinutes, "Custom minutes");
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { custom, customMinutes, new TextBlock { Text = "minutes", VerticalAlignment = VerticalAlignment.Center } } });
        var endOfTrack = Option("At the end of the current track");
        var endOfPlaylist = Option("At the end of the playlist");
        panel.Children.Add(endOfTrack);
        panel.Children.Add(endOfPlaylist);
        options[1].IsChecked = true;

        var status = new TextBlock { Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap };
        status.Text = timer.Mode is null ? "The sleep timer is off." : $"Running: {Describe(timer)}.";
        var start = new Button { Content = "Start", IsDefault = true, MinWidth = 80 };
        start.Click += (_, _) =>
        {
            if (endOfTrack.IsChecked == true)
            {
                timer.StartAtEndOfTrack();
            }
            else if (endOfPlaylist.IsChecked == true)
            {
                timer.StartAtEndOfPlaylist();
            }
            else
            {
                var minutes = custom.IsChecked == true ? (int)(customMinutes.Value ?? 60) : Minutes[Math.Max(0, options.FindIndex(o => o.IsChecked == true))];
                timer.StartAfter(TimeSpan.FromMinutes(minutes));
            }
            Close();
        };
        var off = new Button { Content = "Turn off", MinWidth = 80, IsEnabled = timer.Mode is not null };
        off.Click += (_, _) =>
        {
            timer.Cancel();
            Close();
        };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                panel,
                new TextBlock { Text = "Fade-out and closing the app afterwards are set in Settings › Playback.", FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap },
                status,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { off, cancel, start } },
            },
        };
    }

    public static string Describe(SleepTimerService timer) => timer.Mode switch
    {
        SleepMode.AfterTime => $"stops in {Math.Ceiling(timer.Remaining?.TotalMinutes ?? 0)} min",
        SleepMode.EndOfTrack => "stops at the end of the track",
        SleepMode.EndOfPlaylist => "stops at the end of the playlist",
        _ => "off",
    };
}

/// <summary>
/// CN-09: CAST › TV › Picture delay…: how late the TV shows the visuals. Changes apply to the running stream at once,
/// so the user tunes it by eye while watching (stored per TV).
/// </summary>
public sealed class PictureDelayWindow : Window
{
    public PictureDelayWindow(AudioTastic.App.Integration.CastService cast, AudioTastic.Casting.Discovery.CastDevice device)
    {
        ArgumentNullException.ThrowIfNull(cast);
        ArgumentNullException.ThrowIfNull(device);
        Title = $"Picture delay: {device.Name}";
        Opened += (_, _) => cast.Calibrating = true; // the TV flashes on each kick while this is open
        Closed += (_, _) => cast.Calibrating = false;
        Width = 380;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var value = new TextBlock { FontSize = 20, FontWeight = FontWeight.Light, HorizontalAlignment = HorizontalAlignment.Center };
        var slider = new Slider
        {
            Minimum = 0, Maximum = 10_000, SmallChange = 50, LargeChange = 500, TickFrequency = 50, IsSnapToTickEnabled = true,
            Value = cast.VisualDelay(device).TotalMilliseconds,
        };
        Avalonia.Automation.AutomationProperties.SetName(slider, "Picture delay in milliseconds");
        void Show() => value.Text = string.Create(CultureInfo.InvariantCulture, $"{slider.Value / 1000:0.00} s");
        Show();
        slider.ValueChanged += (_, _) =>
        {
            cast.SetVisualDelay(device, (int)slider.Value);
            Show();
        };
        Button Step(string label, int ms, string name)
        {
            var button = new Button { Content = label, MinWidth = 64 };
            Avalonia.Automation.AutomationProperties.SetName(button, name);
            button.Click += (_, _) => slider.Value = Math.Clamp(slider.Value + ms, slider.Minimum, slider.Maximum);
            return button;
        }
        var reset = new Button { Content = $"Default ({AudioTastic.Data.Settings.CastingNetworkSettings.DefaultVisualDelayMs / 1000.0:0.#} s)" };
        reset.Click += (_, _) => slider.Value = AudioTastic.Data.Settings.CastingNetworkSettings.DefaultVisualDelayMs;
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 80 };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                value,
                slider,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center,
                    Children = { Step("−100 ms", -100, "Picture earlier by 100 milliseconds"), Step("+100 ms", 100, "Picture later by 100 milliseconds") },
                },
                new TextBlock
                {
                    Text = "While this window is open the TV flashes white on every kick. Play music with a clear kick: if the flash comes " +
                           "after you hear the kick, increase the delay; if it comes before, decrease it. Closing the window brings the " +
                           "visualisation back. Internet radio cannot be read ahead, so there the picture always trails.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12, Foreground = Brushes.Gray,
                },
                new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Children = { reset, Column(close) } },
            },
        };
    }

    private static Button Column(Button button)
    {
        Grid.SetColumn(button, 1);
        return button;
    }
}
