using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AudioTastic.Core.Audio.Dsp;
using AudioTastic.Core.Playback;
using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Ui.Equalizer;

/// <summary>
/// EQ-01..EQ-05: state and actions behind the equalizer window. Slider and curve drags change a working copy that is
/// heard at once (<see cref="DspService.PreviewEqualizer"/>) and saved when the drag ends (<see cref="Commit"/>).
/// UI thread only.
/// </summary>
public sealed class EqualizerController : IDisposable
{
    private readonly DspService _dsp;
    private readonly SettingsStore _settings;
    private readonly EqPresetStore _presets;
    private readonly AutoEqService _auto;
    private readonly PlaybackEngine _engine;
    private readonly ILogger<EqualizerController> _logger;
    private EqualizerSettings _working;
    private bool _editing;
    private bool _on;
    private bool _autoOn;

    public EqualizerController(DspService dsp, SettingsStore settings, EqPresetStore presets, AutoEqService auto, PlaybackEngine engine,
        ILogger<EqualizerController> logger)
    {
        _dsp = dsp;
        _settings = settings;
        _presets = presets;
        _auto = auto;
        _engine = engine;
        _logger = logger;
        var current = settings.Current.EqualizerDsp;
        _working = current.Equalizer.Clone();
        (_on, _autoOn) = Flags(current);
        settings.Changed += OnSettingsChanged;
        dsp.Changed += OnDspChanged;
        auto.PresetLoaded += OnAutoPresetLoaded;
    }

    /// <summary>The equalizer as shown (including an uncommitted drag).</summary>
    public EqualizerSettings Current => _working;

    public DspService Dsp => _dsp;

    /// <summary>EQ-02: built-in and user presets.</summary>
    public EqPresetStore Presets => _presets;

    /// <summary>EQ ON: the "equalizer" entry of the DSP chain is enabled.</summary>
    public bool IsOn => _on;

    public bool IsAuto => _autoOn;
    public bool IsParametric => _working.Mode == EqualizerMode.Parametric;

    /// <summary>The curve of the current (working) settings at the playing sample rate.</summary>
    public EqualizerDesign Design => _dsp.DesignFor(_working);

    /// <summary>Something shown changed (UI thread).</summary>
    public event EventHandler? Changed;

    /// <summary>A short message for the main window (preset loaded, import result).</summary>
    public event EventHandler<string>? Message;

