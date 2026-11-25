using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using AudioTastic.Data.Backup;
using AudioTastic.Data.Settings;

namespace AudioTastic.App.Ui;

/// <summary>
/// TL-02: Settings › General › Import settings…: what the backup holds, with a check box per settings group,
/// playlists, equalizer presets and skins. Returns the choice, or null when cancelled.
/// </summary>
public sealed class ImportBackupWindow : Window
{
    private readonly List<(CheckBox Box, SettingsGroup Group)> _groups = [];
    private readonly CheckBox? _playlists;
    private readonly CheckBox? _presets;
    private readonly CheckBox? _skins;
    private readonly Button _import = new() { Content = "Import and restart", IsDefault = true };
    private BackupSelection? _result;

    private ImportBackupWindow(BackupInfo info)
    {
        Title = "Import settings";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8, MaxWidth = 460 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Backup from {info.Created.LocalDateTime:g} (AudioTastic {info.AppVersion}). Choose what to bring in:",
            TextWrapping = TextWrapping.Wrap,
        });
        if (info.SettingsGroups.Count > 0)
        {
            panel.Children.Add(new TextBlock { Text = "Settings", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
            var grid = new WrapPanel { Orientation = Orientation.Horizontal };
            foreach (var (group, title) in SettingsWindow.Groups.Where(g => info.SettingsGroups.Contains(g.Group)))
            {
                var box = new CheckBox { Content = title, IsChecked = true, Width = 200 };
                box.IsCheckedChanged += (_, _) => Update();
                _groups.Add((box, group));
                grid.Children.Add(box);
            }
            panel.Children.Add(grid);
            panel.Children.Add(Hint("Equalizer & DSP includes the DSP chain and VST3 effects; System integration includes the hotkeys; Interface includes window positions."));
        }
        panel.Children.Add(new TextBlock { Text = "Other", FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
        CheckBox? Option(string text, bool present, string hint)
        {
            if (!present)
            {
                return null;
            }
            var box = new CheckBox { Content = text, IsChecked = true };
            AutomationProperties.SetHelpText(box, hint);
            box.IsCheckedChanged += (_, _) => Update();
            panel.Children.Add(box);
            panel.Children.Add(Hint(hint));
            return box;
        }
        _playlists = Option(info.Playlists == 1 ? "1 playlist" : $"{info.Playlists} playlists", info.Playlists > 0, "Replaces all your current playlist tabs.");
        _presets = Option("Equalizer presets", info.HasPresets, "Replaces your own equalizer presets.");
        _skins = Option(info.Skins.Count == 1 ? "1 skin" : $"{info.Skins.Count} skins", info.Skins.Count > 0, "Adds them to your installed skins: " + string.Join(", ", info.Skins) + ".");
        panel.Children.Add(Hint("AudioTastic restarts to import. What the import replaces is saved first to the backups folder in the data folder."));
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        cancel.Click += (_, _) => Close();
        _import.Click += (_, _) =>
        {
            _result = Selection();
            Close();
        };
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0), Children = { _import, cancel } });
        Content = panel;
        Update();
    }

    private static TextBlock Hint(string text) => new() { Text = text, Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap };

    private BackupSelection Selection() => new(
        [.. _groups.Where(g => g.Box.IsChecked == true).Select(g => g.Group)],
        _playlists?.IsChecked == true,
        _presets?.IsChecked == true,
        _skins?.IsChecked == true);

    private void Update() => _import.IsEnabled = !Selection().IsEmpty;

    public static async Task<BackupSelection?> AskAsync(Window owner, BackupInfo info)
    {
        var window = new ImportBackupWindow(info);
        await window.ShowDialog(owner);
        return window._result;
    }
}
