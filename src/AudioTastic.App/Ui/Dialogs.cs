using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AudioTastic.Data;
using AudioTastic.Platform;

namespace AudioTastic.App.Ui;

/// <summary>A small dialog with a message and a list of details (skin errors).</summary>
public sealed class MessageWindow : Window
{
    private MessageWindow(string title, string message, IReadOnlyList<string> details)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var ok = new Button { Content = "OK", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true, MinWidth = 80 };
        ok.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new TextBox
                {
                    Text = string.Join(Environment.NewLine, details),
                    IsReadOnly = true,
                    TextWrapping = TextWrapping.Wrap,
                    MaxHeight = 260,
                    IsVisible = details.Count > 0,
                },
                ok,
            },
        };
    }

    public static Task ShowAsync(Window owner, string title, string message, IReadOnlyList<string> details) =>
        new MessageWindow(title, message, details).ShowDialog(owner);
}

/// <summary>A question with two answers (SI-05 first-start question). Returns true for the first (default) button.</summary>
public sealed class QuestionWindow : Window
{
    private bool _yes;

    private QuestionWindow(string title, string message, string yes, string no)
    {
        Title = title;
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var yesButton = new Button { Content = yes, IsDefault = true, MinWidth = 80 };
        yesButton.Click += (_, _) =>
        {
            _yes = true;
            Close();
        };
        var noButton = new Button { Content = no, IsCancel = true, MinWidth = 80 };
        noButton.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { yesButton, noButton } },
            },
        };
        Opened += (_, _) => yesButton.Focus();
    }

    public static async Task<bool> AskAsync(Window owner, string title, string message, string yes, string no)
    {
        var window = new QuestionWindow(title, message, yes, no);
        await window.ShowDialog(owner);
        return window._yes;
    }
}

/// <summary>A one-line text prompt (FM-10 Open URL). Returns the text, or null when cancelled or empty.</summary>
public sealed class InputWindow : Window
{
    private readonly TextBox _input;
    private string? _result;

    private InputWindow(string title, string prompt, string? initial, string watermark)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        _input = new TextBox { Text = initial, PlaceholderText = watermark };
        Avalonia.Automation.AutomationProperties.SetName(_input, prompt);
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80 };
        ok.Click += (_, _) =>
        {
            _result = string.IsNullOrWhiteSpace(_input.Text) ? null : _input.Text.Trim();
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
                new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap },
                _input,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { ok, cancel } },
            },
        };
        Opened += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
    }

    public static async Task<string?> ShowAsync(Window owner, string title, string prompt, string? initial = null, string watermark = "")
    {
        var window = new InputWindow(title, prompt, initial, watermark);
        await window.ShowDialog(owner);
        return window._result;
    }
}

/// <summary>UI-04: About AudioTastic — logo, version, data folder and third-party notices.</summary>
public sealed class AboutWindow : Window
{
    public AboutWindow(AppPaths paths, IPlatformServices platform)
    {
        Title = "About AudioTastic";
        Width = 560;
        Height = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://AudioTastic/Assets/audiotastic.ico")));

        var version = typeof(AboutWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
        var plus = version.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0)
        {
            version = version[..Math.Min(version.Length, plus + 8)]; // short commit hash
        }
        string notices;
        using (var stream = typeof(AboutWindow).Assembly.GetManifestResourceStream("AudioTastic.ThirdPartyNotices.md"))
        using (var reader = new StreamReader(stream!))
        {
            notices = reader.ReadToEnd();
        }

        var dataLink = new Button { Content = paths.Root, Padding = new Thickness(0), Background = Brushes.Transparent, Foreground = Brushes.Gray };
        dataLink.Click += (_, _) => platform.OpenFolder(paths.Root);
        ToolTip.SetTip(dataLink, "Open the data folder");
        var close = new Button { Content = "Close", HorizontalAlignment = HorizontalAlignment.Right, IsDefault = true, IsCancel = true, MinWidth = 80 };
        close.Click += (_, _) => Close();

        var header = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 16,
            Children =
            {
                new Image
                {
                    Source = Bitmap.DecodeToWidth(Avalonia.Platform.AssetLoader.Open(new Uri("avares://AudioTastic/Assets/logo.png")), 192),
                    Width = 96,
                    Height = 96,
                },
                new StackPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Spacing = 4,
                    Children =
                    {
                        new TextBlock { Text = "AudioTastic Desktop", FontSize = 22, FontWeight = FontWeight.Light },
                        new TextBlock { Text = $"Version {version}", Foreground = Brushes.Gray },
                        new TextBlock { Text = $"{platform.Name} · .NET {Environment.Version}", Foreground = Brushes.Gray },
                        dataLink,
                    },
                },
            },
        };
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), Margin = new Thickness(16) };
        grid.Children.Add(header);
        var label = new TextBlock { Text = "Third-party components", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 16, 0, 6) };
        Grid.SetRow(label, 1);
        grid.Children.Add(label);
        var text = new TextBox { Text = notices, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        Grid.SetRow(text, 2);
        grid.Children.Add(text);
        close.Margin = new Thickness(0, 12, 0, 0);
        Grid.SetRow(close, 3);
        grid.Children.Add(close);
        Content = grid;
    }
}
