using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MeltySynth;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Input.Midi;

/// <summary>
/// FM-08: the MIDI input plugin. The SoundFont is the bundled GeneralUser GS, or the .sf2 chosen on the settings page
/// (setting "soundFont"). It is loaded once and shared by every MIDI file.
/// </summary>
public sealed class MidiInputPlugin : IInputPlugin, IPluginSettingsPage
{
    internal const string SoundFontKey = "soundFont";
    internal const string BundledFileName = "GeneralUser-GS.sf2";

    private readonly MidiSourceFactory _factory;
    private IPluginContext? _context;

    public MidiInputPlugin() => _factory = new MidiSourceFactory(LoadSoundFont);

    public IReadOnlyList<IAudioSourceFactory> SourceFactories => [_factory];

    public void Initialize(IPluginContext context)
    {
        _context = context;
        context.Settings.Changed += OnSettingsChanged;
    }

    public void Dispose()
    {
        if (_context is not null)
        {
            _context.Settings.Changed -= OnSettingsChanged;
        }
    }

    /// <summary>The bundled SoundFont inside the plugin folder.</summary>
    internal string BundledPath => Path.Combine(_context?.PluginFolder ?? AppContext.BaseDirectory, "soundfont", BundledFileName);

    /// <summary>The SoundFont in use: the user's choice when it exists, else the bundled one.</summary>
    internal string SoundFontPath =>
        _context?.Settings.GetValue<string?>(SoundFontKey, null) is { Length: > 0 } chosen && File.Exists(chosen) ? chosen : BundledPath;

    private SoundFont LoadSoundFont()
    {
        var path = SoundFontPath;
        if (!File.Exists(path))
        {
            throw new AudioSourceException("No SoundFont: choose a .sf2 file in Settings › Plugins › MIDI synthesizer.");
        }
        _context?.Logger.LogInformation("Loading SoundFont {Path}", path);
        try
        {
            return new SoundFont(path);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or EndOfStreamException)
        {
            throw new AudioSourceException($"The SoundFont '{Path.GetFileName(path)}' could not be loaded: {ex.Message}", ex);
        }
    }

    private void OnSettingsChanged(object? sender, string key)
    {
        if (key == SoundFontKey)
        {
            _factory.Reset();
        }
    }

    // ---- PG-05 settings page --------------------------------------------------------------------------

    public object CreateSettingsPage()
    {
        var current = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left };
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.IndianRed, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left };
        void Update() => current.Text = SoundFontPath == BundledPath
            ? $"GeneralUser GS v2.0.3 by S. Christian Collins (bundled){(File.Exists(BundledPath) ? "" : " — missing from this build")}"
            : SoundFontPath;
        Update();

        var choose = new Button { Content = "Choose SoundFont…" };
        choose.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(choose) is not { } top)
            {
                return;
            }
            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Choose a SoundFont",
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("SoundFont 2") { Patterns = ["*.sf2"] }],
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
            {
                return;
            }
            try
            {
                _ = new SoundFont(path); // validate before saving
                _context!.Settings.SetValue<string?>(SoundFontKey, path);
                error.Text = null;
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or NotSupportedException or EndOfStreamException)
            {
                error.Text = $"Not a valid SoundFont: {ex.Message}";
            }
            Update();
        };
        var bundled = new Button { Content = "Use the bundled SoundFont" };
        bundled.Click += (_, _) =>
        {
            _context!.Settings.Remove(SoundFontKey);
            error.Text = null;
            Update();
        };
        Avalonia.Automation.AutomationProperties.SetName(current, "SoundFont in use");
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "SoundFont", FontWeight = FontWeight.SemiBold },
                current,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { choose, bundled } },
                error,
                new TextBlock
                {
                    Text = "Applies to the next MIDI file that starts.",
                    Foreground = Brushes.Gray, FontSize = 12,
                },
            },
        };
    }
}

/// <summary>Opens .mid/.midi/.kar files; the SoundFont is loaded on first use and reused.</summary>
internal sealed class MidiSourceFactory(Func<SoundFont> loadSoundFont) : IAudioSourceFactory
{
    private readonly Lock _lock = new();
    private SoundFont? _soundFont;

    public string Name => "MIDI";
    public IReadOnlyCollection<string> Extensions { get; } = [".mid", ".midi", ".kar", ".rmi"];

    public bool CanOpen(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public IAudioSource Open(string path)
    {
        SoundFont soundFont;
        lock (_lock)
        {
            soundFont = _soundFont ??= loadSoundFont();
        }
        return new MidiSource(path, soundFont);
    }

    /// <summary>Forgets the loaded SoundFont (the setting changed).</summary>
    public void Reset()
    {
        lock (_lock)
        {
            _soundFont = null;
        }
    }
}
