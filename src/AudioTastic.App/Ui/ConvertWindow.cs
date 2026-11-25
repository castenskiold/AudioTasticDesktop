using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AudioTastic.Data.Settings;
using AudioTastic.Metadata.Conversion;
using AudioTastic.Sdk.Audio;

namespace AudioTastic.App.Ui;

/// <summary>
/// TL-01: playlist row menu › Convert… (and FM-11 › Rip…): format and quality, destination, file name pattern,
/// what to do with existing files, then a progress list. The choices are kept (Settings › General › Conversion).
/// The window stays open while converting; closing it cancels the batch.
/// </summary>
public sealed class ConvertWindow : Window
{
    private static readonly int[] Mp3Bitrates = [128, 160, 192, 224, 256, 320];
    private static readonly int[] AacBitrates = [96, 128, 160, 192, 256, 320];
    private static readonly int[] OpusBitrates = [64, 96, 128, 160, 192, 256];
    private static readonly int[] VorbisBitrates = [96, 128, 160, 192, 256, 320];

    private readonly ConversionService _service;
    private readonly SettingsStore _settings;
    private readonly IReadOnlyList<ConversionItem> _items;
    private readonly ConversionSettings _choices;
    private readonly IReadOnlyList<EncodeFormat> _formats;
    private readonly StackPanel _setup = new() { Spacing = 10 };
    private readonly StackPanel _quality = new() { Spacing = 6 };
    private readonly TextBox _folder = new() { Width = 320 };
    private readonly TextBox _pattern = new() { Width = 320 };
    private readonly TextBlock _example = new() { Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
    private readonly Button _start = new() { Content = "Start", IsDefault = true, MinWidth = 90 };
    private readonly Button _cancel = new() { Content = "Cancel", IsCancel = true, MinWidth = 90 };
    private readonly StackPanel _progress = new() { Spacing = 4 };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private ConversionBatch? _batch;

    private ConvertWindow(ConversionService service, SettingsStore settings, IReadOnlyList<ConversionItem> items, string title)
    {
        _service = service;
        _settings = settings;
        _items = items;
        _choices = settings.Current.General.Conversion;
        _formats = [.. service.Formats];
        Title = title;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        BuildSetup();
        _start.Click += (_, _) => StartBatch();
        _cancel.Click += (_, _) =>
        {
            if (_batch is not null)
            {
                _batch.Cancel(); // Stop: the list stays to show what was done
                _cancel.IsEnabled = false;
            }
            else
            {
                Close();
            }
        };
        _timer.Tick += (_, _) => Refresh();
        Closing += (_, _) =>
        {
            _timer.Stop();
            _batch?.Cancel(); // partial files are removed
        };
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = items.Count == 1 ? $"1 track" : $"{items.Count} tracks", FontWeight = FontWeight.SemiBold },
                _setup,
                new ScrollViewer { MaxHeight = 320, Content = _progress },
                _summary,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { _start, _cancel } },
            },
        };
    }

    /// <summary>Opens the dialog for <paramref name="items"/> (modeless, owned by <paramref name="owner"/>).</summary>
    public static void Show(Window owner, ConversionService service, SettingsStore settings, IReadOnlyList<ConversionItem> items, string title = "Convert")
    {
        ArgumentNullException.ThrowIfNull(owner);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count > 0)
        {
            new ConvertWindow(service, settings, items, title).Show(owner);
        }
    }

    private static Grid Row(string label, Control control, string? help = null)
    {
        AutomationProperties.SetName(control, label);
        if (help is not null)
        {
            AutomationProperties.SetHelpText(control, help);
            ToolTip.SetTip(control, help);
        }
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*") };
        grid.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(control, 1);
        grid.Children.Add(control);
        return grid;
    }

    private void BuildSetup()
    {
        if (_formats.Count == 0)
        {
            _setup.Children.Add(new TextBlock { Text = "No encoder is installed (the FFmpeg plugin is disabled or missing).", TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
            _start.IsEnabled = false;
            return;
        }
        var format = new ComboBox { ItemsSource = _formats.Select(Describe).ToList(), MinWidth = 220 };
        var current = Enum.TryParse<EncodeFormat>(_choices.Format, true, out var f) && _formats.Contains(f) ? f : _formats[0];
        format.SelectedIndex = _formats.ToList().IndexOf(current);
        format.SelectionChanged += (_, _) =>
        {
            _choices.Format = _formats[Math.Max(0, format.SelectedIndex)].ToString();
            BuildQuality();
            UpdateExample();
        };
        _setup.Children.Add(Row("Format", format));
        _setup.Children.Add(_quality);
        BuildQuality();

        var sameFolder = new CheckBox { Content = "Next to the originals", IsChecked = _choices.SameFolder };
        _folder.Text = _choices.Destination ?? Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        _folder.IsEnabled = !_choices.SameFolder;
        var browse = new Button { Content = "Browse…", IsEnabled = !_choices.SameFolder };
        AutomationProperties.SetName(browse, "Choose the output folder");
        browse.Click += async (_, _) =>
        {
            var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Output folder" });
            if (picked.Count > 0 && picked[0].TryGetLocalPath() is { } path)
            {
                _folder.Text = path;
            }
        };
        sameFolder.IsCheckedChanged += (_, _) =>
        {
            _choices.SameFolder = sameFolder.IsChecked == true;
            _folder.IsEnabled = browse.IsEnabled = !_choices.SameFolder;
            UpdateExample();
        };
        _folder.TextChanged += (_, _) => UpdateExample();
        _setup.Children.Add(Row("Output folder", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { _folder, browse } }));
        _setup.Children.Add(Row(string.Empty, sameFolder));

        _pattern.Text = _choices.Pattern;
        _pattern.TextChanged += (_, _) => UpdateExample();
        _setup.Children.Add(Row("File names", _pattern, "Fields: " + string.Join(" ", ConversionNaming.Fields) + ". Use / for subfolders."));
        _setup.Children.Add(Row(string.Empty, _example));

        var exists = new ComboBox { ItemsSource = new[] { "Skip the track", "Replace the file", "Add a number" }, SelectedIndex = (int)_choices.IfExists };
        exists.SelectionChanged += (_, _) => _choices.IfExists = (ExistingFileAction)Math.Max(0, exists.SelectedIndex);
        _setup.Children.Add(Row("If the file exists", exists));

        var parallel = new ComboBox { ItemsSource = Enumerable.Range(0, 9).Select(n => n == 0 ? "Automatic" : n.ToString(System.Globalization.CultureInfo.CurrentCulture)).ToList(), SelectedIndex = Math.Clamp(_choices.ParallelJobs, 0, 8) };
        parallel.SelectionChanged += (_, _) => _choices.ParallelJobs = Math.Max(0, parallel.SelectedIndex);
        _setup.Children.Add(Row("Files at once", parallel, "How many files are converted at the same time. Automatic uses half the processor cores (1–4)."));

        var art = new CheckBox { Content = "Include the cover", IsChecked = _choices.CopyArt };
        art.IsCheckedChanged += (_, _) => _choices.CopyArt = art.IsChecked == true;
        _setup.Children.Add(Row(string.Empty, art));
        UpdateExample();
    }

    private static string Describe(EncodeFormat format) => format switch
    {
        EncodeFormat.Flac => "FLAC (lossless)",
        EncodeFormat.Alac => "Apple Lossless (ALAC, .m4a)",
        EncodeFormat.Wav => "WAV (uncompressed)",
        EncodeFormat.Mp3 => "MP3",
        EncodeFormat.Aac => "AAC (.m4a)",
        EncodeFormat.Opus => "Opus",
        EncodeFormat.Vorbis => "Ogg Vorbis",
        _ => format.ToString(),
    };

    private EncodeFormat Format => Enum.TryParse<EncodeFormat>(_choices.Format, true, out var f) ? f : EncodeFormat.Flac;

    /// <summary>The quality controls for the chosen format.</summary>
    private void BuildQuality()
    {
        _quality.Children.Clear();
        switch (Format)
        {
            case EncodeFormat.Flac or EncodeFormat.Alac or EncodeFormat.Wav:
                var bits = new ComboBox { ItemsSource = new[] { "Like the original", "16 bit", "24 bit" }, SelectedIndex = _choices.BitsPerSample switch { 16 => 1, 24 => 2, _ => 0 } };
                bits.SelectionChanged += (_, _) => _choices.BitsPerSample = bits.SelectedIndex switch { 1 => 16, 2 => 24, _ => 0 };
                _quality.Children.Add(Row("Bit depth", bits, "Like the original keeps 24-bit files at 24 bit and writes everything else as 16 bit."));
                break;
            case EncodeFormat.Mp3:
                var vbr = new CheckBox { Content = "Variable bit rate (VBR)", IsChecked = _choices.Mp3Vbr };
                var level = new ComboBox
                {
                    ItemsSource = Enumerable.Range(0, 10).Select(v => v switch { 0 => "V0 (best, ~245 kbps)", 2 => "V2 (~190 kbps)", 5 => "V5 (~130 kbps)", 9 => "V9 (smallest)", _ => $"V{v}" }).ToList(),
                    SelectedIndex = Math.Clamp(_choices.Mp3VbrQuality, 0, 9),
                };
                level.SelectionChanged += (_, _) => _choices.Mp3VbrQuality = Math.Max(0, level.SelectedIndex);
                var rate = Bitrates(Mp3Bitrates, _choices.Mp3BitrateKbps, v => _choices.Mp3BitrateKbps = v);
                void Show()
                {
                    level.IsVisible = _choices.Mp3Vbr;
                    rate.IsVisible = !_choices.Mp3Vbr;
                }
                vbr.IsCheckedChanged += (_, _) =>
                {
                    _choices.Mp3Vbr = vbr.IsChecked == true;
                    Show();
                };
                _quality.Children.Add(Row(string.Empty, vbr));
                _quality.Children.Add(Row("Bit rate", rate));
                _quality.Children.Add(Row("VBR quality", level));
                Show();
                break;
            case EncodeFormat.Aac:
                _quality.Children.Add(Row("Bit rate", Bitrates(AacBitrates, _choices.AacBitrateKbps, v => _choices.AacBitrateKbps = v)));
                break;
            case EncodeFormat.Opus:
                _quality.Children.Add(Row("Bit rate", Bitrates(OpusBitrates, _choices.OpusBitrateKbps, v => _choices.OpusBitrateKbps = v), "Opus is written at 48 kHz."));
                break;
            case EncodeFormat.Vorbis:
                _quality.Children.Add(Row("Bit rate", Bitrates(VorbisBitrates, _choices.VorbisBitrateKbps, v => _choices.VorbisBitrateKbps = v), "An average: Vorbis varies the bit rate with the music."));
                break;
        }
    }

    private static ComboBox Bitrates(int[] values, int current, Action<int> set)
    {
        var box = new ComboBox { ItemsSource = values.Select(v => $"{v} kbps").ToList() };
        var index = Array.IndexOf(values, current);
        box.SelectedIndex = index >= 0 ? index : values.Length - 2;
        box.SelectionChanged += (_, _) => set(values[Math.Max(0, box.SelectedIndex)]);
        return box;
    }

    private void UpdateExample()
    {
        var first = _items[0];
        var name = ConversionNaming.Render(_pattern.Text ?? string.Empty, first.Tags ?? _service.TagsFor(first), first.Path) + ConversionNaming.ExtensionOf(Format);
        var folder = _choices.SameFolder ? Path.GetDirectoryName(first.Path) : _folder.Text;
        _example.Text = "Example: " + Path.Combine(folder ?? string.Empty, name);
    }

    private void StartBatch()
    {
        _choices.Pattern = string.IsNullOrWhiteSpace(_pattern.Text) ? ConversionNaming.DefaultPattern : _pattern.Text.Trim();
        _choices.Destination = string.IsNullOrWhiteSpace(_folder.Text) ? null : _folder.Text.Trim();
        var choices = _choices;
        _settings.Update(SettingsGroup.General, s => s.General.Conversion = choices);
        _setup.IsVisible = false;
        _start.IsVisible = false;
        _cancel.Content = "Stop";
        foreach (var item in _items)
        {
            var bar = new ProgressBar { Width = 140, Minimum = 0, Maximum = 1, Height = 6, VerticalAlignment = VerticalAlignment.Center };
            var state = new TextBlock { Width = 260, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            var name = item.Tags?.Title ?? Path.GetFileName(item.Path);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { new TextBlock { Text = name, Width = 200, TextTrimming = TextTrimming.CharacterEllipsis }, bar, state } };
            AutomationProperties.SetName(row, name);
            _progress.Children.Add(row);
        }
        _batch = _service.Start(_items, ConversionOptions.From(choices));
        _ = _batch.Completion.ContinueWith(_ => Dispatcher.UIThread.Post(Finished), TaskScheduler.Default);
        _timer.Start();
    }

    private void Refresh()
    {
        if (_batch is null)
        {
            return;
        }
        for (var i = 0; i < _batch.Jobs.Count; i++)
        {
            var job = _batch.Jobs[i];
            var row = (StackPanel)_progress.Children[i];
            ((ProgressBar)row.Children[1]).Value = job.State == ConversionState.Done ? 1 : job.Progress;
            var text = job.State switch
            {
                ConversionState.Waiting => "Waiting",
                ConversionState.Running => "Converting…",
                ConversionState.Done => "Done",
                ConversionState.Skipped => "Skipped: the file exists",
                ConversionState.Cancelled => "Stopped",
                _ => "Failed: " + job.Message,
            };
            ((TextBlock)row.Children[2]).Text = text;
            AutomationProperties.SetHelpText(row, text);
        }
    }

    private void Finished()
    {
        _timer.Stop();
        Refresh();
        if (_batch is null)
        {
            return;
        }
        var jobs = _batch.Jobs;
        int Count(ConversionState s) => jobs.Count(j => j.State == s);
        var parts = new List<string> { $"{Count(ConversionState.Done)} converted" };
        if (Count(ConversionState.Skipped) > 0)
        {
            parts.Add($"{Count(ConversionState.Skipped)} skipped");
        }
        if (Count(ConversionState.Failed) > 0)
        {
            parts.Add($"{Count(ConversionState.Failed)} failed");
        }
        if (Count(ConversionState.Cancelled) > 0)
        {
            parts.Add($"{Count(ConversionState.Cancelled)} stopped");
        }
        _summary.Text = string.Join(", ", parts) + ".";
        _cancel.Content = "Close";
        _cancel.IsEnabled = true;
        _batch.Dispose();
        _batch = null;
    }
}
