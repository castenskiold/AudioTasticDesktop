using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using AudioTastic.Metadata;

namespace AudioTastic.App.Ui;

/// <summary>
/// MD-01: Playlist context menu › Edit tags… (Ctrl+E). Edits one or several files; fields whose values differ between
/// the files show "(multiple values)" and are left alone unless typed in. Only changed fields are written.
/// <see cref="ShowAsync"/> returns the edit, or null when cancelled or nothing changed. With a single file, Look up
/// online… (MD-03) fills the fields from MusicBrainz and the cover from the Cover Art Archive, for review before Save.
/// </summary>
public sealed class TagEditorWindow : Window
{
    private static readonly (TagField Field, string Label)[] Rows =
    [
        (TagField.Title, "Title"),
        (TagField.Artist, "Artist"),
        (TagField.AlbumArtist, "Album artist"),
        (TagField.Album, "Album"),
        (TagField.Year, "Year"),
        (TagField.Genre, "Genre"),
        (TagField.Composer, "Composer"),
        (TagField.Comment, "Comment"),
    ];

    private readonly Dictionary<TagField, (TextBox Box, string? Initial)> _boxes = [];
    private readonly IReadOnlyList<EditableTags> _files;
    private CoverChange? _cover;
    private TagEdit? _result;
    private readonly Action<TagPicture?, string?> _showCover;
    private readonly TagLookup? _lookup;

    private TagEditorWindow(IReadOnlyList<EditableTags> files, string heading, string? note, TagLookup? lookup)
    {
        _files = files;
        _lookup = files.Count == 1 ? lookup : null;
        Title = files.Count == 1 ? "Edit tags" : $"Edit tags ({files.Count} files)";
        // Resizable; the lyrics field takes the extra height (Anders: it was too small).
        Width = 600;
        Height = 760;
        MinWidth = 480;
        MinHeight = 600;
        CanResize = true;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 10, RowSpacing = 6 };
        var row = 0;
        void AddRow(string label, Control control, bool grow = false)
        {
            grid.RowDefinitions.Add(new RowDefinition(grow ? GridLength.Star : GridLength.Auto));
            var text = new TextBlock { Text = label, VerticalAlignment = grow ? VerticalAlignment.Top : VerticalAlignment.Center, Margin = grow ? new Thickness(0, 6, 0, 0) : default };
            Grid.SetRow(text, row);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(text);
            grid.Children.Add(control);
            row++;
        }
        foreach (var (field, label) in Rows)
        {
            AddRow(label, Box(field, label));
        }
        // Track n of m, disc n of m on one row each.
        AddRow("Track", Pair(Box(TagField.Track, "Track number", 70), Box(TagField.TrackCount, "Track count", 70)));
        AddRow("Disc", Pair(Box(TagField.Disc, "Disc number", 70), Box(TagField.DiscCount, "Disc count", 70)));
        var lyrics = Box(TagField.Lyrics, "Lyrics");
        lyrics.AcceptsReturn = true;
        lyrics.TextWrapping = TextWrapping.Wrap;
        lyrics.MinHeight = 140;
        lyrics.VerticalAlignment = VerticalAlignment.Stretch;
        lyrics.VerticalContentAlignment = VerticalAlignment.Top;
        AddRow("Lyrics", lyrics, grow: true);

