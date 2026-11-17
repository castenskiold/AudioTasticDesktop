using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Playlists;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Sdk.Plugins;

/// <summary>
/// PG-01: the seven plugin types. A plugin class implements one or more of the interfaces below (one assembly may
/// contain several plugin classes). Every plugin class needs a public parameterless constructor.
/// </summary>
public enum PluginType
{
    Input,
    Output,
    Dsp,
    Visualisation,
    General,
    UiPanel,
    CastTarget,
}

/// <summary>
/// PG-01: base of every plugin. The host creates the instance, calls <see cref="Initialize"/> once, and
/// <see cref="IDisposable.Dispose"/> when the plugin is disabled, uninstalled or the player exits.
/// Exceptions thrown by a plugin are caught and counted; 3 within a minute disable the plugin (PG-03).
/// </summary>
public interface IPlugin : IDisposable
{
    void Initialize(IPluginContext context);
}

/// <summary>What the host gives a plugin.</summary>
public interface IPluginContext
{
    /// <summary>The id from plugin.json.</summary>
    string PluginId { get; }

    /// <summary>The folder the plugin was loaded from (read-only: may be in the program folder).</summary>
    string PluginFolder { get; }

    /// <summary>A writable folder for the plugin's own files (created on demand).</summary>
    string DataFolder { get; }

    /// <summary>PG-05: the plugin's own settings, stored by the player.</summary>
    IPluginSettings Settings { get; }

    /// <summary>Controlled access to playback, playlists and tags.</summary>
    IPlayerApi Player { get; }

    /// <summary>Logger writing to the player's log files, tagged with the plugin id.</summary>
    ILogger Logger { get; }
}

/// <summary>
/// PG-05: per-plugin settings, saved with the player's settings (JSON). Values must be JSON-serialisable.
/// Thread-safe.
/// </summary>
public interface IPluginSettings
{
    /// <summary>The stored value, or <paramref name="defaultValue"/> when missing or not convertible.</summary>
    T GetValue<T>(string key, T defaultValue);

    void SetValue<T>(string key, T value);

    void Remove(string key);

    /// <summary>Raised with the key after <see cref="SetValue{T}"/> or <see cref="Remove"/>.</summary>
    event EventHandler<string>? Changed;
}

/// <summary>PG-05: a plugin that shows a settings page in Settings › Plugins.</summary>
public interface IPluginSettingsPage
{
    /// <summary>Returns an Avalonia <c>Control</c> (typed as object so the SDK does not depend on Avalonia).</summary>
    object CreateSettingsPage();
}

/// <summary>PG-01 Input: decoders for new formats (FM-xx), and optionally playlist formats (PL-03).</summary>
public interface IInputPlugin : IPlugin
{
    IReadOnlyList<IAudioSourceFactory> SourceFactories { get; }

    IReadOnlyList<IPlaylistFormat> PlaylistFormats => [];
}

/// <summary>PG-01 Output: new output devices (listed in Settings › Output). Hosted from P6.</summary>
public interface IOutputPlugin : IPlugin
{
    string Name { get; }

    IAudioOutput CreateOutput();
}

/// <summary>PG-01 DSP: effects in the DSP chain (EQ-05). Hosted from P7.</summary>
public interface IDspPlugin : IPlugin
{
    string Name { get; }

    IDspEffect CreateEffect(AudioFormat format);
}

/// <summary>
/// An effect instance. <see cref="Process"/> runs on the audio thread: it must not allocate, lock, block or
/// throw, and must stay within its time budget (PG-03), or the plugin is disabled.
/// </summary>
public interface IDspEffect : IDisposable
{
    /// <summary>Processes interleaved samples in place.</summary>
    void Process(Span<float> samples, int channels);

    /// <summary>Clears internal state (after a seek or track change).</summary>
    void Reset();
}

/// <summary>PG-01 Visualisation: spectrum/scope/full-screen visuals (UI-07). Hosted from P7.</summary>
public interface IVisualisationPlugin : IPlugin
{
    string Name { get; }

    IVisualisation Create();
}

public interface IVisualisation : IDisposable
{
    /// <summary>Post-DSP audio (UI thread, about 60 times a second). Copy what you need; the span is reused.</summary>
    void OnAudio(ReadOnlySpan<float> samples, AudioFormat format);

    /// <summary>Returns an Avalonia <c>Control</c> that draws the visualisation.</summary>
    object CreateView();
}

/// <summary>PG-01 General: background features using <see cref="IPlayerApi"/> (e.g. scrobblers, loggers).</summary>
public interface IGeneralPlugin : IPlugin
{
}

/// <summary>PG-01 UI panel: a dockable window opened from Main menu › Windows.</summary>
public interface IUiPanelPlugin : IPlugin
{
    string Title { get; }

    /// <summary>Returns an Avalonia <c>Control</c> for the panel.</summary>
    object CreatePanel();
}

/// <summary>PG-01 Cast target: new kinds of cast devices (CN-xx). The contract is completed in P11 (SDK 0.x).</summary>
public interface ICastTargetPlugin : IPlugin
{
    string Name { get; }
}
