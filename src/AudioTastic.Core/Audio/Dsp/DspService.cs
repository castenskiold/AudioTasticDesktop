using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Core.Audio.Dsp;

/// <summary>One effect as shown in the DSP chain window.</summary>
/// <param name="Id">Chain id (see <see cref="DspChainEntry"/>).</param>
/// <param name="IsPlugin">From a DSP plugin.</param>
/// <param name="IsVst3">EQ-06: a VST3 effect (it can be removed and may have an editor).</param>
/// <param name="Problem">Why the effect is bypassed (a VST3 effect that could not load), or null.</param>
public sealed record DspChainItem(string Id, string Name, bool Enabled, bool IsPlugin, bool IsVst3 = false, string? Problem = null);

/// <summary>
/// EQ-05: keeps the graph's <see cref="DspChain"/> in step with Settings › Equalizer &amp; DSP and the enabled DSP
/// plugins. Built-in stages live as long as the service; plugin stages are created when their plugin appears and
/// disposed (after a render boundary) when it goes away.
/// </summary>
public sealed class DspService : IDisposable
{
    private static readonly string[] BuiltInOrder =
        [DspChainEntry.Equalizer, DspChainEntry.Compressor, DspChainEntry.Widener, DspChainEntry.Reverb, DspChainEntry.Limiter];

    private readonly MixerGraph _graph;
    private readonly SettingsStore _settings;
    private readonly ILogger _logger;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DspStage> _builtIn;
    private readonly Dictionary<string, PluginEffectStage> _plugins = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Vst3.Vst3Stage> _vst3 = new(StringComparer.Ordinal);
    private Func<IReadOnlyList<PluginEffect>> _pluginEffects = () => [];
    private IReadOnlyList<DspChainItem> _items = [];

    public DspService(MixerGraph graph, SettingsStore settings, ILogger<DspService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(settings);
        _graph = graph;
        _settings = settings;
        _logger = logger ?? NullLogger<DspService>.Instance;
        _builtIn = new Dictionary<string, DspStage>(StringComparer.Ordinal)
        {
            [DspChainEntry.Equalizer] = Equalizer,
            [DspChainEntry.Compressor] = Compressor,
            [DspChainEntry.Widener] = Widener,
            [DspChainEntry.Reverb] = Reverb,
            [DspChainEntry.Limiter] = Limiter,
        };
        settings.Changed += OnSettingsChanged;
        Apply(settings.Current.EqualizerDsp);
    }

    public EqualizerStage Equalizer { get; } = new();
    public CompressorStage Compressor { get; } = new();
    public LimiterStage Limiter { get; } = new();
    public ReverbStage Reverb { get; } = new();
    public WidenerStage Widener { get; } = new();

    /// <summary>The chain in order, with every known effect (also those not yet in the settings).</summary>
    public IReadOnlyList<DspChainItem> Items => Volatile.Read(ref _items);

    /// <summary>Raised after the chain or an effect's on/off state changed (any thread).</summary>
    public event EventHandler? Changed;

    /// <summary>Sample rate the chain runs at (44.1 kHz before the first track).</summary>
    public int SampleRate => _graph.Format.SampleRate;

    /// <summary>PG-01: the source of plugin effects (set by the app once the plugin host exists).</summary>
    public void SetPluginEffects(Func<IReadOnlyList<PluginEffect>> source)
    {
        ArgumentNullException.ThrowIfNull(source);
        _pluginEffects = source;
        Apply(_settings.Current.EqualizerDsp);
    }

    /// <summary>Re-reads the plugin effects (plugins enabled, disabled or installed).</summary>
    public void RefreshPlugins() => Apply(_settings.Current.EqualizerDsp);

