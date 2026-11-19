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
/// FM-08: the MIDI input plugin. The SoundFont is the .sf2 chosen on the settings page (setting "soundFont"), else
/// GeneralUser GS downloaded on request into the plugin's data folder (it is not bundled any more). It is loaded once
/// and shared by every MIDI file.
/// </summary>
public sealed class MidiInputPlugin : IInputPlugin, IPluginSettingsPage
{
    internal const string SoundFontKey = "soundFont";
    internal const string BundledFileName = SoundFontDownload.FileName;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

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

    /// <summary>Where GeneralUser GS is downloaded to (the plugin's data folder).</summary>
    internal string DownloadFolder => Path.Combine(_context?.DataFolder ?? AppContext.BaseDirectory, "soundfont");

    /// <summary>GeneralUser GS: the download, or a copy bundled by an older build when that is all there is.</summary>
    internal string BundledPath
    {
        get
        {
            var downloaded = Path.Combine(DownloadFolder, BundledFileName);
            var oldBundled = Path.Combine(_context?.PluginFolder ?? AppContext.BaseDirectory, "soundfont", BundledFileName);
            return !File.Exists(downloaded) && File.Exists(oldBundled) ? oldBundled : downloaded;
        }
    }

    /// <summary>The SoundFont in use: the user's choice when it exists, else GeneralUser GS.</summary>
    internal string SoundFontPath =>
        _context?.Settings.GetValue<string?>(SoundFontKey, null) is { Length: > 0 } chosen && File.Exists(chosen) ? chosen : BundledPath;

    private SoundFont LoadSoundFont()
    {
        var path = SoundFontPath;
        if (!File.Exists(path))
        {
            throw new AudioSourceException("No SoundFont yet: download GeneralUser GS or choose a .sf2 file in Settings › Plugins › MIDI synthesizer › Settings.");
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
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brushes.Gray, FontSize = 12, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left };
        var download = new Button { Content = $"Download GeneralUser GS ({SoundFontDownload.Size / 1_000_000} MB)" };
        void Update()
        {
            var haveDefault = File.Exists(BundledPath);
            current.Text = SoundFontPath == BundledPath
                ? haveDefault ? "GeneralUser GS v2.0.3 by S. Christian Collins" : "None yet: MIDI files need a SoundFont."
                : SoundFontPath;
            download.IsVisible = !haveDefault;
        }
        Update();
        download.Click += async (_, _) =>
        {
            download.IsEnabled = false;
            error.Text = null;
            var progress = new Progress<double>(p => status.Text = $"Downloading… {p:P0}");
            try
            {
                await new SoundFontDownload(Http).DownloadAsync(DownloadFolder, progress, CancellationToken.None);
                status.Text = "Downloaded and checked.";
                _factory.Reset();
                _context?.Logger.LogInformation("Downloaded GeneralUser GS to {Folder}", DownloadFolder);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or TaskCanceledException or UnauthorizedAccessException)
            {
                status.Text = null;
                error.Text = $"The download failed: {ex.Message}";
            }
            download.IsEnabled = true;
            Update();
        };

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
        var bundled = new Button { Content = "Use GeneralUser GS" };
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
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { download, choose, bundled } },
                status,
                error,
                new TextBlock
                {
                    Text = "GeneralUser GS v2.0.3 by S. Christian Collins is free to use (its licence is saved next to it). It is downloaded from "
                        + "AudioTastic's GitHub releases only when you click Download.",
                    Foreground = Brushes.Gray, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left,
                },
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
