using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Core.Audio.Dsp;

/// <summary>One effect as shown in the DSP chain window.</summary>
/// <param name="Id">Chain id (see <see cref="DspChainEntry"/>).</param>
/// <param name="IsPlugin">From a DSP plugin.</param>
public sealed record DspChainItem(string Id, string Name, bool Enabled, bool IsPlugin);

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

    /// <summary>EQ-01: applies equalizer settings to the sound at once without saving them (slider drags).</summary>
    public void PreviewEqualizer(EqualizerSettings settings) => Equalizer.Apply(settings);

    /// <summary>EQ-03 helper: the curve of <paramref name="settings"/> at the current sample rate.</summary>
    public EqualizerDesign DesignFor(EqualizerSettings settings) => EqualizerDesign.Create(settings, SampleRate);

    public void Dispose()
    {
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
        lock (_lock)
        {
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
                DspStage? stage = _builtIn.TryGetValue(id, out var b) ? b : _plugins.TryGetValue(id, out var p) ? p : null;
                if (stage is null || !seen.Add(id))
                {
                    return;
                }
                stage.Enabled = enabled;
                stages.Add(stage);
                items.Add(new DspChainItem(id, stage.Name, enabled, stage is PluginEffectStage));
            }
            foreach (var entry in settings.Chain)
            {
                Add(entry.Id, entry.Enabled);
            }
            foreach (var id in BuiltInOrder.Concat(_plugins.Keys.Order(StringComparer.Ordinal)))
            {
                Add(id, false);
            }
            _graph.Dsp.SetStages(stages);
            Volatile.Write(ref _items, items);
        }
        if (gone.Count > 0)
        {
            _graph.WaitForRenderBoundary();
            foreach (var stage in gone)
            {
                stage.Dispose();
            }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
