using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using AudioTastic.Metadata;

namespace AudioTastic.App.Ui;

/// <summary>
/// MD-05: Playlist context menu › File info… (Alt+3), or a double-click on the format badges (MW5): the file's
/// technical details, loudness and tempo, and all its tags, read-only. Values can be selected; Copy puts everything
/// on the clipboard as text.
/// </summary>
public sealed class FileInfoWindow : Window
{
    public FileInfoWindow(string heading, IReadOnlyList<(string Title, IReadOnlyList<InfoRow> Rows)> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        Title = "File info";
        Width = 620;
        Height = 640;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var body = new StackPanel { Spacing = 14 };
        foreach (var (title, rows) in sections.Where(s => s.Rows.Count > 0))
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), RowSpacing = 3, ColumnSpacing = 12 };
            for (var i = 0; i < rows.Count; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var label = new TextBlock { Text = rows[i].Label, Foreground = Brushes.Gray, TextTrimming = TextTrimming.CharacterEllipsis };
                var value = new SelectableTextBlock { Text = rows[i].Value, TextWrapping = TextWrapping.Wrap };
                Avalonia.Automation.AutomationProperties.SetName(value, rows[i].Label);
                Grid.SetRow(label, i);
                Grid.SetRow(value, i);
                Grid.SetColumn(value, 1);
                grid.Children.Add(label);
                grid.Children.Add(value);
            }
            body.Children.Add(new StackPanel
            {
                Spacing = 6,
                Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, grid },
            });
        }

        var copy = new Button { Content = "Copy", MinWidth = 80 };
        copy.Click += async (_, _) =>
        {
            if (Clipboard is { } clipboard)
            {
                await clipboard.SetTextAsync(AsText(heading, sections));
            }
        };
        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 80 };
        close.Click += (_, _) => Close();
        var top = new SelectableTextBlock
        {
            Inlines = [new Run(heading) { FontWeight = FontWeight.SemiBold }],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10),
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { copy, close } };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        Content = new DockPanel
        {
            Margin = new Thickness(16),
            Children = { top, buttons, new ScrollViewer { Content = body } },
        };
    }

    /// <summary>The dialog's content as plain text (Copy).</summary>
    public static string AsText(string heading, IReadOnlyList<(string Title, IReadOnlyList<InfoRow> Rows)> sections)
    {
        ArgumentNullException.ThrowIfNull(sections);
        var text = new StringBuilder().AppendLine(heading);
        foreach (var (title, rows) in sections.Where(s => s.Rows.Count > 0))
        {
            text.AppendLine().AppendLine(title);
            foreach (var row in rows)
            {
                text.Append("  ").Append(row.Label).Append(": ").AppendLine(row.Value);
            }
        }
        return text.ToString();
    }
}
