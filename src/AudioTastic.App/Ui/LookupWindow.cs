using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AudioTastic.Metadata;

namespace AudioTastic.App.Ui;

/// <summary>MD-03: what the lookup window needs from the tag editor.</summary>
/// <param name="DiscogsToken">MD-03: the user's Discogs token when the Discogs fallback is on, else null.</param>
public sealed record LookupRequest(OnlineLookupService Service, string Title, string? Artist, TimeSpan? Length,
    IReadOnlyDictionary<TagField, string> Current, bool Tags, bool Covers, string? DiscogsToken = null);

/// <summary>MD-03: the fields and cover the user accepted.</summary>
public sealed record LookupChoice(IReadOnlyDictionary<TagField, string> Fields, TagPicture? Cover);

/// <summary>
/// MD-03: Tag editor › Look up online…. Searches MusicBrainz (title, artist, length), lists the matches, and shows a
/// match's values next to the current ones with a tick per field (ticked where they differ), plus its Cover Art
/// Archive cover. "Use" copies the ticked values into the editor; nothing is written until the editor's Save.
/// </summary>
#pragma warning disable CA1001 // The cancellation source is disposed when replaced and on Closed (windows are not IDisposable).
public sealed class LookupWindow : Window
#pragma warning restore CA1001
{
    private static readonly (TagField Field, string Label)[] Compared =
    [
        (TagField.Title, "Title"), (TagField.Artist, "Artist"), (TagField.AlbumArtist, "Album artist"), (TagField.Album, "Album"),
        (TagField.Year, "Year"), (TagField.Track, "Track"), (TagField.TrackCount, "Tracks"), (TagField.Disc, "Disc"),
    ];

    private readonly LookupRequest _request;
    private readonly TextBox _title;
    private readonly TextBox _artist;
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap };
    private readonly ListBox _matches = new() { Height = 160 };
    private readonly Grid _compare = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*,*"), ColumnSpacing = 10, RowSpacing = 4 };
    private readonly Image _coverImage = new() { Width = 120, Height = 120, Stretch = Stretch.Uniform };
    private readonly CheckBox _useCover = new() { Content = "Use this cover", IsEnabled = false };
    private readonly TextBlock _coverText = new() { Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap, Width = 160 };
    private readonly Button _use = new() { Content = "Use", IsDefault = true, MinWidth = 80, IsEnabled = false };
    private readonly Dictionary<TagField, (CheckBox Box, string Value)> _ticks = [];
    private IReadOnlyList<LookupCandidate> _candidates = [];
    private TagPicture? _cover;
    private CancellationTokenSource? _work;
    private LookupChoice? _result;

    private LookupWindow(LookupRequest request)
    {
        _request = request;
        Title = "Look up online (MusicBrainz)";
        Width = 720;
        Height = 620;
        MinWidth = 560;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        _title = new TextBox { Text = request.Title, Width = 240 };
        _artist = new TextBox { Text = request.Artist ?? string.Empty, Width = 200 };
        AutomationProperties.SetName(_title, "Search title");
        AutomationProperties.SetName(_artist, "Search artist");
        AutomationProperties.SetName(_matches, "Matches");
        var search = new Button { Content = "Search" };
        search.Click += (_, _) => _ = SearchAsync();
        _useCover.IsCheckedChanged += (_, _) => UpdateUse();
        _matches.SelectionChanged += (_, _) => _ = ShowAsync(_matches.SelectedIndex);

        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, _) => Close();
        _use.Click += (_, _) =>
        {
            _result = new LookupChoice(
                _ticks.Where(t => t.Value.Box.IsChecked == true).ToDictionary(t => t.Key, t => t.Value.Value),
                _useCover.IsChecked == true ? _cover : null);
            Close();
        };

        var top = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "Title", VerticalAlignment = VerticalAlignment.Center }, _title,
                        new TextBlock { Text = "Artist", VerticalAlignment = VerticalAlignment.Center }, _artist, search,
                    },
                },
                _status,
                _matches,
            },
        };
        var details = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16, Margin = new Thickness(0, 10, 0, 0) };
        var coverPanel = new StackPanel
        {
            Spacing = 6,
            IsVisible = request.Covers,
            Children = { new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _coverImage, HorizontalAlignment = HorizontalAlignment.Left }, _coverText, _useCover },
        };
        Grid.SetColumn(coverPanel, 1);
        details.Children.Add(new ScrollViewer { Content = _compare });
        details.Children.Add(coverPanel);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0), Children = { cancel, _use } };
        var footnote = new TextBlock
        {
            Text = "Data from MusicBrainz (CC0) and the Cover Art Archive. Nothing is written until you save in the tag editor.",
            FontSize = 11, Foreground = Brushes.Gray, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        DockPanel.SetDock(footnote, Dock.Bottom);
        Content = new DockPanel { Margin = new Thickness(16), Children = { top, buttons, footnote, details } };
        Opened += (_, _) => _ = SearchAsync();
        Closed += (_, _) =>
        {
            _work?.Cancel();
            _work?.Dispose();
            _work = null;
        };
    }

    /// <summary>Shows the window and returns the accepted values, or null.</summary>
    public static async Task<LookupChoice?> ShowAsync(Window owner, LookupRequest request)
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(request);
        var window = new LookupWindow(request);
        await window.ShowDialog(owner);
        return window._result is { } r && (r.Fields.Count > 0 || r.Cover is not null) ? r : null;
    }

    private CancellationToken Restart()
    {
        _work?.Cancel();
        _work?.Dispose();
        _work = new CancellationTokenSource();
        return _work.Token;
    }

    private async Task SearchAsync()
    {
        var title = _title.Text?.Trim();
        if (string.IsNullOrEmpty(title))
        {
            _status.Text = "Enter a title to search for.";
            return;
        }
        var token = Restart();
        _status.Text = "Searching MusicBrainz…";
        _matches.ItemsSource = null;
        Clear();
        try
        {
            _candidates = await _request.Service.SearchAsync(title, _artist.Text, _request.Length, token);
            if (token.IsCancellationRequested)
            {
                return;
            }
            if (_candidates.Count == 0 && _request.Length is not null)
            {
                // Lengths differ between releases: try once more without it.
                _candidates = await _request.Service.SearchAsync(title, _artist.Text, null, token);
            }
            _status.Text = _candidates.Count == 0 ? "No matches. Check the title and artist, or try without the artist." : $"{_candidates.Count} matches: pick one to compare.";
            _matches.ItemsSource = _candidates.Select(Describe).ToList();
            if (_candidates.Count > 0)
            {
                _matches.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException ex)
        {
            _status.Text = $"MusicBrainz could not be reached: {ex.Message}";
        }
        catch (System.Text.Json.JsonException)
        {
            _status.Text = "MusicBrainz sent an answer that could not be read.";
        }
    }

    private static string Describe(LookupCandidate c)
    {
        var parts = new List<string> { c.Artist is null ? c.Title : $"{c.Artist} – {c.Title}" };
        if (c.Album is not null)
        {
            parts.Add(c.Year is { } y ? $"{c.Album} ({y})" : c.Album);
        }
        if (c.Track is { } t)
        {
            parts.Add(c.TrackCount is { } n ? $"track {t}/{n}" : $"track {t}");
        }
        if (c.Length is { } l)
        {
            parts.Add(FileInfoReader.Time(l)[..^4]);
        }
        parts.Add($"score {c.Score}");
        return string.Join("  ·  ", parts);
    }

    private void Clear()
    {
        _compare.Children.Clear();
        _compare.RowDefinitions.Clear();
        _ticks.Clear();
        _coverImage.Source = null;
        _cover = null;
        _useCover.IsChecked = false;
        _useCover.IsEnabled = false;
        _coverText.Text = string.Empty;
        _use.IsEnabled = false;
    }

    private async Task ShowAsync(int index)
    {
        Clear();
        if (index < 0 || index >= _candidates.Count)
        {
            return;
        }
        var candidate = _candidates[index];
        var token = Restart();
        if (_request.Tags)
        {
            AddRow(0, null, "Field", "Current", "MusicBrainz", header: true);
            var row = 1;
            foreach (var (field, label) in Compared)
            {
                var current = _request.Current.GetValueOrDefault(field);
                var proposed = candidate.Fields.GetValueOrDefault(field);
                if (proposed is null && current is null)
                {
                    continue;
                }
                var tick = new CheckBox { IsChecked = proposed is not null && !string.Equals(proposed, current, StringComparison.Ordinal), IsEnabled = proposed is not null };
                AutomationProperties.SetName(tick, $"Use {label}");
                tick.IsCheckedChanged += (_, _) => UpdateUse();
                if (proposed is not null)
                {
                    _ticks[field] = (tick, proposed);
                }
                AddRow(row++, tick, label, current ?? "—", proposed ?? "—", header: false);
            }
        }
        UpdateUse();
        if (_request.Covers)
        {
            if (candidate.ReleaseId is null && (_request.DiscogsToken is null || candidate.Album is null))
            {
                _coverText.Text = "No release: no cover.";
                return;
            }
            _coverText.Text = "Loading cover…";
            try
            {
                var cover = candidate.ReleaseId is { } release ? await _request.Service.FrontCoverAsync(release, token) : null;
                var source = "Cover Art Archive";
                if (cover is null && _request.DiscogsToken is { } discogs && candidate.Album is { } album && !token.IsCancellationRequested)
                {
                    // MD-03: the Cover Art Archive has none; try Discogs (Settings › Metadata).
                    _coverText.Text = "No cover in the Cover Art Archive; asking Discogs…";
                    cover = await _request.Service.DiscogsCoverAsync(album, candidate.AlbumArtist ?? candidate.Artist, discogs, token);
                    source = "Discogs";
                }
                if (token.IsCancellationRequested)
                {
                    return;
                }
                _cover = cover;
                if (cover is null)
                {
                    _coverText.Text = _request.DiscogsToken is null
                        ? "The Cover Art Archive has no cover for this release."
                        : "Neither the Cover Art Archive nor Discogs has a cover for this release.";
                    return;
                }
                using var stream = new MemoryStream(cover.Data);
                _coverImage.Source = Bitmap.DecodeToWidth(stream, 240);
                _coverText.Text = $"From {source}: {cover.MimeType}, {cover.Data.Length / 1024.0:0} KB";
                _useCover.IsEnabled = true;
                _useCover.IsChecked = true;
                UpdateUse();
            }
            catch (OperationCanceledException)
            {
            }
#pragma warning disable CA1031 // A cover that cannot be fetched or decoded is simply not offered.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                _coverText.Text = $"No cover: {ex.Message}";
            }
        }
    }

    private void UpdateUse() => _use.IsEnabled = _ticks.Values.Any(t => t.Box.IsChecked == true) || _useCover.IsChecked == true;

    private void AddRow(int row, CheckBox? tick, string label, string current, string proposed, bool header)
    {
        _compare.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        // Only header cells get a colour: an explicit null Foreground would hide the theme's text colour.
        TextBlock Cell(string text, bool strong)
        {
            var cell = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            if (header)
            {
                cell.Foreground = Brushes.Gray;
            }
            else if (strong)
            {
                cell.FontWeight = FontWeight.SemiBold;
            }
            return cell;
        }
        Control[] cells = [tick ?? (Control)new TextBlock(), Cell(label, false), Cell(current, false), Cell(proposed, true)];
        for (var column = 0; column < cells.Length; column++)
        {
            Grid.SetRow(cells[column], row);
            Grid.SetColumn(cells[column], column);
            _compare.Children.Add(cells[column]);
        }
    }
}
