using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Data.Settings;

namespace AudioTastic.App.Ui.Equalizer;

/// <summary>EQ-04: the parametric band table (type, frequency, gain, Q, on/off per band; add and delete).</summary>
public sealed class ParametricBandsWindow : Window
{
    private readonly EqualizerController _controller;
    private readonly StackPanel _rows = new() { Spacing = 4 };

    public ParametricBandsWindow(EqualizerController controller)
    {
        _controller = controller;
        Title = "Parametric bands";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var add = new Button { Content = "Add band" };
        add.Click += (_, _) =>
        {
            if (_controller.Current.Parametric.Count < EqualizerDesign.MaxBands)
            {
                _controller.Change(eq => eq.Parametric = [.. eq.Parametric, new ParametricBand(FilterType.Peak, 1000, 0, 1)]);
                Rebuild();
            }
        };
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true, MinWidth = 80 };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Frequency 20 Hz–20 kHz, gain ±24 dB (peak and shelves), Q 0.1–10. Up to 10 bands.", Foreground = Brushes.Gray, FontSize = 12 },
                _rows,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { add, close } },
            },
        };
        Rebuild();
    }

    private void Rebuild()
    {
        _rows.Children.Clear();
        var bands = _controller.Current.Parametric;
        for (var i = 0; i < bands.Count; i++)
        {
            var index = i;
            var band = bands[i];
            var on = new CheckBox { IsChecked = band.Enabled, VerticalAlignment = VerticalAlignment.Center };
            var type = new ComboBox { ItemsSource = Enum.GetValues<FilterType>().Select(ParametricEditor.TypeName).ToList(), SelectedIndex = (int)band.Type, Width = 110 };
            var frequency = Number(band.Frequency, 20, 20000, 10, "0");
            var gain = Number(band.GainDb, -24, 24, 0.5m, "+0.0;-0.0;0.0");
            gain.IsEnabled = ParametricEditor.HasGain(band.Type);
            var q = Number(band.Q, 0.1, 10, 0.1m, "0.00");
            var delete = new Button { Content = "✕", Padding = new Thickness(8, 2) };
            Avalonia.Automation.AutomationProperties.SetName(on, $"Band {i + 1} on");
            Avalonia.Automation.AutomationProperties.SetName(type, $"Band {i + 1} type");
            Avalonia.Automation.AutomationProperties.SetName(frequency, $"Band {i + 1} frequency (Hz)");
            Avalonia.Automation.AutomationProperties.SetName(gain, $"Band {i + 1} gain (dB)");
            Avalonia.Automation.AutomationProperties.SetName(q, $"Band {i + 1} Q");
            Avalonia.Automation.AutomationProperties.SetName(delete, $"Delete band {i + 1}");
            on.IsCheckedChanged += (_, _) => Update(index, b => b with { Enabled = on.IsChecked == true });
            type.SelectionChanged += (_, _) =>
            {
                var t = (FilterType)Math.Max(0, type.SelectedIndex);
                gain.IsEnabled = ParametricEditor.HasGain(t);
                Update(index, b => b with { Type = t });
            };
            frequency.ValueChanged += (_, e) => Update(index, b => b with { Frequency = (double)(e.NewValue ?? 1000) });
            gain.ValueChanged += (_, e) => Update(index, b => b with { GainDb = (double)(e.NewValue ?? 0) });
            q.ValueChanged += (_, e) => Update(index, b => b with { Q = (double)(e.NewValue ?? 1) });
            delete.Click += (_, _) =>
            {
                _controller.Change(eq => eq.Parametric.RemoveAt(index));
                Rebuild();
            };
            _rows.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new TextBlock { Text = (i + 1).ToString(CultureInfo.InvariantCulture), Width = 16, VerticalAlignment = VerticalAlignment.Center }, on, type, frequency, gain, q, delete },
            });
        }
    }

    private void Update(int index, Func<ParametricBand, ParametricBand> change)
    {
        if (index < _controller.Current.Parametric.Count)
        {
            _controller.Change(eq => eq.Parametric[index] = change(eq.Parametric[index]));
        }
    }

    private static NumericUpDown Number(double value, double min, double max, decimal step, string format) => new()
    {
        Minimum = (decimal)min,
        Maximum = (decimal)max,
        Increment = step,
        Value = (decimal)Math.Clamp(value, min, max),
        FormatString = format,
        Width = 110,
    };
}

/// <summary>
/// EQ-05: the DSP chain window (EQ2 › DSP, Settings › Equalizer &amp; DSP): the effects in processing order with
/// on/off, Move up/down (Alt+Up/Down) and the selected effect's settings. Changes are heard at once.
/// </summary>
public sealed class DspChainWindow : Window
{
    private static DspChainWindow? _open;
    private readonly DspService _dsp;
    private readonly SettingsStore _settings;
    private readonly AudioTastic.Core.Audio.Dsp.Vst3.Vst3Library _vst3;
    private readonly ListBox _list = new() { MinHeight = 200, Width = 220 };
    private readonly StackPanel _detail = new() { Spacing = 8, Width = 320 };
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private EqualizerDspSettings _working;
    private bool _rebuilding;