    /// <summary>Changes the working copy and lets it be heard; <see cref="Commit"/> saves it.</summary>
    public void Preview(Action<EqualizerSettings> change)
    {
        ArgumentNullException.ThrowIfNull(change);
        _editing = true;
        change(_working);
        _working.Preset = null;
        _dsp.PreviewEqualizer(_working);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Saves the working copy (end of a drag, or a single change).</summary>
    public void Commit()
    {
        _editing = false;
        var copy = _working.Clone();
        _settings.Update(SettingsGroup.EqualizerDsp, s => s.EqualizerDsp.Equalizer = copy);
    }

    /// <summary>Preview and commit in one step.</summary>
    public void Change(Action<EqualizerSettings> change)
    {
        Preview(change);
        Commit();
    }

    public void SetOn(bool on) => _settings.Update(SettingsGroup.EqualizerDsp, s =>
    {
        var chain = s.EqualizerDsp.Chain;
        var index = chain.FindIndex(e => e.Id == DspChainEntry.Equalizer);
        if (index >= 0)
        {
            chain[index] = chain[index] with { Enabled = on };
        }
        else
        {
            chain.Insert(0, new DspChainEntry(DspChainEntry.Equalizer, on));
        }
    });

    public void ToggleOn() => SetOn(!_on);

    public void ToggleAuto() => _settings.Update(SettingsGroup.EqualizerDsp, s => s.EqualizerDsp.AutoEq = !s.EqualizerDsp.AutoEq);

    /// <summary>EQ-04: switches between the ten sliders and the parametric editor (each keeps its own bands).</summary>
    public void ToggleParametric() =>
        Change(eq => eq.Mode = eq.Mode == EqualizerMode.Graphic ? EqualizerMode.Parametric : EqualizerMode.Graphic);

    /// <summary>EQ-01: Flat (all graphic bands and the preamp at 0 dB).</summary>
    public void Reset() => Change(eq =>
    {
        eq.Bands = new double[10];
        eq.PreampDb = 0;
    });

    public void LoadPreset(string name)
    {
        if (_presets.Find(name) is not { } preset)
        {
            return;
        }
        var eq = preset.Settings.Clone();
        eq.Preset = preset.Name;
        // A graphic preset keeps the user's parametric bands (and the other way round).
        if (preset.IsBuiltIn || eq.Mode == EqualizerMode.Graphic)
        {
            eq.Parametric = [.. _working.Parametric];
        }
        _working = eq;
        _editing = false;
        _settings.Update(SettingsGroup.EqualizerDsp, s => s.EqualizerDsp.Equalizer = eq.Clone());
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>EQ-02: the PRESETS menu.</summary>
    public void ShowPresetsMenu(Control source, Window owner)
    {
        var user = _presets.User;
        var entry = _engine.CurrentEntry;
        var path = entry is { IsUrl: false } ? entry.Path : null;
        var genre = entry?.Tags?.Genre;
        var filePreset = path is null ? null : _auto.FilePreset(path);
        var items = new List<object>
        {
            Sub("Load", [.. user.Select(p => Item(p.Name, () => LoadPreset(p.Name), check: p.Name == _working.Preset)),
                .. user.Count > 0 ? new object[] { new Separator() } : [],
                .. EqPresetStore.BuiltIn.Select(p => Item(p.Name, () => LoadPreset(p.Name), check: p.Name == _working.Preset))]),
            Item("Save as…", () => _ = SaveAsAsync(owner)),
            Sub("Delete", [.. user.Select(p => Item(p.Name, () => _presets.Delete(p.Name)))], user.Count > 0),
            Item("Import Winamp preset (.eqf, .q1)…", () => _ = ImportAsync(owner)),
            Item("Reset to flat", Reset),
            new Separator(),
            Sub(filePreset is null ? "Assign to this file" : $"Assign to this file ({filePreset})",
                AssignItems(filePreset, name => _auto.AssignToFile(path!, name)), path is not null),
            Sub(genre is null ? "Assign to genre" : $"Assign to genre '{genre}'",
                AssignItems(genre is null ? null : _auto.PresetFor(string.Empty, genre), name => _auto.AssignToGenre(genre!, name)),
                !string.IsNullOrWhiteSpace(genre)),
        };
        new ContextMenu { ItemsSource = items }.Open(source);

        List<object> AssignItems(string? current, Action<string?> assign) =>
        [
            Item("None", () => assign(null), check: current is null),
            new Separator(),
            .. _presets.All.Select(p => Item(p.Name, () => assign(p.Name), check: p.Name == current)),
        ];
    }

    /// <summary>EQ-05: the DSP chain window.</summary>
    public void OpenDspChain(Window owner) => DspChainWindow.ShowFor(owner, _dsp, _settings);

    public void Dispose()
    {
        _settings.Changed -= OnSettingsChanged;
        _dsp.Changed -= OnDspChanged;
        _auto.PresetLoaded -= OnAutoPresetLoaded;
    }

    private async Task SaveAsAsync(Window owner)
    {
        var name = await InputWindow.ShowAsync(owner, "Save preset", "Preset name:", _working.Preset);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }
        _presets.Save(name, _working);
        _working.Preset = name.Trim();
        Commit();
        Message?.Invoke(this, $"Preset '{name.Trim()}' saved");
    }

    private async Task ImportAsync(Window owner)
    {
        var files = await owner.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import Winamp EQ presets",
            AllowMultiple = true,
            FileTypeFilter = [new FilePickerFileType("Winamp EQ presets") { Patterns = ["*.eqf", "*.q1"] }, FilePickerFileTypes.All],
        });
        var count = 0;
        foreach (var path in files.Select(f => f.TryGetLocalPath()).OfType<string>())
        {
            try
            {
                count += _presets.Import(path).Count;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                _logger.LogWarning(ex, "Could not import {Path}", path);
                Message?.Invoke(this, $"Could not import {Path.GetFileName(path)}: {ex.Message}");
            }
        }
        if (count > 0)
        {
            Message?.Invoke(this, count == 1 ? "Imported 1 preset" : $"Imported {count} presets");
        }
    }

    private void OnSettingsChanged(object? sender, SettingsChangedEventArgs e)
    {
        if (e.Group != SettingsGroup.EqualizerDsp)
        {
            return;
        }
        var eq = e.Settings.EqualizerDsp.Equalizer.Clone();
        var flags = Flags(e.Settings.EqualizerDsp);
        Dispatcher.UIThread.Post(() =>
        {
            (_on, _autoOn) = flags;
            if (!_editing)
            {
                _working = eq;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    private static (bool On, bool Auto) Flags(EqualizerDspSettings s) =>
        (s.Chain.Any(e => e.Id == DspChainEntry.Equalizer && e.Enabled), s.AutoEq);

    private void OnDspChanged(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() => Changed?.Invoke(this, EventArgs.Empty));

    private void OnAutoPresetLoaded(object? sender, string name) => Dispatcher.UIThread.Post(() => Message?.Invoke(this, $"Auto EQ: {name}"));

    private static MenuItem Item(string header, Action action, bool enabled = true, bool? check = null)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled };
        if (check is { } isChecked)
        {
            item.ToggleType = MenuItemToggleType.CheckBox;
            item.IsChecked = isChecked;
        }
        item.Click += (_, _) => action();
        return item;
    }

    private static MenuItem Sub(string header, IEnumerable<object> items, bool enabled = true) =>
        new() { Header = header, ItemsSource = items.ToList(), IsEnabled = enabled };
}