    /// <summary>
    /// PG-03: a DSP plugin is about to be unloaded: take its effects out of the chain and dispose them once the audio
    /// thread can no longer be inside them.
    /// </summary>
    public void RemovePlugin(string pluginId)
    {
        List<PluginEffectStage> removed;
        lock (_lock)
        {
            removed = [.. _plugins.Values.Where(p => string.Equals(p.Effect.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))];
            if (removed.Count == 0)
            {
                return;
            }
            foreach (var stage in removed)
            {
                _plugins.Remove(stage.Id);
            }
            _graph.Dsp.SetStages(_graph.Dsp.Stages.Where(s => !removed.Contains(s)));
        }
        _graph.WaitForRenderBoundary();
        foreach (var stage in removed)
        {
            stage.Dispose();
        }
        Apply(_settings.Current.EqualizerDsp, includePlugins: false);
    }

    /// <summary>
    /// EQ-06: adds a VST3 effect to the chain (switched on, before the limiter so it still guards the output) and
    /// returns its id. The plug-in is loaded when the chain picks the change up.
    /// </summary>
    public string AddVst3(string path, string classId, string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        ArgumentException.ThrowIfNullOrEmpty(classId);
        var id = "vst3:" + Guid.NewGuid().ToString("N")[..12];
        _settings.Update(SettingsGroup.EqualizerDsp, s =>
        {
            var dsp = s.EqualizerDsp;
            dsp.Vst3.Add(new Vst3EffectSettings { Id = id, Path = path, ClassId = classId, Name = name });
            var limiter = dsp.Chain.FindIndex(e => e.Id == DspChainEntry.Limiter);
            dsp.Chain.Insert(limiter >= 0 ? limiter : dsp.Chain.Count, new DspChainEntry(id, true));
        });
        return id;
    }

    /// <summary>EQ-06: takes a VST3 effect out of the chain (its plug-in is closed after a render boundary).</summary>
    public void RemoveVst3(string id) => _settings.Update(SettingsGroup.EqualizerDsp, s =>
    {
        s.EqualizerDsp.Vst3.RemoveAll(v => v.Id == id);
        s.EqualizerDsp.Chain.RemoveAll(e => e.Id == id);
    });

    /// <summary>EQ-06: the stage of a VST3 effect (for its editor), or null.</summary>
    public Vst3.Vst3Stage? FindVst3(string id)
    {
        lock (_lock)
        {
            return _vst3.GetValueOrDefault(id);
        }
    }

    /// <summary>EQ-06: stores every loaded VST3 effect's current state in the settings (editor closed, exit).</summary>
    public void SaveVst3States()
    {
        Dictionary<string, string> states;
        lock (_lock)
        {
            states = _vst3.Values.Select(v => (v.Id, State: v.CaptureState())).Where(v => v.State is not null)
                .ToDictionary(v => v.Id, v => v.State!, StringComparer.Ordinal);
        }
        if (states.Count == 0)
        {
            return;
        }
        _settings.Update(SettingsGroup.EqualizerDsp, s =>
        {
            foreach (var effect in s.EqualizerDsp.Vst3)
            {
                if (states.TryGetValue(effect.Id, out var state))
                {
                    effect.State = state;
                }
            }
        });
    }

    /// <summary>EQ-01: applies equalizer settings to the sound at once without saving them (slider drags).</summary>
    public void PreviewEqualizer(EqualizerSettings settings) => Equalizer.Apply(settings);

    /// <summary>EQ-03 helper: the curve of <paramref name="settings"/> at the current sample rate.</summary>
    public EqualizerDesign DesignFor(EqualizerSettings settings) => EqualizerDesign.Create(settings, SampleRate);

    public void Dispose()
    {
        SaveVst3States(); // the effects' last settings survive a restart
        _settings.Changed -= OnSettingsChanged;
        lock (_lock)
        {
            _graph.Dsp.SetStages([]);
            foreach (var stage in _plugins.Values)
            {
                stage.Dispose();
            }
            _plugins.Clear();
        }
        _graph.WaitForRenderBoundary();
        lock (_lock)
        {
            foreach (var stage in _vst3.Values)
            {
                stage.Dispose();
            }
            _vst3.Clear();
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Group == SettingsGroup.EqualizerDsp)
        {
            Apply(e.Settings.EqualizerDsp);
        }
    }

