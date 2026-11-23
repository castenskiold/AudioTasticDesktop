using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Core.Audio.Dsp.Vst3;

namespace AudioTastic.App.Ui.Equalizer;

/// <summary>
/// EQ-06: DSP chain › Add VST3 effect…: the effects found on this computer, a rescan, and the blacklist count. The
/// first open scans if nothing was scanned yet.
/// </summary>
public sealed class Vst3PickerWindow : Window
{
    private readonly Vst3Library _library;
    private readonly DspService _dsp;
    private readonly ListBox _list = new() { Height = 300, Width = 420 };
    private readonly TextBlock _status = new() { Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 420 };
    private readonly Button _add = new() { Content = "Add", IsDefault = true, MinWidth = 80 };
    private readonly Button _rescan = new() { Content = "Rescan" };

    public Vst3PickerWindow(Vst3Library library, DspService dsp)
    {
        _library = library;
        _dsp = dsp;
        Title = "Add VST3 effect";
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Avalonia.Automation.AutomationProperties.SetName(_list, "VST3 effects found on this computer");
        _add.Click += (_, _) => AddSelected();
        _list.DoubleTapped += (_, _) => AddSelected();
        _rescan.Click += async (_, _) => await ScanAsync(full: true);
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80 };
        cancel.Click += (_, _) => Close();
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                _list,
                _status,
                new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
                    Children = { _rescan, Column(_add, 2), Column(cancel, 3) },
                },
            },
        };
        Opened += async (_, _) =>
        {
            if (!_library.IsAvailable)
            {
                _status.Text = "VST3 is not available: the VST3 host library is missing from this installation.";
                _add.IsEnabled = _rescan.IsEnabled = false;
                return;
            }
            Fill();
            if (!_library.HasScanned)
            {
                await ScanAsync(full: false);
            }
        };
    }

    private static Control Column(Control control, int column)
    {
        Grid.SetColumn(control, column);
        control.Margin = new Thickness(8, 0, 0, 0);
        return control;
    }

    private void Fill()
    {
        var effects = _library.Effects;
        _list.ItemsSource = effects.Select(e => new TextBlock
        {
            Text = $"{e.Name}   ·   {e.Class.Vendor}   ·   {e.Class.SubCategories.Replace("|", ", ", StringComparison.Ordinal)}",
            Tag = e,
        }).ToList();
        _list.SelectedIndex = effects.Count > 0 ? 0 : -1;
        var blacklisted = _library.Blacklisted.Count;
        _status.Text = (effects.Count == 0 ? "No VST3 effects found. Install some, or add a folder in Settings › Equalizer & DSP, then Rescan." : $"{effects.Count} effect(s).")
                       + (blacklisted > 0 ? $" {blacklisted} module(s) are blacklisted because they crashed or failed while loading (see Settings › Equalizer & DSP)." : string.Empty);
        _add.IsEnabled = effects.Count > 0;
    }

    private async Task ScanAsync(bool full)
    {
        _rescan.IsEnabled = _add.IsEnabled = false;
        var progress = new Progress<string>(name => _status.Text = $"Scanning {name}…");
        try
        {
            await _library.RescanAsync(full, progress);
        }
        finally
        {
            _rescan.IsEnabled = true;
            Fill();
        }
    }

    private void AddSelected()
    {
        if ((_list.SelectedItem as Control)?.Tag is Vst3Effect effect)
        {
            _dsp.AddVst3(effect.Path, effect.Class.Id, effect.Name);
            Close();
        }
    }
}

/// <summary>EQ-06: a VST3 effect's own editor in a window of its own (one per effect).</summary>
public sealed class Vst3EditorWindow : Window
{
    private static readonly Dictionary<string, Vst3EditorWindow> Open = new(StringComparer.Ordinal);
    private readonly DspService _dsp;

    private Vst3EditorWindow(Vst3Stage stage, Vst3Plugin plugin, DspService dsp)
    {
        _dsp = dsp;
        Title = stage.Name;
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        var host = new Vst3EditorHost(plugin, this, message => Dispatcher.UIThread.Post(() => Content = new TextBlock { Text = message, Margin = new Thickness(16), TextWrapping = TextWrapping.Wrap, MaxWidth = 360 }));
        Content = host;
        Closed += (_, _) =>
        {
            Open.Remove(stage.Id);
            _dsp.SaveVst3States(); // the editor's changes survive a restart
        };
    }

    /// <summary>Shows the editor of <paramref name="id"/> (or brings it to the front).</summary>
    public static void ShowFor(Window owner, DspService dsp, string id)
    {
        if (Open.TryGetValue(id, out var window))
        {
            window.Activate();
            return;
        }
        if (dsp.FindVst3(id) is not { Plugin: { } plugin } stage)
        {
            return;
        }
        window = new Vst3EditorWindow(stage, plugin, dsp);
        Open[id] = window;
        window.Show(owner);
    }

    /// <summary>Closes the editor of <paramref name="id"/> (before the effect leaves the chain).</summary>
    public static void CloseFor(string id)
    {
        if (Open.TryGetValue(id, out var window))
        {
            window.Close();
        }
    }
}

/// <summary>
/// Hosts the plug-in's editor view in a native child window (Avalonia's default native child: HWND on Windows, NSView
/// on macOS, an X11 window on Linux) and follows the size the editor asks for.
/// </summary>
internal sealed class Vst3EditorHost(Vst3Plugin plugin, Window window, Action<string> failed) : NativeControlHost
{
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        var handle = base.CreateNativeControlCore(parent);
        try
        {
            var (width, height) = plugin.OpenEditor(handle.Handle, (w, h) => Dispatcher.UIThread.Post(() => Resize(w, h)));
            Resize(width, height);
        }
        catch (Vst3Exception ex)
        {
            failed(ex.Message);
        }
        return handle;
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        plugin.CloseEditor(); // before its native window goes
        base.DestroyNativeControlCore(control);
    }

    /// <summary>Editor sizes are pixels on Windows and Linux, points on macOS (VST3 IPlugView).</summary>
    private void Resize(int width, int height)
    {
        var scale = OperatingSystem.IsMacOS() ? 1 : window.RenderScaling;
        Width = Math.Max(50, width / scale);
        Height = Math.Max(30, height / scale);
    }
}
