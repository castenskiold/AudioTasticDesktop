using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AudioTastic.Core.Audio;
using AudioTastic.Core.Playback;
using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App;

/// <summary>
/// P1 temporary test window: open files, transport, seek, volume, device choice and engine status, until the
/// skinned main window takes over in P2. Winamp keys: Z/X/C/V/B transport, L open, Up/Down volume.
/// Files can also be dropped on the window or passed on the command line.
/// </summary>
public sealed partial class AudioDebugWindow : Window
{
    private static readonly string[] OpenPatterns = ["*.wav", "*.flac", "*.mp3", "*.aiff", "*.aif", "*.aifc", "*.ape", "*.aac", "*.m4a", "*.wv"];

    private readonly PlaybackEngine? _engine;
    private readonly IAudioOutput? _output;
    private readonly SettingsStore? _settings;
    private readonly ILogger<AudioDebugWindow>? _logger;
    private readonly DispatcherTimer _seekDebounce = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private bool _updating;
    private bool _showRemaining;
    private double? _pendingSeekSeconds;

    /// <summary>Used by the XAML previewer only.</summary>
    public AudioDebugWindow()
    {
        InitializeComponent();
    }

    public AudioDebugWindow(PlaybackEngine engine, IAudioOutput output, SettingsStore settings, ILogger<AudioDebugWindow> logger)
    {
        _engine = engine;
        _output = output;
        _settings = settings;
        _logger = logger;
        InitializeComponent();

        _updating = true;
        VolumeSlider.Value = engine.Volume;
        MuteBox.IsChecked = engine.IsMuted;
        VolumeText.Text = $"{engine.Volume}";
        _updating = false;

        VolumeSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_updating)
            {
                engine.Volume = (int)Math.Round(VolumeSlider.Value);
                VolumeText.Text = $"{engine.Volume}";
            }
        };
        MuteBox.IsCheckedChanged += (_, _) =>
        {
            if (!_updating)
            {
                engine.IsMuted = MuteBox.IsChecked == true;
            }
        };
        SeekSlider.PropertyChanged += (_, e) =>
        {
            if (e.Property == RangeBase.ValueProperty && !_updating)
            {
                _pendingSeekSeconds = SeekSlider.Value;
                _seekDebounce.Stop();
                _seekDebounce.Start();
            }
        };
        _seekDebounce.Tick += (_, _) =>
        {
            _seekDebounce.Stop();
            if (_pendingSeekSeconds is { } seconds)
            {
                _pendingSeekSeconds = null;
                Run(() => engine.SeekAsync(TimeSpan.FromSeconds(seconds)));
            }
        };
        DeviceBox.SelectionChanged += (_, _) =>
        {
            if (!_updating && DeviceBox.SelectedItem is DeviceChoice choice)
            {
                Run(() => engine.SetOutputDeviceAsync(choice.Name));
            }
        };

        engine.StateChanged += (_, _) => Dispatcher.UIThread.Post(UpdateStatus);
        engine.TrackChanged += (_, _) => Dispatcher.UIThread.Post(UpdateTrack);
        engine.PositionChanged += (_, e) => Dispatcher.UIThread.Post(() => UpdatePosition(e.Position, e.Duration));
        engine.PlaybackError += (_, e) => Dispatcher.UIThread.Post(() =>
            StatusText.Text = $"Cannot play {System.IO.Path.GetFileName(e.Path)}: {e.Message}");
        output.DevicesChanged += (_, _) => Dispatcher.UIThread.Post(LoadDevices);

        AddHandler(DragDrop.DropEvent, OnDrop);
        KeyDown += OnKeyDown;
        LoadDevices();
        UpdateTrack();
        UpdateStatus();
    }

    /// <summary>Opens and plays <paramref name="paths"/> (command-line files, drag and drop).</summary>
    public void OpenFiles(IReadOnlyList<string> paths)
    {
        if (_engine is null || paths.Count == 0)
        {
            return;
        }
        Run(() => _engine.OpenAsync(paths));
    }

    private async void Open_OnClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open audio files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Audio files (FM-01)") { Patterns = OpenPatterns },
                FilePickerFileTypes.All,
            ],
        });
        OpenFiles([.. files.Select(f => f.TryGetLocalPath()).OfType<string>()]);
    }

    private void Previous_OnClick(object? sender, RoutedEventArgs e) => Run(() => _engine!.PreviousAsync());
    private void Play_OnClick(object? sender, RoutedEventArgs e) => Run(() => _engine!.PlayAsync());
    private void Pause_OnClick(object? sender, RoutedEventArgs e) => Run(() => _engine!.PauseAsync());
    private void Stop_OnClick(object? sender, RoutedEventArgs e) => Run(() => _engine!.StopAsync());
    private void Next_OnClick(object? sender, RoutedEventArgs e) => Run(() => _engine!.NextAsync());

    private void Time_OnClick(object? sender, RoutedEventArgs e)
    {
        _showRemaining = !_showRemaining;
        if (_engine is not null)
        {
            UpdatePosition(_engine.Position, _engine.Duration);
        }
    }

    private void TrackList_OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (TrackList.SelectedIndex >= 0)
        {
            var index = TrackList.SelectedIndex;
            Run(() => _engine!.PlayIndexAsync(index));
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        var files = e.DataTransfer.TryGetFiles();
        if (files is not null)
        {
            OpenFiles([.. files.Select(f => f.TryGetLocalPath()).OfType<string>()]);
        }
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_engine is null || e.KeyModifiers != KeyModifiers.None || e.Source is ComboBox)
        {
            return;
        }
        switch (e.Key)
        {
            case Key.Z: Run(_engine.PreviousAsync); break;
            case Key.X: Run(_engine.PlayAsync); break;
            case Key.C: Run(_engine.PauseAsync); break;
            case Key.V: Run(_engine.StopAsync); break;
            case Key.B: Run(_engine.NextAsync); break;
            case Key.L: Open_OnClick(this, new RoutedEventArgs()); break;
            case Key.Up: VolumeSlider.Value = Math.Min(100, VolumeSlider.Value + 5); break;
            case Key.Down: VolumeSlider.Value = Math.Max(0, VolumeSlider.Value - 5); break;
            default: return;
        }
        e.Handled = true;
    }

    private async void Run(Func<Task> command)
    {
        try
        {
            await command();
        }
#pragma warning disable CA1031 // UI command: show the error instead of crashing.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger?.LogError(ex, "Command failed");
            StatusText.Text = ex.Message;
        }
    }

    private void LoadDevices()
    {
        if (_engine is null)
        {
            return;
        }
        _updating = true;
        try
        {
            var choices = new List<DeviceChoice> { new(null, "System default") };
            choices.AddRange(_engine.GetOutputDevices().Select(d => new DeviceChoice(d.Name, d.IsDefault ? $"{d.Name} (default)" : d.Name)));
            DeviceBox.ItemsSource = choices;
            var saved = _settings?.Current.Output.DeviceName;
            DeviceBox.SelectedItem = choices.FirstOrDefault(c => c.Name == saved) ?? choices[0];
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateTrack()
    {
        if (_engine is null)
        {
            return;
        }
        TrackList.ItemsSource = _engine.Tracks.Select(System.IO.Path.GetFileName).ToArray();
        TrackList.SelectedIndex = _engine.CurrentIndex;
        if (_engine.CurrentTrack is { } track)
        {
            TitleText.Text = track.Artist is null ? track.Title : $"{track.Artist} – {track.Title}";
            var s = track.Stream;
            var depth = s.BitsPerSample > 0 ? $"{s.BitsPerSample}-bit / " : string.Empty;
            var rate = s.Bitrate > 0 ? $" · {s.Bitrate / 1000} kbps" : string.Empty;
            FormatText.Text = $"{s.Codec.ToUpperInvariant()} · {depth}{s.SampleRate / 1000.0:0.#} kHz · {(s.Channels == 1 ? "mono" : s.Channels == 2 ? "stereo" : $"{s.Channels} ch")}{rate}";
        }
        else
        {
            TitleText.Text = _engine.Tracks.Count == 0 ? "Nothing loaded" : System.IO.Path.GetFileName(_engine.Tracks[Math.Max(0, _engine.CurrentIndex)]);
            FormatText.Text = " ";
        }
        UpdatePosition(_engine.Position, _engine.Duration);
    }

    private void UpdatePosition(TimeSpan position, TimeSpan? duration)
    {
        _updating = true;
        try
        {
            var length = duration?.TotalSeconds ?? 0;
            SeekSlider.IsEnabled = length > 0 && _engine?.State is PlaybackState.Playing or PlaybackState.Paused;
            SeekSlider.Maximum = Math.Max(1, length);
            if (_pendingSeekSeconds is null)
            {
                SeekSlider.Value = Math.Min(position.TotalSeconds, SeekSlider.Maximum);
            }
            var shown = _showRemaining && duration is { } d ? "-" + Format(d - position) : Format(position);
            TimeButton.Content = duration is { } total ? $"{shown} / {Format(total)}" : shown;
        }
        finally
        {
            _updating = false;
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        if (_engine is null || _output is null)
        {
            return;
        }
        var device = _engine.OutputDeviceName ?? "(closed)";
        var format = _output.Format is { } f ? $"{f.SampleRate} Hz" : "-";
        StatusText.Text = $"{_engine.State} · {_output.BackendName}: {device} · {format} · " +
                          $"buffer {_settings?.Current.Output.BufferMilliseconds} ms · underruns {_engine.Underruns}";
    }

    private static string Format(TimeSpan t)
    {
        if (t < TimeSpan.Zero)
        {
            t = TimeSpan.Zero;
        }
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", null) : $"{(int)t.TotalMinutes}:{t.Seconds:00}";
    }

    private sealed record DeviceChoice(string? Name, string Label)
    {
        public override string ToString() => Label;
    }
}
