using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Plugins.Host;

/// <summary>
/// PB-04 / PG-01: the output the engine plays through. It lists the built-in devices (miniaudio) plus the devices of
/// every enabled output plugin, named "Plugin name: device" (or just "Plugin name" when the plugin lists no devices),
/// and forwards each call to the output that owns the chosen device.
/// <para>
/// Plugin calls are guarded (PG-03): an <see cref="InvalidOperationException"/> from <c>Open</c> means "refused"
/// (as for the built-in output, e.g. no exclusive mode) and is passed on; any other exception is a plugin fault and
/// is turned into an <see cref="InvalidOperationException"/>, so the engine falls back to the default device. When an
/// output plugin is switched off or disabled while it plays, <see cref="OutputLost"/> is raised and the engine
/// continues on the default device. A plugin that throws on its own audio thread cannot be caught; output plugins
/// must catch their own exceptions there (see the plugin guide).
/// </para>
/// </summary>
public sealed class OutputRouter : IAudioOutput
{
    private const string Separator = ": ";
    private readonly IAudioOutput _builtIn;
    private readonly PluginHost _host;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<IOutputPlugin, PluginOutput> _outputs = new(ReferenceEqualityComparer.Instance);
    private Dictionary<string, Route> _routes = new(StringComparer.Ordinal);
    private IAudioOutput? _active;
    private PluginOutput? _activePlugin;
    private AudioFormat? _format;
    private bool _disposed;

    public OutputRouter(IAudioOutput builtIn, PluginHost host, ILogger<OutputRouter>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(builtIn);
        ArgumentNullException.ThrowIfNull(host);
        _builtIn = builtIn;
        _host = host;
        _logger = logger ?? NullLogger<OutputRouter>.Instance;
        _builtIn.DevicesChanged += OnDevicesChanged;
        _builtIn.OutputLost += OnBuiltInLost;
        _host.PluginUnloading += OnPluginUnloading;
        _host.PluginsChanged += OnDevicesChanged;
    }

    public event EventHandler? DevicesChanged;
    public event EventHandler<AudioOutputLostEventArgs>? OutputLost;

    /// <summary>The output plugin (id) that is playing, or null for the built-in output.</summary>
    public string? ActivePluginId
    {
        get
        {
            lock (_lock)
            {
                return _activePlugin?.Record.Id;
            }
        }
    }

    public string BackendName => Current(o => o.BackendName, _builtIn.BackendName);
    public bool IsOpen => Current(o => o.IsOpen, false);
    public bool IsRunning => Current(o => o.IsRunning, false);
    public bool IsExclusive => Current(o => o.IsExclusive, false);
    public OutputSampleFormat SampleFormat => Current(o => o.SampleFormat, OutputSampleFormat.F32);
    public int DeviceSampleRate => Current(o => o.DeviceSampleRate, 0);

    /// <summary>The open format; kept after an output plugin went away, so the engine can reopen on the default device.</summary>
    public AudioFormat? Format
    {
        get
        {
            lock (_lock)
            {
                return _active is null ? _format : Current(o => o.Format, null) ?? _format;
            }
        }
    }

    public string? DeviceName
    {
        get
        {
            lock (_lock)
            {
                var name = Current(o => o.DeviceName, null);
                return _activePlugin is { } p ? (name is null ? p.Name : p.Name + Separator + name) : name;
            }
        }
    }

    public IReadOnlyList<AudioDeviceInfo> GetDevices()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var routes = new Dictionary<string, Route>(StringComparer.Ordinal);
            var list = new List<AudioDeviceInfo>();
            foreach (var device in _builtIn.GetDevices())
            {
                list.Add(device);
                routes.TryAdd(device.Name, new Route(null, device));
            }
            foreach (var output in PluginOutputs())
            {
                var devices = Guard(output, () => output.Output.GetDevices(), []);
                if (devices.Count == 0)
                {
                    Add(new AudioDeviceInfo(output.Name, false, ReadOnlyMemory<byte>.Empty), output, null);
                }
                foreach (var device in devices)
                {
                    Add(device with { Name = output.Name + Separator + device.Name, IsDefault = false }, output, device);
                }
            }
            _routes = routes;
            return list;

