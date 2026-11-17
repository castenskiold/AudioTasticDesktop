using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using AudioTastic.Data;
using AudioTastic.Platform;
using AudioTastic.Plugins.Host;
using AudioTastic.Sdk.Plugins;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Ui;

/// <summary>
/// PG-04: Settings › Plugins. Lists every plugin with name, version, author, types and status; install from
/// .atplugin, enable/disable, uninstall, open the plugin's settings (PG-05) and see error details.
/// </summary>
internal sealed class PluginManagerPage : StackPanel
{
    private readonly PluginHost _host;
    private readonly AppPaths _paths;
    private readonly IPlatformServices _platform;
    private readonly ILogger _logger;
    private readonly StackPanel _list = new() { Spacing = 8 };

    public PluginManagerPage(PluginHost host, AppPaths paths, IPlatformServices platform, ILogger logger)
    {
        _host = host;
        _paths = paths;
        _platform = platform;
        _logger = logger;
        Spacing = 12;

        var install = new Button { Content = "Install plugin…" };
        install.Click += async (_, _) => await InstallAsync();
        var folder = new Button { Content = "Open plugins folder" };
        folder.Click += (_, _) =>
        {
            Directory.CreateDirectory(_paths.PluginsFolder);
            _platform.OpenFolder(_paths.PluginsFolder);
        };
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { install, folder } });
        Children.Add(new TextBlock
        {
            Text = "Plugins run inside the player. Only install plugins you trust: a plugin that keeps failing is switched off, " +
                   "but a native crash inside a plugin can still close the player.",
            TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12, MaxWidth = 480, HorizontalAlignment = HorizontalAlignment.Left,
        });
        Children.Add(_list);
        Refresh();
    }

    private void Refresh()
    {
        _list.Children.Clear();
        var plugins = _host.Plugins;
        if (plugins.Count == 0)
        {
            _list.Children.Add(new TextBlock { Text = "No plugins found.", Foreground = Brushes.Gray });
        }
        foreach (var plugin in plugins)
        {
            _list.Children.Add(Card(plugin));
        }
    }

    private Border Card(PluginRecord plugin)
    {
        var m = plugin.Manifest;
        var body = new StackPanel { Spacing = 3 };
        body.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = m.Name, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = m.Version, Foreground = Brushes.Gray },
            },
        });
        var details = string.Join(" · ", new[]
        {
            m.Author is { Length: > 0 } author ? $"by {author}" : null,
            m.Types.Count > 0 ? string.Join(", ", m.Types.Select(TypeName)) : null,
            plugin.IsBuiltIn ? "built in" : "installed",
        }.OfType<string>());
        body.Children.Add(new TextBlock { Text = details, FontSize = 12, Foreground = Brushes.Gray });
        if (m.Description is { Length: > 0 } description)
        {
            body.Children.Add(new TextBlock { Text = description, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, HorizontalAlignment = HorizontalAlignment.Left });
        }
        body.Children.Add(new TextBlock { Text = StatusText(plugin.Status), FontSize = 12, FontWeight = FontWeight.SemiBold, Foreground = StatusBrush(plugin.Status) });
        if (plugin.Error is { Length: > 0 } error)
        {
            body.Children.Add(new SelectableTextBlock { Text = error, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, Foreground = Brushes.IndianRed });
        }

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 4, 0, 0) };
        if (plugin.Status != PluginStatus.Incompatible)
        {
            var on = plugin.Status == PluginStatus.Enabled;
            var toggle = new Button { Content = on ? "Disable" : "Enable" };
            Avalonia.Automation.AutomationProperties.SetName(toggle, $"{(on ? "Disable" : "Enable")} {m.Name}");
            toggle.Click += (_, _) => Act(() =>
            {
                if (on)
                {
                    _host.Disable(plugin.Id);
                }
                else
                {
                    _host.Enable(plugin.Id);
                }
            });
            buttons.Children.Add(toggle);
        }
        if (plugin.HasSettingsPage)
        {
            var settings = new Button { Content = "Settings…" };
            Avalonia.Automation.AutomationProperties.SetName(settings, $"{m.Name} settings");
            settings.Click += (_, _) => OpenSettings(plugin);
            buttons.Children.Add(settings);
        }
        if (!plugin.IsBuiltIn)
        {
            var uninstall = new Button { Content = "Uninstall" };
            Avalonia.Automation.AutomationProperties.SetName(uninstall, $"Uninstall {m.Name}");
            uninstall.Click += (_, _) => Act(() => _host.Uninstall(plugin.Id));
            buttons.Children.Add(uninstall);
        }
        body.Children.Add(buttons);

        return new Border
        {
            Child = body,
            Padding = new Thickness(10, 8),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)),
            MaxWidth = 500,
            HorizontalAlignment = HorizontalAlignment.Left,
            MinWidth = 420,
        };
    }

    private void Act(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or KeyNotFoundException)
        {
            _logger.LogWarning(ex, "Plugin action failed");
            _ = MessageWindow.ShowAsync(Owner(), "Plugins", "The action failed:", [ex.Message]);
        }
        Refresh();
    }

    private async Task InstallAsync()
    {
        var files = await Owner().StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Install plugin",
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("AudioTastic plugin") { Patterns = ["*" + PluginHost.PackageExtension] }],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
        {
            return;
        }
        try
        {
            var record = _host.Install(path);
            if (record.Status != PluginStatus.Enabled)
            {
                await MessageWindow.ShowAsync(Owner(), "Plugin installed", $"{record.Manifest.Name} was installed but could not be started:", [record.Error ?? record.Status.ToString()]);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            await MessageWindow.ShowAsync(Owner(), "Plugin not installed", "The plugin package is not valid:", [ex.Message]);
        }
        Refresh();
    }

    private void OpenSettings(PluginRecord plugin)
    {
        object? page = null;
        var provider = plugin.Instances.OfType<IPluginSettingsPage>().FirstOrDefault();
        if (provider is null || !_host.Guard(plugin, () => page = provider.CreateSettingsPage()))
        {
            Refresh();
            return;
        }
        var window = new Window
        {
            Title = $"{plugin.Manifest.Name} settings",
            Width = 520,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new ScrollViewer
            {
                Margin = new Thickness(16),
                Content = page as Control ?? new TextBlock { Text = "This plugin's settings page is not an Avalonia control.", TextWrapping = TextWrapping.Wrap },
            },
        };
        window.Show(Owner());
    }

    private Window Owner() => (Window)TopLevel.GetTopLevel(this)!;

    private static string TypeName(PluginType type) => type switch
    {
        PluginType.Dsp => "DSP",
        PluginType.UiPanel => "UI panel",
        PluginType.CastTarget => "cast target",
        _ => type.ToString().ToLowerInvariant(),
    };

    private static string StatusText(PluginStatus status) => status switch
    {
        PluginStatus.Enabled => "Enabled",
        PluginStatus.Disabled => "Disabled",
        PluginStatus.Failed => "Failed",
        _ => "Incompatible",
    };

    private static IBrush StatusBrush(PluginStatus status) => status switch
    {
        PluginStatus.Enabled => Brushes.MediumSeaGreen,
        PluginStatus.Disabled => Brushes.Gray,
        _ => Brushes.IndianRed,
    };
}