    private void Apply(EqualizerDspSettings settings, bool includePlugins = true)
    {
        List<PluginEffectStage> gone = [];
        List<Vst3.Vst3Stage> goneVst3 = [];
        lock (_lock)
        {
            // EQ-06: one stage per VST3 effect in the settings; a changed module or class gets a new stage.
            foreach (var (id, stage) in _vst3.ToList())
            {
                var wanted = settings.Vst3.FirstOrDefault(v => v.Id == id);
                if (wanted is null || wanted.Path != stage.Settings.Path || wanted.ClassId != stage.Settings.ClassId)
                {
                    goneVst3.Add(stage);
                    _vst3.Remove(id);
                }
            }
            foreach (var effect in settings.Vst3.Where(v => !_vst3.ContainsKey(v.Id) && v.Id.Length > 0))
            {
                _vst3[effect.Id] = new Vst3.Vst3Stage(effect, _logger);
            }

            Equalizer.Apply(settings.Equalizer);
            Compressor.Apply(settings.Compressor);
            Limiter.Apply(settings.Limiter);
            Reverb.Apply(settings.Reverb);
            Widener.Apply(settings.Widener);

            if (includePlugins)
            {
                IReadOnlyList<PluginEffect> effects;
                try
                {
                    effects = _pluginEffects();
                }
#pragma warning disable CA1031 // Plugin enumeration must never break the player's audio settings.
                catch (Exception ex)
#pragma warning restore CA1031
                {
                    _logger.LogWarning(ex, "Could not list DSP plugin effects");
                    effects = [];
                }
                var ids = effects.Select(e => e.Id).ToHashSet(StringComparer.Ordinal);
                foreach (var id in _plugins.Keys.Where(id => !ids.Contains(id)).ToList())
                {
                    gone.Add(_plugins[id]);
                    _plugins.Remove(id);
                }
                foreach (var effect in effects.Where(e => !_plugins.ContainsKey(e.Id)))
                {
                    _plugins[effect.Id] = new PluginEffectStage(effect);
                }
            }

            // Order: the settings' chain first, then effects it does not list yet (switched off).
            var stages = new List<DspStage>();
            var items = new List<DspChainItem>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            void Add(string id, bool enabled)
            {
                DspStage? stage = _builtIn.TryGetValue(id, out var b) ? b : _plugins.TryGetValue(id, out var p) ? p
                    : _vst3.TryGetValue(id, out var v) ? v : null;
                if (stage is null || !seen.Add(id))
                {
                    return;
                }
                stage.Enabled = enabled;
                stages.Add(stage);
                items.Add(new DspChainItem(id, stage.Name, enabled, stage is PluginEffectStage, stage is Vst3.Vst3Stage, (stage as Vst3.Vst3Stage)?.Problem));
            }
            foreach (var entry in settings.Chain)
            {
                Add(entry.Id, entry.Enabled);
            }
            foreach (var id in BuiltInOrder.Concat(_plugins.Keys.Order(StringComparer.Ordinal)).Concat(_vst3.Keys))
            {
                Add(id, false);
            }
            _graph.Dsp.SetStages(stages); // configures new stages (VST3 effects load here)
            Volatile.Write(ref _items, [.. items.Select(i => i.IsVst3 && _vst3.TryGetValue(i.Id, out var v) ? i with { Problem = v.Problem } : i)]);
        }
        if (gone.Count > 0 || goneVst3.Count > 0)
        {
            _graph.WaitForRenderBoundary();
            foreach (var stage in gone)
            {
                stage.Dispose();
            }
            foreach (var stage in goneVst3)
            {
                stage.Dispose();
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
