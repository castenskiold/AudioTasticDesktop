using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using AudioTastic.Core.Audio;
using AudioTastic.Plugins.Host;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;

namespace AudioTastic.App.Ui;

/// <summary>
/// UI-07 / PG-01: a window hosting a visualisation plugin. The plugin's view (an Avalonia control) gets the newest
/// post-DSP audio about 60 times a second on the UI thread. Faults are reported to the plugin host; the window closes
/// when the plugin is switched off or fails.
/// </summary>
public sealed class VisualisationPluginWindow : Window
{
    private const int Frames = 2048;
    private readonly PluginHost _host;
    private readonly PluginRecord _record;
    private readonly VisualisationFeed _feed;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(1000.0 / 60) };
    private readonly float[] _buffer = new float[Frames * 2];
    private IVisualisation? _visualisation;
    private long _lastWritten = -1;

    private VisualisationPluginWindow(PluginHost host, PluginRecord record, IVisualisationPlugin plugin, VisualisationFeed feed)
    {
        _host = host;
        _record = record;
        _feed = feed;
        Title = "Visualisation";
        Width = 640;
        Height = 360;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        if (!host.Guard(record, () =>
            {
                Title = plugin.Name;
                _visualisation = plugin.Create();
                Content = _visualisation.CreateView() as Control
                    ?? throw new InvalidOperationException("CreateView must return an Avalonia Control.");
            }))
        {
            Content = new TextBlock { Text = "The visualisation could not start (see the log).", Foreground = Brushes.Gray, Margin = new Avalonia.Thickness(16) };
        }
        _timer.Tick += (_, _) => Feed();
        host.PluginUnloading += OnPluginUnloading;
        Opened += (_, _) => _timer.Start();
        Closed += (_, _) =>
        {
            _timer.Stop();
            host.PluginUnloading -= OnPluginUnloading;
            DisposeVisualisation();
            if (Current == this)
            {
                Current = null;
            }
        };
    }

    /// <summary>The open visualisation window, if any (Ctrl+Shift+K toggles it).</summary>
    public static VisualisationPluginWindow? Current { get; private set; }

    /// <summary>Opens <paramref name="plugin"/> in the visualisation window (replacing another one).</summary>
    public static void Open(Window owner, PluginHost host, IVisualisationPlugin plugin, VisualisationFeed feed)
    {
        ArgumentNullException.ThrowIfNull(host);
        if (host.RecordOf(plugin) is not { } record)
        {
            return;
        }
        Current?.Close();
        Current = new VisualisationPluginWindow(host, record, plugin, feed);
        Current.Show(owner);
    }

    private void Feed()
    {
        if (_visualisation is not { } vis)
        {
            return;
        }
        var written = _feed.WrittenFrames;
        if (written == _lastWritten)
        {
            Array.Clear(_buffer); // paused or stopped: silence
        }
        else
        {
            _feed.ReadLatest(_buffer);
        }
        _lastWritten = written;
        if (!_host.Guard(_record, () => vis.OnAudio(_buffer, new AudioFormat(_feed.SampleRate, 2))))
        {
            _timer.Stop();
            Close();
        }
    }

    private void OnPluginUnloading(object? sender, PluginRecord record)
    {
        if (record != _record)
        {
            return;
        }
        // The plugin is disposed right after this returns: drop its view first, on the UI thread.
        void Stop()
        {
            _timer.Stop();
            DisposeVisualisation();
            Dispatcher.UIThread.Post(Close);
        }
        if (Dispatcher.UIThread.CheckAccess())
        {
            Stop();
        }
        else
        {
            Dispatcher.UIThread.Invoke(Stop);
        }
    }

    private void DisposeVisualisation()
    {
        if (_visualisation is { } vis)
        {
            _visualisation = null;
            Content = null;
            _host.Guard(_record, vis.Dispose);
        }
    }
}
