using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App.Ui;

/// <summary>
/// User manual screenshots (docs/manual). Debug builds only: with the environment variable AUDIOTASTIC_MANUAL_SHOTS set
/// to a folder, the player uses the silent null audio device, plays the files it was started with, opens every window,
/// dialog, menu and settings page in turn, renders each one's own content to a PNG (2× scale) and exits. Nothing is
/// captured from the screen, so other programs never appear. Run it with a portable copy and demo files
/// (scripts/make_manual.py does all of it).
/// </summary>
public sealed partial class PlayerShell
{
#if DEBUG
    internal static string? ManualCaptureFolder { get; } =
        Environment.GetEnvironmentVariable("AUDIOTASTIC_MANUAL_SHOTS") is { Length: > 0 } folder ? folder : null;

    private const double ShotScale = 2;

    private async Task CaptureManualAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        string Png(string name) => Path.Combine(folder, name + ".png");
        ContextMenu? lastMenu = null;
        using var menus = MenuBase.OpenedEvent.AddClassHandler<ContextMenu>((menu, _) => lastMenu = menu);
        try
        {
            await Task.Delay(4000);
            await _engine.SeekAsync(TimeSpan.FromSeconds(62));
            await Task.Delay(5000); // waveform, art, lyrics and the spectrum settle

            Snap(_main!, Png("02-main-window"));
            Snap(_equalizer!, Png("14-equalizer"));
            Snap(_playlist!, Png("05-playlist"));
            Snap(_artWindow!, Png("19-art-and-lyrics"));

            async Task Menu(Action open, string name)
            {
                lastMenu = null;
                open();
                for (var i = 0; i < 30 && lastMenu is null; i++)
                {
                    await Task.Delay(100);
                }
                await Task.Delay(400);
                if (lastMenu is { } menu)
                {
                    Snap(menu, Png(name));
                    menu.Close();
                    await Task.Delay(200);
                }
                else
                {
                    _logger.LogWarning("Manual: no menu for {Name}", name);
                }
            }

            async Task Dialog(Action open, string name, Action<Window>? prepare = null)
            {
                var before = _desktop!.Windows.ToHashSet();
                open();
                Window? window = null;
                for (var i = 0; i < 60 && window is null; i++)
                {
                    await Task.Delay(100);
                    window = _desktop.Windows.FirstOrDefault(w => !before.Contains(w) && w.IsVisible);
                }
                if (window is null)
                {
                    _logger.LogWarning("Manual: no window for {Name}", name);
                    return;
                }
                await Task.Delay(800);
                prepare?.Invoke(window);
                await Task.Delay(prepare is null ? 0 : 600);
                Snap(window, Png(name));
                window.Close();
                await Task.Delay(300);
            }

            await Menu(() => ShowMainMenu(_main!), "04-main-menu");

            // Playlist: menus, search, selection-based dialogs.
            var list = _playlist!.GetVisualDescendants().OfType<ListBox>().First();
            await Menu(() => _playlists.ShowAddMenu(_playlist!), "06-playlist-add-menu");
            await Menu(() => _playlists.ShowRemoveMenu(_playlist!), "07-playlist-remove-menu");
            await Menu(() => _playlists.ShowSelectMenu(_playlist!), "08-playlist-select-menu");
            await Menu(() => _playlists.ShowSortMenu(_playlist!), "09-playlist-sort-menu");
            await Menu(() => _playlists.ShowListMenu(_playlist!), "10-playlist-list-menu");
            await Menu(() => _playlists.ShowTabMenu(_playlist!, _playlists.Active, () => { }), "12-playlist-tab-menu");
            list.Selection.Clear();
            list.Selection.Select(2);
            await Task.Delay(300);
            await Menu(() => _playlists.ShowRowMenu(list), "11-playlist-row-menu");
            await Dialog(() => _playlists.ShowFileInfo(list, current: false), "21-file-info");
            await Dialog(() => _playlists.EditTags(list), "22-edit-tags");
            list.Selection.Select(3);
            list.Selection.Select(4);
            await Task.Delay(300);
            await Dialog(() => _playlists.Convert(list), "24-convert");
            list.Selection.Clear();
            Execute("search", _playlist!);
            await Task.Delay(300);
            if (_playlist!.GetVisualDescendants().OfType<TextBox>().FirstOrDefault() is { } search)
            {
                search.Text = "nova";
                await Task.Delay(800);
                Snap(_playlist!, Png("13-playlist-search"));
                search.Text = string.Empty;
                await Task.Delay(300);
            }

            // Mini player.
            ToggleMiniPlayer();
            await Task.Delay(1500);
            Snap(_mini!, Png("03-mini-player"));
            ToggleMiniPlayer();
            await Task.Delay(1500);

            // Equalizer, presets, parametric mode, DSP chain, VST3 picker.
            await Menu(() => _eq.ShowPresetsMenu(_equalizer!, _equalizer!), "16-equalizer-presets-menu");
            _eq.ToggleParametric();
            await Task.Delay(800);
            Snap(_equalizer!, Png("15-equalizer-parametric"));
            await Dialog(() => _ = new Equalizer.ParametricBandsWindow(_eq).ShowDialog(_equalizer!), "20-parametric-bands");
            _eq.ToggleParametric();
            await Task.Delay(500);
            await Dialog(() => _eq.OpenDspChain(_equalizer!), "17-dsp-chain");
            var dsp = AppHost.Services?.GetService(typeof(AudioTastic.Core.Audio.Dsp.DspService)) as AudioTastic.Core.Audio.Dsp.DspService;
            if (dsp is not null)
            {
                await Dialog(() => new Equalizer.Vst3PickerWindow(_eq.Vst3, dsp).Show(_main!), "18-vst3-picker",
                    _ => { }); // the scan of the configured folders fills it
            }

            // Playback dialogs.
            await Dialog(() => Execute("speed", _main!), "25-speed");
            await Dialog(() => Execute("sleepTimer", _main!), "26-sleep-timer");
            await Dialog(() => Execute("openUrl", _main!), "27-open-url");
            await Dialog(() => Execute("about", _main!), "28-about");

            // Settings: the window, then every page whole.
            await Dialog(() => Execute("settings", _main!), "30-settings-window", window =>
            {
                if (window is SettingsWindow settings)
                {
                    settings.SelectGroup(0);
                }
            });
            ShowSettings();
            await Task.Delay(1000);
            if (_settingsWindow is { } settingsWindow)
            {
                for (var i = 0; i < SettingsWindow.Groups.Length; i++)
                {
                    settingsWindow.SelectGroup(i);
                    await Task.Delay(900);
                    if (settingsWindow.PageContent is { } page)
                    {
                        var slug = SettingsWindow.Groups[i].Title.ToLowerInvariant().Replace(" & ", "-").Replace(' ', '-');
                        Snap(page, Png($"{31 + i}-settings-{slug}"));
                    }
                }
                settingsWindow.Close();
                await Task.Delay(300);
            }

            // Settings backup: export this portable copy's settings, then show the import choices.
            var backup = Path.Combine(Path.GetTempPath(), $"audiotastic-manual-{Environment.ProcessId}.atbackup");
            SaveForBackup();
            await Task.Run(() => AudioTastic.Data.Backup.BackupService.Export(_paths, backup,
                typeof(PlayerShell).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "?"));
            var info = AudioTastic.Data.Backup.BackupService.Read(backup);
            await Dialog(() => _ = ImportBackupWindow.AskAsync(_main!, info), "29-import-backup");
            File.Delete(backup);
            _logger.LogInformation("Manual screenshots written to {Folder}", folder);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Manual screenshots failed");
        }
        Exit();
    }

    /// <summary>
    /// Renders a visual's own content (not the screen) to a PNG at <see cref="ShotScale"/>, transparent where the visual
    /// draws nothing (scripts/make_manual_screenshots.py adds backgrounds and builds the combined player picture). The
    /// user's name in paths is replaced by "you" first.
    /// </summary>
    private static void Snap(Visual visual, string path)
    {
        Anonymise(visual);
        var size = visual.Bounds.Size;
        using var target = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(size.Width * ShotScale), (int)Math.Ceiling(size.Height * ShotScale)),
            new Vector(96 * ShotScale, 96 * ShotScale));
        target.Render(visual);
        target.Save(path, PngBitmapEncoderOptions.Default);
    }

    private static void Anonymise(Visual visual)
    {
        // The profile folder, e.g. C:\Users\<name>; its name need not match Environment.UserName.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\', '/');
        var parent = Path.GetDirectoryName(profile);
        if (string.IsNullOrEmpty(profile) || string.IsNullOrEmpty(parent))
        {
            return;
        }
        var neutral = Path.Combine(parent, "you");
        string Clean(string text) => text.Replace(profile, neutral, StringComparison.OrdinalIgnoreCase);
        foreach (var item in visual.GetVisualDescendants().Prepend(visual))
        {
            switch (item)
            {
                case TextBox { Text: { } text } box when Clean(text) != text:
                    box.Text = Clean(text);
                    break;
                case TextBlock { Text: { } text } block when block.Inlines is not { Count: > 0 } && Clean(text) != text:
                    block.Text = Clean(text);
                    break;
            }
        }
    }
#else
    internal static string? ManualCaptureFolder => null;

    private static Task CaptureManualAsync(string folder) => Task.CompletedTask;
#endif
}