        // Cover: preview, choose, remove.
        var image = new Image { Width = 96, Height = 96, Stretch = Stretch.Uniform };
        var coverText = new TextBlock { FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        var choose = new Button { Content = "Choose image…" };
        var remove = new Button { Content = "Remove" };
        AutomationProperties.SetName(image, "Cover");
        void ShowCover(TagPicture? picture, string? text)
        {
            image.Source = Decode(picture);
            coverText.Text = text ?? (picture is null ? "No cover" : $"{picture.MimeType}, {picture.Data.Length / 1024.0:0.#} KB");
            remove.IsEnabled = picture is not null || text is not null;
        }
        _showCover = ShowCover;
        var covers = files.Select(f => f.Cover).ToList();
        var same = covers.Skip(1).All(c => SameCover(c, covers[0]));
        ShowCover(same ? covers[0] : null, same ? null : "(multiple covers)");
        choose.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose cover image",
                FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.jpg", "*.jpeg", "*.png"] }],
            });
            if (picked.Count == 1 && picked[0].TryGetLocalPath() is { } path)
            {
                var data = await File.ReadAllBytesAsync(path);
                var picture = new TagPicture(data, path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg");
                _cover = new CoverChange(picture);
                ShowCover(picture, null);
            }
        };
        remove.Click += (_, _) =>
        {
            _cover = CoverChange.Remove;
            ShowCover(null, null);
        };
        AddRow("Cover", new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = image }, new StackPanel { Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Children = { coverText, choose, remove } } },
        });

        var save = new Button { Content = "Save", IsDefault = true, MinWidth = 80 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        save.Click += (_, _) =>
        {
            _result = Collect();
            Close();
        };
        cancel.Click += (_, _) => Close();

        var top = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 0, 10) };
        var headingRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        headingRow.Children.Add(new TextBlock { Text = heading, TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (_lookup is not null)
        {
            var lookupButton = new Button { Content = "Look up online…" };
            ToolTip.SetTip(lookupButton, "Find tags on MusicBrainz and a cover on the Cover Art Archive");
            lookupButton.Click += (_, _) => _ = LookUpAsync();
            Grid.SetColumn(lookupButton, 1);
            headingRow.Children.Add(lookupButton);
            if (_lookup.StartAtOnce)
            {
                Opened += (_, _) => _ = LookUpAsync();
            }
        }
        top.Children.Add(headingRow);
        if (note is not null)
        {
            top.Children.Add(new TextBlock { Text = note, FontSize = 12, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap });
        }
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { cancel, save } };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        Content = new DockPanel { Margin = new Thickness(16), Children = { top, buttons, grid } };
        Opened += (_, _) => _boxes[TagField.Title].Box.Focus();
    }

    /// <summary>Shows the editor for the files' tags; returns the changes, or null.</summary>
    public static async Task<TagEdit?> ShowAsync(Window owner, IReadOnlyList<EditableTags> files, string heading, string? note, TagLookup? lookup = null)
    {
        ArgumentNullException.ThrowIfNull(owner);
        var window = new TagEditorWindow(files, heading, note, lookup);
        await window.ShowDialog(owner);
        return window._result is { IsEmpty: false } edit ? edit : null;
    }

    /// <summary>MD-03: searches with the title and artist typed (else the file name) and copies the accepted values in.</summary>
    private async Task LookUpAsync()
    {
        if (_lookup is null)
        {
            return;
        }
        var title = _boxes[TagField.Title].Box.Text?.Trim();
        var artist = _boxes[TagField.Artist].Box.Text?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            // "Artist - Title" file names are common.
            var name = Path.GetFileNameWithoutExtension(_lookup.FileName);
            var dash = name.IndexOf(" - ", StringComparison.Ordinal);
            (artist, title) = dash > 0 && string.IsNullOrEmpty(artist) ? (name[..dash].Trim(), name[(dash + 3)..].Trim()) : (artist, name);
        }
        var current = _boxes.Where(b => !string.IsNullOrEmpty(b.Value.Box.Text)).ToDictionary(b => b.Key, b => b.Value.Box.Text!);
        var choice = await LookupWindow.ShowAsync(this, new LookupRequest(_lookup.Service, title, string.IsNullOrEmpty(artist) ? null : artist, _lookup.Length,
            current, _lookup.Tags, _lookup.Covers, _lookup.DiscogsToken));
        if (choice is null)
        {
            return;
        }
        foreach (var (field, value) in choice.Fields)
        {
            if (_boxes.TryGetValue(field, out var box))
            {
                box.Box.Text = value;
            }
        }
        if (choice.Cover is { } cover)
        {
            _cover = new CoverChange(cover);
            _showCover(cover, null);
        }
    }

    private TextBox Box(TagField field, string name, double? width = null)
    {
        var (value, multiple) = EditableTags.Common(_files, field);
        var box = new TextBox { Text = value ?? string.Empty, PlaceholderText = multiple ? "(multiple values)" : null };
        if (width is { } w)
        {
            box.Width = w;
        }
        AutomationProperties.SetName(box, name);
        _boxes[field] = (box, value ?? string.Empty);
        return box;
    }

    private static StackPanel Pair(TextBox number, TextBox count) => new()
    {
        Orientation = Orientation.Horizontal,
        Spacing = 6,
        Children = { number, new TextBlock { Text = "of", VerticalAlignment = VerticalAlignment.Center }, count },
    };

    /// <summary>Only the fields the user changed (a "(multiple values)" field left empty stays untouched).</summary>
    private TagEdit Collect()
    {
        var fields = new Dictionary<TagField, string?>();
        foreach (var (field, (box, initial)) in _boxes)
        {
            var text = box.Text ?? string.Empty;
            if (!string.Equals(text, initial, StringComparison.Ordinal))
            {
                fields[field] = text;
            }
        }
        return new TagEdit(fields, _cover);
    }

    private static bool SameCover(TagPicture? a, TagPicture? b) =>
        a is null ? b is null : b is not null && a.Data.AsSpan().SequenceEqual(b.Data);

    private static Bitmap? Decode(TagPicture? picture)
    {
        if (picture is null)
        {
            return null;
        }
        try
        {
            using var stream = new MemoryStream(picture.Data);
            return Bitmap.DecodeToWidth(stream, 192);
        }
#pragma warning disable CA1031 // A broken embedded picture is shown as no picture.
        catch (Exception)
#pragma warning restore CA1031
        {
            return null;
        }
    }
}

/// <summary>MD-03: online lookup for the tag editor (single file): the service, the track's length and file name, the
/// services switched on, and whether to start searching when the editor opens (playlist menu › Look up online…).</summary>
public sealed record TagLookup(OnlineLookupService Service, TimeSpan? Length, string FileName, bool Tags, bool Covers, bool StartAtOnce,
    string? DiscogsToken = null);
