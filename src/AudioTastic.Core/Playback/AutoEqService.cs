using System.Text;
using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Data.Cache;
using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Core.Playback;

/// <summary>
/// EQ-03: with AUTO on, loads a preset when a track starts: the preset assigned to that file (cache.db kind
/// "autoeq"), else the one assigned to its genre (Settings › Equalizer &amp; DSP), else the settings stay.
/// Loading a preset also switches the equalizer on.
/// </summary>
public sealed class AutoEqService : IDisposable
{
    public const string CacheKind = "autoeq";
    private const int DataVersion = 1;

    private readonly IPlayerController _player;
    private readonly SettingsStore _settings;
    private readonly EqPresetStore _presets;
    private readonly CacheStore? _cache;
    private readonly ILogger _logger;

    public AutoEqService(IPlayerController player, SettingsStore settings, EqPresetStore presets, CacheStore? cache = null,
        ILogger<AutoEqService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(player);
        _player = player;
        _settings = settings;
        _presets = presets;
        _cache = cache;
        _logger = logger ?? NullLogger<AutoEqService>.Instance;
        player.TrackChanged += OnTrackChanged;
    }

    /// <summary>Raised with the preset name after AUTO loaded a preset.</summary>
    public event EventHandler<string>? PresetLoaded;

    /// <summary>The preset assigned to <paramref name="path"/>, or null.</summary>
    public string? FilePreset(string path)
    {
        if (_cache is null || path.Contains("://", StringComparison.Ordinal) || !File.Exists(path))
        {
            return null;
        }
        return _cache.TryGet(FileKey.FromFile(path), CacheKind, DataVersion) is { Length: > 0 } data ? Encoding.UTF8.GetString(data) : null;
    }

    /// <summary>Assigns a preset to a file (null removes the rule).</summary>
    public void AssignToFile(string path, string? preset)
    {
        if (_cache is null || path.Contains("://", StringComparison.Ordinal) || !File.Exists(path))
        {
            return;
        }
        if (preset is null)
        {
            _cache.Delete(path, CacheKind);
        }
        else
        {
            _cache.Put(FileKey.FromFile(path), CacheKind, DataVersion, Encoding.UTF8.GetBytes(preset));
        }
    }

    /// <summary>Assigns a preset to a genre (null removes the rule).</summary>
    public void AssignToGenre(string genre, string? preset)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(genre);
        _settings.Update(SettingsGroup.EqualizerDsp, s =>
        {
            foreach (var key in s.EqualizerDsp.GenrePresets.Keys.Where(k => string.Equals(k, genre.Trim(), StringComparison.OrdinalIgnoreCase)).ToList())
            {
                s.EqualizerDsp.GenrePresets.Remove(key);
            }
            if (preset is not null)
            {
                s.EqualizerDsp.GenrePresets[genre.Trim()] = preset;
            }
        });
    }

    /// <summary>The preset AUTO would load for a track (file rule, then genre rule).</summary>
    public string? PresetFor(string path, string? genre)
    {
        if (FilePreset(path) is { } file)
        {
            return file;
        }
        if (string.IsNullOrWhiteSpace(genre))
        {
            return null;
        }
        // Compare by hand: the settings are cloned through JSON, which drops the dictionary's comparer.
        return _settings.Current.EqualizerDsp.GenrePresets
            .FirstOrDefault(p => string.Equals(p.Key, genre.Trim(), StringComparison.OrdinalIgnoreCase)).Value;
    }

    /// <summary>Loads a preset into the equalizer and switches it on.</summary>
    public bool Load(string presetName)
    {
        if (_presets.Find(presetName) is not { } preset)
        {
            return false;
        }
        _settings.Update(SettingsGroup.EqualizerDsp, s =>
        {
            var eq = preset.Settings.Clone();
            eq.Preset = preset.Name;
            s.EqualizerDsp.Equalizer = eq;
            var chain = s.EqualizerDsp.Chain;
            var index = chain.FindIndex(e => e.Id == DspChainEntry.Equalizer);
            if (index >= 0)
            {
                chain[index] = chain[index] with { Enabled = true };
            }
            else
            {
                chain.Insert(0, new DspChainEntry(DspChainEntry.Equalizer, true));
            }
        });
        return true;
    }

    public void Dispose() => _player.TrackChanged -= OnTrackChanged;

    private void OnTrackChanged(object? sender, TrackChangedEventArgs e)
    {
        if (!_settings.Current.EqualizerDsp.AutoEq || e.Entry is not { } entry)
        {
            return;
        }
        try
        {
            if (PresetFor(entry.Path, entry.Tags?.Genre) is { } name && name != _settings.Current.EqualizerDsp.Equalizer.Preset && Load(name))
            {
                _logger.LogInformation("Auto EQ: loaded preset {Preset} for {Path}", name, entry.Path);
                PresetLoaded?.Invoke(this, name);
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Auto EQ could not read the rules for {Path}", entry.Path);
        }
    }
}