    private DspChainWindow(DspService dsp, SettingsStore settings, AudioTastic.Core.Audio.Dsp.Vst3.Vst3Library vst3)
    {
        _dsp = dsp;
        _settings = settings;
        _vst3 = vst3;
        _working = settings.Current.EqualizerDsp;
        Title = "DSP chain";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Avalonia.Automation.AutomationProperties.SetName(_list, "Effects in processing order");
        _save.Tick += (_, _) =>
        {
            _save.Stop();
            Save();
        };
        var up = new Button { Content = "Move up" };
        var down = new Button { Content = "Move down" };
        up.Click += (_, _) => Move(-1);
        down.Click += (_, _) => Move(1);
        var addVst3 = new Button { Content = "Add VST3 effect…" };
        addVst3.Click += (_, _) => new Vst3PickerWindow(_vst3, _dsp).ShowDialog(this);
        _list.SelectionChanged += (_, _) => ShowDetail();
        _list.KeyDown += (_, e) =>
        {
            if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Up or Key.Down)
            {
                Move(e.Key == Key.Up ? -1 : 1);
                e.Handled = true;
            }
        };
        var close = new Button { Content = "Close", IsCancel = true, MinWidth = 80, HorizontalAlignment = HorizontalAlignment.Right };
        close.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "Audio passes through the effects from top to bottom, before the volume.", Foreground = Brushes.Gray, FontSize = 12 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 16,
                    Children =
                    {
                        new StackPanel { Spacing = 6, Children = { _list, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { up, down } }, addVst3 } },
                        _detail,
                    },
                },
                close,
            },
        };
        dsp.Changed += OnDspChanged;
        Closed += (_, _) =>
        {
            dsp.Changed -= OnDspChanged;
            if (_save.IsEnabled)
            {
                _save.Stop();
                Save();
            }
            _open = null;
        };
        RebuildList();
    }

    /// <summary>Shows the window (one at a time).</summary>
    public static void ShowFor(Window owner, DspService dsp, SettingsStore settings, AudioTastic.Core.Audio.Dsp.Vst3.Vst3Library vst3)
    {
        if (_open is not null)
        {
            _open.Activate();
            return;
        }
        _open = new DspChainWindow(dsp, settings, vst3);
        _open.Show(owner);
    }

    private void OnDspChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_save.IsEnabled)
        {
            _working = _settings.Current.EqualizerDsp;
            RebuildList();
        }
    });

    private void RebuildList()
    {
        _rebuilding = true;
        var selected = (_list.SelectedItem as Control)?.Tag as string;
        var rows = _dsp.Items.Select(item =>
        {
            var box = new CheckBox
            {
                Content = item.Name + (item.IsPlugin ? "  (plugin)" : item.IsVst3 ? (item.Problem is null ? "  (VST3)" : "  (VST3, bypassed)") : string.Empty),
                IsChecked = item.Enabled,
                Tag = item.Id,
            };
            box.IsCheckedChanged += (_, _) => SetEnabled(item.Id, box.IsChecked == true);
            // A click on the row's check box also selects the row, so its details (Open editor…) show (Anders: a
            // left click only ticked it; the details came only with a right click).
            box.AddHandler(PointerPressedEvent, (_, _) => _list.SelectedItem = box, Avalonia.Interactivity.RoutingStrategies.Tunnel);
            box.GotFocus += (_, _) => _list.SelectedItem = box;
            return box;
        }).ToList();
        _list.ItemsSource = rows;
        _list.SelectedItem = rows.FirstOrDefault(r => (string?)r.Tag == selected) ?? rows.FirstOrDefault();
        _rebuilding = false;
        ShowDetail();
    }

    private List<DspChainEntry> CurrentChain() => [.. _dsp.Items.Select(i => new DspChainEntry(i.Id, i.Enabled))];

    private void SetEnabled(string id, bool enabled)
    {
        if (_rebuilding)
        {
            return;
        }
        var chain = CurrentChain();
        var index = chain.FindIndex(e => e.Id == id);
        chain[index] = chain[index] with { Enabled = enabled };
        _settings.Update(SettingsGroup.EqualizerDsp, s => s.EqualizerDsp.Chain = chain);
    }

    private void Move(int delta)
    {
        if ((_list.SelectedItem as Control)?.Tag is not string id)
        {
            return;
        }
        var chain = CurrentChain();
        var index = chain.FindIndex(e => e.Id == id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= chain.Count)
        {
            return;
        }
        (chain[index], chain[target]) = (chain[target], chain[index]);
        _settings.Update(SettingsGroup.EqualizerDsp, s => s.EqualizerDsp.Chain = chain);
    }

    private void ShowDetail()
    {
        _detail.Children.Clear();
        if ((_list.SelectedItem as Control)?.Tag is not string id)
        {
            return;
        }
        var item = _dsp.Items.First(i => i.Id == id);
        _detail.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeight.SemiBold, FontSize = 15 });
        switch (id)
        {
            case DspChainEntry.Equalizer:
                Note("The equalizer is set in the equalizer window (Alt+G): ON switches it, PARAM chooses the parametric editor.");
                break;
            case DspChainEntry.Compressor:
                var c = _working.Compressor;
                Slider("Threshold", c.ThresholdDb, -60, 0, "0.0 dB", v => c.ThresholdDb = v, () => _dsp.Compressor.Apply(c));
                Slider("Ratio", c.Ratio, 1, 20, "0.0:1", v => c.Ratio = v, () => _dsp.Compressor.Apply(c));
                Slider("Attack", c.AttackMs, 0.1, 200, "0.0 ms", v => c.AttackMs = v, () => _dsp.Compressor.Apply(c));
                Slider("Release", c.ReleaseMs, 10, 2000, "0 ms", v => c.ReleaseMs = v, () => _dsp.Compressor.Apply(c));
                Slider("Make-up gain", c.MakeupDb, 0, 24, "0.0 dB", v => c.MakeupDb = v, () => _dsp.Compressor.Apply(c));
                break;
            case DspChainEntry.Limiter:
                var l = _working.Limiter;
                Slider("Ceiling", l.CeilingDb, -24, 0, "0.0 dBFS", v => l.CeilingDb = v, () => _dsp.Limiter.Apply(l));
                Slider("Release", l.ReleaseMs, 10, 1000, "0 ms", v => l.ReleaseMs = v, () => _dsp.Limiter.Apply(l));
                Note("No sample exceeds the ceiling (5 ms look-ahead). Best placed last.");
                break;
            case DspChainEntry.Reverb:
                var r = _working.Reverb;
                Slider("Room size", r.RoomSize, 0, 1, "0%", v => r.RoomSize = v, () => _dsp.Reverb.Apply(r), percent: true);
                Slider("Damping", r.Damping, 0, 1, "0%", v => r.Damping = v, () => _dsp.Reverb.Apply(r), percent: true);
                Slider("Mix", r.Mix, 0, 1, "0%", v => r.Mix = v, () => _dsp.Reverb.Apply(r), percent: true);
                Slider("Width", r.Width, 0, 1, "0%", v => r.Width = v, () => _dsp.Reverb.Apply(r), percent: true);
                break;
            case DspChainEntry.Widener:
                var w = _working.Widener;
                Slider("Width", w.Width, 0, 2, "0%", v => w.Width = v, () => _dsp.Widener.Apply(w), percent: true);
                Note("0 % is mono, 100 % unchanged, 200 % twice as wide.");
                break;
            default:
                if (item.IsVst3)
                {
                    ShowVst3(item);
                    break;
                }
                Note("An effect from a DSP plugin. Its options, if any, are on the plugin's settings page (Settings › Plugins).");
                break;
        }
    }

    /// <summary>EQ-06: a VST3 effect's details: why it is bypassed, its editor, removing it.</summary>
    private void ShowVst3(DspChainItem item)
    {
        var stage = _dsp.FindVst3(item.Id);
        if (item.Problem is { } problem)
        {
            Note("Bypassed: " + problem);
        }
        if (stage is not null)
        {
            Note(stage.Settings.Path);
        }
        var editor = new Button { Content = "Open editor", IsEnabled = stage?.Plugin is { HasEditor: true } };
        editor.Click += (_, _) => Vst3EditorWindow.ShowFor(this, _dsp, item.Id);
        var remove = new Button { Content = "Remove from chain" };
        remove.Click += (_, _) =>
        {
            Vst3EditorWindow.CloseFor(item.Id);
            _dsp.RemoveVst3(item.Id);
        };
        _detail.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { editor, remove } });
        if (stage?.Plugin is { HasEditor: false })
        {
            Note("This effect has no editor of its own on this platform.");
        }
        Note("Its settings are saved with the chain (when its editor closes and when AudioTastic exits).");
    }

    private void Slider(string label, double value, double min, double max, string format, Action<double> set, Action apply, bool percent = false)
    {
        var text = new TextBlock { Width = 70, TextAlignment = TextAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, Width = 180 };
        Avalonia.Automation.AutomationProperties.SetName(slider, label);
        void Show() => text.Text = (percent ? slider.Value * 100 : slider.Value).ToString(format, CultureInfo.CurrentCulture);
        Show();
        slider.ValueChanged += (_, _) =>
        {
            set(slider.Value);
            apply(); // heard at once; saved shortly after the last change
            Show();
            _save.Stop();
            _save.Start();
        };
        _detail.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { new TextBlock { Text = label, Width = 90, VerticalAlignment = VerticalAlignment.Center }, slider, text },
        });
    }

    private void Note(string text) =>
        _detail.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12 });

    private void Save()
    {
        var working = _working;
        _settings.Update(SettingsGroup.EqualizerDsp, s =>
        {
            s.EqualizerDsp.Compressor = working.Compressor;
            s.EqualizerDsp.Limiter = working.Limiter;
            s.EqualizerDsp.Reverb = working.Reverb;
            s.EqualizerDsp.Widener = working.Widener;
        });
    }
}
