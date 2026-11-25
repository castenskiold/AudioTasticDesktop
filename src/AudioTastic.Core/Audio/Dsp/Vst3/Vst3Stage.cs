using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Core.Audio.Dsp.Vst3;

/// <summary>
/// EQ-06: a VST3 effect in the DSP chain. The plug-in is loaded and set up in <see cref="Configure"/> (a control
/// thread: the chain configures a stage before it joins, and format changes happen with no output running), with its
/// saved state restored once. A plug-in that cannot load or fails while processing is bypassed (the audio passes
/// through unchanged) and reports why in <see cref="Problem"/>.
/// </summary>
public sealed class Vst3Stage(Vst3EffectSettings settings, ILogger logger) : DspStage(settings.Id, settings.Name), IDisposable
{
    private Vst3Plugin? _plugin;
    private volatile bool _faulted;
    private volatile bool _processFailed;
    private volatile int _latency;
    private string? _problem;

    public Vst3EffectSettings Settings { get; } = settings;

    /// <summary>The loaded plug-in (null before the first Configure or after a failed load).</summary>
    public Vst3Plugin? Plugin => _plugin;

    /// <summary>Why the effect is bypassed, or null.</summary>
    public string? Problem => _processFailed ? "The plug-in failed while processing and is bypassed." : _problem;

    public override void Configure(int sampleRate, int channels)
    {
        if (channels != 2)
        {
            Fail("The DSP chain is not stereo.");
            return;
        }
        try
        {
            if (_plugin is null)
            {
                var plugin = Vst3Plugin.Open(Settings.Path, Settings.ClassId);
                if (Settings.State is { Length: > 0 } saved)
                {
                    try
                    {
                        plugin.SetState(Convert.FromBase64String(saved));
                    }
                    catch (Exception ex) when (ex is Vst3Exception or FormatException)
                    {
                        logger.LogWarning("{Effect}: the saved settings could not be restored ({Message})", Name, ex.Message);
                    }
                }
                _plugin = plugin;
            }
            _plugin.Setup(sampleRate);
            _plugin.TakeRestartFlags(); // set-up changes do not count
            _latency = Math.Max(0, _plugin.Latency);
            _problem = null;
            _processFailed = false;
            _faulted = false;
            logger.LogInformation("VST3 effect {Effect} ready at {Rate} Hz (latency {Latency} frames)", Name, sampleRate, _latency);
        }
        catch (Exception ex) when (ex is Vst3Exception or DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            Fail(ex.Message);
        }
    }

    /// <summary>
    /// EQ-06: the plug-in's delay (frames at the chain's rate), read when it is set up and when it reports a change;
    /// the player shows the position that much earlier, so the time display matches what is heard.
    /// </summary>
    public override int LatencyFrames => _faulted ? 0 : _latency;

    /// <summary>
    /// UI thread, while the editor is open (about 30 times a second): passes the plug-in's meters to its editor and
    /// picks up a changed latency. Returns true when the latency changed.
    /// </summary>
    public bool Poll()
    {
        if (_faulted || _plugin is not { } plugin)
        {
            return false;
        }
        plugin.FlushOutputParameters();
        if ((plugin.TakeRestartFlags() & Vst3Plugin.RestartLatencyChanged) == 0)
        {
            return false;
        }
        var latency = Math.Max(0, plugin.Latency);
        if (latency == _latency)
        {
            return false;
        }
        logger.LogInformation("VST3 effect {Effect}: latency now {Latency} frames", Name, latency);
        _latency = latency;
        return true;
    }

    private void Fail(string problem)
    {
        _problem = problem;
        _faulted = true;
        logger.LogWarning("VST3 effect {Effect} is bypassed: {Problem}", Name, problem);
    }

    public override void Process(Span<float> samples, int channels)
    {
        if (_faulted || _plugin is not { } plugin)
        {
            return;
        }
        if (!plugin.Process(samples))
        {
            _faulted = true; // no logging on the audio thread: Problem tells the chain window
            _processFailed = true;
        }
    }

    /// <summary>The plug-in's current state as base64 for the settings, or null when it is not loaded.</summary>
    public string? CaptureState()
    {
        if (_plugin is not { } plugin)
        {
            return null;
        }
        try
        {
            return Convert.ToBase64String(plugin.GetState());
        }
        catch (Vst3Exception ex)
        {
            logger.LogWarning("{Effect}: could not save its settings ({Message})", Name, ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        _faulted = true;
        _plugin?.Dispose();
        _plugin = null;
    }
}