            void Add(AudioDeviceInfo shown, PluginOutput output, AudioDeviceInfo? inner)
            {
                if (routes.TryAdd(shown.Name, new Route(output, inner)))
                {
                    list.Add(shown);
                }
            }
        }
    }

    public IReadOnlyList<DeviceFormat> GetDeviceFormats(AudioDeviceInfo? device)
    {
        lock (_lock)
        {
            var route = Resolve(device);
            return route.Plugin is { } p ? Guard(p, () => p.Output.GetDeviceFormats(route.Device), []) : _builtIn.GetDeviceFormats(route.Device);
        }
    }

    public void Open(AudioDeviceInfo? device, AudioFormat format, OutputOptions options, IAudioRenderer renderer)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(renderer);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var route = Resolve(device);
            var target = route.Plugin?.Output ?? _builtIn;
            if (_active is not null && !ReferenceEquals(_active, target))
            {
                CloseActive();
            }
            _format = null;
            if (route.Plugin is { } plugin)
            {
                try
                {
                    plugin.Output.Open(route.Device, format, options, renderer);
                }
                catch (InvalidOperationException)
                {
                    throw; // refused (format, exclusive mode): not a fault
                }
#pragma warning disable CA1031 // A plugin fault must never escape into the player.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _host.ReportFault(plugin.Record, ex);
                    throw new InvalidOperationException($"Output plugin '{plugin.Name}' could not open: {ex.Message}", ex);
                }
                _logger.LogInformation("Playing through output plugin {Plugin} ({Device})", plugin.Record.Id, route.Device?.Name ?? "its default device");
            }
            else
            {
                _builtIn.Open(route.Device, format, options, renderer);
            }
            _active = target;
            _activePlugin = route.Plugin;
            _format = format;
        }
    }

    public void SetDither(bool dither) => Run(o => o.SetDither(dither));

    public void StartOutput()
    {
        lock (_lock)
        {
            if (_activePlugin is { } plugin)
            {
                if (!_host.Guard(plugin.Record, plugin.Output.StartOutput))
                {
                    // The plugin failed to start: let the engine continue on the default device.
                    RaiseLostLater($"output plugin '{plugin.Name}' failed to start");
                }
                return;
            }
            (_active ?? _builtIn).StartOutput();
        }
    }

    public void StopOutput() => Run(o => o.StopOutput());

    public void Close()
    {
        lock (_lock)
        {
            CloseActive();
            _format = null;
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            _builtIn.DevicesChanged -= OnDevicesChanged;
            _builtIn.OutputLost -= OnBuiltInLost;
            _host.PluginUnloading -= OnPluginUnloading;
            _host.PluginsChanged -= OnDevicesChanged;
            if (_activePlugin is not null)
            {
                CloseActive();
            }
            foreach (var output in _outputs.Values)
            {
                DisposeOutput(output);
            }
            _outputs.Clear();
        }
        // The built-in output belongs to the DI container, which disposes it.
    }

    // ---- internals --------------------------------------------------------------------------------------

    private sealed record PluginOutput(IOutputPlugin Plugin, PluginRecord Record, string Name, IAudioOutput Output);

    private sealed record Route(PluginOutput? Plugin, AudioDeviceInfo? Device);

    /// <summary>The enabled output plugins' outputs, created on first use (one per plugin instance).</summary>
    private List<PluginOutput> PluginOutputs()
    {
        var result = new List<PluginOutput>();
        foreach (var plugin in _host.Instances<IOutputPlugin>())
        {
            if (_outputs.TryGetValue(plugin, out var existing))
            {
                result.Add(existing);
                continue;
            }
            if (_host.RecordOf(plugin) is not { } record)
            {
                continue;
            }
            string name = record.Manifest.Name;
            IAudioOutput? output = null;
            if (!_host.Guard(record, () =>
                {
                    name = string.IsNullOrWhiteSpace(plugin.Name) ? record.Manifest.Name : plugin.Name.Trim();
                    output = plugin.CreateOutput();
                }) || output is null)
            {
                continue;
            }
            var entry = new PluginOutput(plugin, record, name, output);
            output.DevicesChanged += OnDevicesChanged;
            output.OutputLost += OnPluginLost;
            _outputs[plugin] = entry;
            result.Add(entry);
        }
        return result;
    }

    /// <summary>Finds who owns <paramref name="device"/> (null = the built-in default device).</summary>
    private Route Resolve(AudioDeviceInfo? device)
    {
        if (device is null)
        {
            return new Route(null, null);
        }
        if (!_routes.ContainsKey(device.Name))
        {
            GetDevices(); // the list may have changed since the caller got the device
        }
        return _routes.TryGetValue(device.Name, out var route)
            ? route
            : throw new InvalidOperationException($"Output device '{device.Name}' is not available.");
    }

    private T Current<T>(Func<IAudioOutput, T> get, T fallback)
    {
        lock (_lock)
        {
            if (_active is null)
            {
                return fallback;
            }
            return _activePlugin is { } plugin ? Guard(plugin, () => get(plugin.Output), fallback) : get(_active);
        }
    }

    private void Run(Action<IAudioOutput> action)
    {
        lock (_lock)
        {
            if (_activePlugin is { } plugin)
            {
                _host.Guard(plugin.Record, () => action(plugin.Output));
            }
            else if (_active is not null)
            {
                action(_active);
            }
        }
    }

    private T Guard<T>(PluginOutput plugin, Func<T> call, T fallback)
    {
        var result = fallback;
        _host.Guard(plugin.Record, () => result = call());
        return result;
    }

    private void CloseActive()
    {
        if (_activePlugin is { } plugin)
        {
            _host.Guard(plugin.Record, () =>
            {
                plugin.Output.StopOutput();
                plugin.Output.Close();
            });
        }
        else
        {
            _active?.Close();
        }
        _active = null;
        _activePlugin = null;
    }

    private void DisposeOutput(PluginOutput output)
    {
        output.Output.DevicesChanged -= OnDevicesChanged;
        output.Output.OutputLost -= OnPluginLost;
        try
        {
            output.Output.Dispose();
        }
#pragma warning disable CA1031 // A plugin fault must never escape into the player.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogWarning(ex, "Output plugin {Id} failed while closing", output.Record.Id);
        }
    }

    /// <summary>A plugin is about to be disposed: close and drop its outputs (raising OutputLost when it was playing).</summary>
    private void OnPluginUnloading(object? sender, PluginRecord record)
    {
        string? lost = null;
        lock (_lock)
        {
            foreach (var (plugin, output) in _outputs.Where(o => o.Value.Record == record).ToList())
            {
                if (ReferenceEquals(_activePlugin, output))
                {
                    CloseActive();
                    lost = $"output plugin '{output.Name}' was switched off";
                }
                DisposeOutput(output);
                _outputs.Remove(plugin);
            }
        }
        if (lost is not null)
        {
            _logger.LogWarning("Output lost: {Reason}", lost);
            RaiseLostLater(lost);
        }
    }

    private void RaiseLostLater(string reason) =>
        ThreadPool.QueueUserWorkItem(static s => s.Router.OutputLost?.Invoke(s.Router, new AudioOutputLostEventArgs(s.Reason)), (Router: this, Reason: reason), false);

    private void OnBuiltInLost(object? sender, AudioOutputLostEventArgs e)
    {
        if (_activePlugin is null)
        {
            OutputLost?.Invoke(this, e);
        }
    }

    private void OnPluginLost(object? sender, AudioOutputLostEventArgs e)
    {
        lock (_lock)
        {
            if (_activePlugin is null || !ReferenceEquals(_activePlugin.Output, sender))
            {
                return;
            }
            CloseActive(); // the engine reopens on the default device; _format is kept for that
        }
        OutputLost?.Invoke(this, e);
    }

    private void OnDevicesChanged(object? sender, EventArgs e) => DevicesChanged?.Invoke(this, EventArgs.Empty);
}
