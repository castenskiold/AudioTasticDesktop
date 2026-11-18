using System.Diagnostics;
using AudioTastic.Data.Settings;
using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;

namespace AudioTastic.Core.Audio.Dsp;

/// <summary>
/// EQ-05 / PG-03: an effect from a DSP plugin, as offered to the <see cref="DspService"/>.
/// <paramref name="Fault"/> reports a plugin fault to the plugin host (3 within a minute disable the plugin).
/// </summary>
public sealed record PluginEffect(string PluginId, string Name, Func<AudioFormat, IDspEffect> Create, Action<Exception> Fault)
{
    /// <summary>Chain id: "plugin:&lt;plugin id&gt;:&lt;effect name&gt;".</summary>
    public string Id => DspChainEntry.PluginPrefix + PluginId + ":" + Name;
}

/// <summary>
/// EQ-05 / PG-03: runs a plugin's <see cref="IDspEffect"/> in the chain. An exception bypasses the effect until the
/// next format change and is reported as a fault. Time budget: an effect may use at most
/// <see cref="BudgetFraction"/> of real time, measured over each second of audio; every second over budget is a fault.
/// </summary>
public sealed class PluginEffectStage(PluginEffect effect) : DspStage(effect.Id, effect.Name), IDisposable
{
    /// <summary>Largest share of real time one plugin effect may use.</summary>
    public const double BudgetFraction = 0.10;

    private IDspEffect? _effect;
    private volatile bool _faulted;
    private long _ticks;
    private long _frames;
    private int _sampleRate = 44_100;

    public PluginEffect Effect { get; } = effect;

    public override void Configure(int sampleRate, int channels)
    {
        DisposeEffect();
        _sampleRate = sampleRate;
        _ticks = 0;
        _frames = 0;
        try
        {
            _effect = Effect.Create(new AudioFormat(sampleRate, channels));
            _faulted = false;
        }
#pragma warning disable CA1031 // A plugin fault must never escape into the player.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _faulted = true;
            Effect.Fault(ex);
        }
    }

    public override void Process(Span<float> samples, int channels)
    {
        if (_faulted || _effect is not { } effect)
        {
            return;
        }
        var start = Stopwatch.GetTimestamp();
        try
        {
            effect.Process(samples, channels);
        }
#pragma warning disable CA1031 // A plugin fault must never escape into the audio thread.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _faulted = true;
            ReportLater(ex);
            return;
        }
        _ticks += Stopwatch.GetTimestamp() - start;
        _frames += samples.Length / channels;
        if (_frames >= _sampleRate)
        {
            var used = _ticks / (double)Stopwatch.Frequency / (_frames / (double)_sampleRate);
            _ticks = 0;
            _frames = 0;
            if (used > BudgetFraction)
            {
                ReportLater(new TimeoutException(
                    $"The effect used {used:P0} of real time (budget {BudgetFraction:P0})."));
            }
        }
    }

    public override void Reset()
    {
        if (_faulted || _effect is not { } effect)
        {
            return;
        }
        try
        {
            effect.Reset();
        }
#pragma warning disable CA1031 // A plugin fault must never escape into the audio thread.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _faulted = true;
            ReportLater(ex);
        }
    }

    /// <summary>Only after the stage left the chain and a render boundary passed.</summary>
    public void Dispose() => DisposeEffect();

    private void DisposeEffect()
    {
        var effect = _effect;
        _effect = null;
        if (effect is null)
        {
            return;
        }
        try
        {
            effect.Dispose();
        }
#pragma warning disable CA1031 // A plugin fault must never escape into the player.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            Effect.Fault(ex);
        }
    }

    // Reporting allocates and may unload the plugin: never on the audio thread.
    private void ReportLater(Exception ex) =>
        ThreadPool.QueueUserWorkItem(static s => s.Stage.Effect.Fault(s.Ex), (Stage: this, Ex: ex), false);
}
