using Avalonia;
using AudioTastic.Core.Audio;
using AudioTastic.Core.Audio.Output;
using AudioTastic.Core.Diagnostics;
using AudioTastic.Core.Playback;
using AudioTastic.Data;
using AudioTastic.Data.Cache;
using AudioTastic.Data.Settings;
using AudioTastic.App.Plugins;
using AudioTastic.Plugins.Host;
using AudioTastic.Sdk.Audio;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App;

internal static class Program
{
    /// <summary>
    /// CO-01 start-up order: data paths → logging → crash handlers → settings → cache → services → UI.
    /// Paths and logging come first so that every later failure is logged and reported.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        var platform = PlatformServicesFactory.Create();
        // SI-08: --help, --version and mistakes are answered on the console the program was started from.
        var parsed = AudioTastic.App.CommandLine.Parse(args);
        if (parsed.Options is not { } options)
        {
            platform.AttachToParentConsole();
            Console.WriteLine();
            return parsed.Result.Invoke();
        }
        if (options.Unregister)
        {
            return Unregister(platform);
        }
        var paths = AppPaths.ForCurrentProcess(args).EnsureCreated();

        // SI-06: a second start hands its files to the running player and ends (before logging, whose file the
        // running player holds).
        using var instance = AcquireOrForward(paths, options, platform, out var forwarded);
        if (instance is null)
        {
            return forwarded ? 0 : 1;
        }
        if (options.OnlyQuit)
        {
            return 0; // --quit with no player running: nothing to do, and nothing to start
        }
        // SI-02 / SI-07: an app identity before any window exists, so Windows' media controls name AudioTastic.
        try
        {
            platform.SetAppIdentity(AppId, "AudioTastic", Path.Combine(AppContext.BaseDirectory, "audiotastic.png"), writeToSystem: !paths.IsPortable);
        }
#pragma warning disable CA1031 // Cosmetic: never stop the start-up over it.
        catch (Exception)
#pragma warning restore CA1031
        {
        }
        var inbox = new AudioTastic.App.Integration.InstanceInbox();
        instance.Listen(inbox.Post);

        using var logging = Logging.Create(paths, LogLevelSetting.Information);
        var log = logging.Factory.CreateLogger("AudioTastic");
        var crashReporter = new CrashReporter(paths, logging.Factory.CreateLogger<CrashReporter>());
        crashReporter.InstallProcessHandlers(beforeExit: logging.Flush);

        log.LogInformation("AudioTastic starting; data folder {Root} (portable: {Portable})", paths.Root, paths.IsPortable);

        try
        {
            var settings = new SettingsStore(paths, logging.Factory.CreateLogger<SettingsStore>());
            logging.SetLevel(settings.Current.General.LogLevel);
            settings.Changed += (_, e) =>
            {
                if (e.Group == SettingsGroup.General)
                {
                    logging.SetLevel(e.Settings.General.LogLevel);
                }
            };

            var cache = new CacheStore(paths, logging.Factory.CreateLogger<CacheStore>());

            using var services = new ServiceCollection()
                .AddSingleton(options)
                .AddSingleton(paths)
                .AddSingleton(logging)
                .AddSingleton(logging.Factory)
                .AddSingleton(typeof(ILogger<>), typeof(Logger<>))
                .AddSingleton(crashReporter)
                .AddSingleton(settings)
                .AddSingleton(cache)
                .AddSingleton(platform)
                .AddSingleton(inbox)
                .AddSingleton<AudioTastic.App.Integration.ShellIntegrationService>()
                .AddSingleton<AudioTastic.App.Integration.MediaControlsService>()
                .AddSingleton<AudioTastic.App.Integration.HotkeyManager>()
                .AddSingleton<AudioTastic.App.Integration.TaskbarService>()
                .AddSingleton<AudioTastic.App.Integration.NotificationService>()
                .AddSingleton<AudioTastic.App.Integration.CastService>()
                // P4 plugins (PG-01/03/05): decoders come from enabled input plugins, read live by every consumer.
                .AddSingleton(sp => new PluginHost(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<ILoggerFactory>()))
                .AddSingleton<IEnumerable<IAudioSourceFactory>>(sp => sp.GetRequiredService<PluginHost>().LiveSourceFactories)
                .AddSingleton(sp => new PlayerApi(sp.GetRequiredService<PlaybackEngine>(), sp.GetRequiredService<AudioTastic.Core.Playlists.PlaylistService>(),
                    sp.GetRequiredService<AudioTastic.Metadata.TagService>(), () => sp.GetRequiredService<AudioTastic.App.Ui.Playlist.PlaylistController>()))
                .AddSingleton(sp => new MiniaudioOutput(sp.GetRequiredService<ILogger<MiniaudioOutput>>()))
                // PB-04 / PG-01: the engine plays through the router: built-in devices plus output plugins' devices.
                .AddSingleton<IAudioOutput>(sp => new AudioTastic.Plugins.Host.OutputRouter(sp.GetRequiredService<MiniaudioOutput>(),
                    sp.GetRequiredService<PluginHost>(), sp.GetRequiredService<ILogger<AudioTastic.Plugins.Host.OutputRouter>>()))
                .AddSingleton(sp => new AudioTastic.Core.Playback.ResumeStore(sp.GetRequiredService<AudioTastic.Data.Cache.CacheStore>()))
                .AddSingleton<PlaybackEngine>()
                .AddSingleton<AudioTastic.Core.Playback.SleepTimerService>()
                .AddSingleton<IPlayerController>(sp => sp.GetRequiredService<PlaybackEngine>())
                // P2 player UI: skins (UI-03) and the shell that owns the skinned windows.
                .AddSingleton<AudioTastic.App.Skins.SkinManager>()
                // P3 playlists (PL-01..07, FM-09) and tags (MD-01).
                .AddSingleton(sp => { var s = new AudioTastic.Core.Playlists.PlaylistService(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<ILogger<AudioTastic.Core.Playlists.PlaylistService>>(), sp.GetRequiredService<PluginHost>().LivePlaylistFormats); s.Load(); return s; })
                .AddSingleton<AudioTastic.Metadata.TagService>()
                .AddSingleton<AudioTastic.Metadata.TagLoader>()
                // P9 tag editing (MD-01): writes wait while the player has the file open.
                .AddSingleton<AudioTastic.Metadata.TagWriter>()
                .AddSingleton<AudioTastic.Metadata.ArtService>() // MD-02
                .AddSingleton<AudioTastic.App.Ui.ArtCoordinator>()
                .AddSingleton<AudioTastic.Metadata.LyricsService>() // MD-04
                .AddSingleton<AudioTastic.Metadata.FileInfoReader>() // MD-05
                .AddSingleton<AudioTastic.Metadata.OnlineLookupService>() // MD-03
                .AddSingleton<AudioTastic.App.Ui.LyricsCoordinator>()
                .AddSingleton(sp => new AudioTastic.Metadata.TagWriteService(sp.GetRequiredService<AudioTastic.Metadata.TagWriter>(),
                    sp.GetRequiredService<PlaybackEngine>().IsDecoding, sp.GetRequiredService<ILogger<AudioTastic.Metadata.TagWriteService>>()))
                // P5 analysis (AN-01), waveforms (CO-07) and ReplayGain (PB-03).
                .AddSingleton<AudioTastic.Analysis.AnalysisService>()
                .AddSingleton<AudioTastic.Analysis.ReplayGainService>()
                .AddSingleton<AudioTastic.Analysis.BeatInfoService>()
                .AddSingleton<AudioTastic.App.Ui.AnalysisCoordinator>()
                .AddSingleton<AudioTastic.App.Ui.Playlist.PlaylistController>()
                // EQ-01..EQ-05: DSP chain, presets, auto EQ and the equalizer window's controller.
                .AddSingleton(sp => new AudioTastic.Core.Audio.Dsp.DspService(sp.GetRequiredService<PlaybackEngine>().Graph,
                    sp.GetRequiredService<SettingsStore>(), sp.GetRequiredService<ILogger<AudioTastic.Core.Audio.Dsp.DspService>>()))
                .AddSingleton(sp => new AudioTastic.Core.Audio.Dsp.EqPresetStore(sp.GetRequiredService<AppPaths>().PresetsFolder))
                .AddSingleton(sp => new AudioTastic.Core.Playback.AutoEqService(sp.GetRequiredService<PlaybackEngine>(), sp.GetRequiredService<SettingsStore>(),
                    sp.GetRequiredService<AudioTastic.Core.Audio.Dsp.EqPresetStore>(), sp.GetRequiredService<AudioTastic.Data.Cache.CacheStore>(),
                    sp.GetRequiredService<ILogger<AudioTastic.Core.Playback.AutoEqService>>()))
                .AddSingleton<AudioTastic.App.Ui.Equalizer.EqualizerController>()
                .AddSingleton<AudioTastic.App.Ui.PlayerShell>()
                .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

            AppHost.Services = services;

            // PG-01: plugins are loaded before the UI so that start-up files can use every decoder.
            var plugins = services.GetRequiredService<PluginHost>();
            plugins.AttachPlayer(services.GetRequiredService<PlayerApi>());
            plugins.LoadAll();

            // EQ-05 / PG-01: DSP plugins' effects join the chain; a plugin's effects leave it before the plugin unloads.
            var dsp = services.GetRequiredService<AudioTastic.Core.Audio.Dsp.DspService>();
            dsp.SetPluginEffects(() => PluginEffects(plugins));
            plugins.PluginUnloading += (_, record) => dsp.RemovePlugin(record.Id);
            plugins.PluginsChanged += (_, _) => dsp.RefreshPlugins();
            _ = services.GetRequiredService<AudioTastic.Core.Playback.AutoEqService>(); // EQ-03 listens from the start

            // AN-02 / XF-02: the engine asks the analysis for beats when it plans a beat-matched transition.
            services.GetRequiredService<PlaybackEngine>().BeatProvider = services.GetRequiredService<AudioTastic.Analysis.BeatInfoService>();
            services.GetRequiredService<PlaybackEngine>().CrossfadeAligner =
                new AudioTastic.Analysis.CrossfadeAligner(services.GetRequiredService<IEnumerable<IAudioSourceFactory>>());

            // PB-03: the engine asks the ReplayGain service for each track's gain.
            services.GetRequiredService<PlaybackEngine>().ReplayGainProvider = services.GetRequiredService<AudioTastic.Analysis.ReplayGainService>();

            // SI-05: the file manager's context menu follows the setting, this program's location and the plugins' file types.
            services.GetRequiredService<AudioTastic.App.Integration.ShellIntegrationService>().Start();

            var exitCode = BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            log.LogInformation("AudioTastic exited with code {Code}", exitCode);
            return exitCode;
        }
#pragma warning disable CA1031 // Top-level handler: report every crash.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            crashReporter.Write(ex, "UI thread / start-up");
            return 1;
        }
    }

    /// <summary>
    /// SI-06: becomes the running instance, or sends this start's files to the one already running. A player that is
    /// still starting or just exiting gets up to 15 seconds; then this start gives up (<paramref name="forwarded"/> false).
    /// </summary>
    /// <summary>
    /// P10 installers (--unregister): removes the context menu entries, file type registration, Start menu shortcut and
    /// app identity, so uninstalling leaves nothing behind in the registry. Settings and playlists are left alone.
    /// </summary>
    private static int Unregister(AudioTastic.Platform.IPlatformServices platform)
    {
        var failures = 0;
        void Step(Action action)
        {
            try
            {
                action();
            }
#pragma warning disable CA1031 // Best effort: remove as much as possible.
            catch (Exception)
#pragma warning restore CA1031
            {
                failures++;
            }
        }
        Step(() => platform.Shell.SetContextMenu(null));
        Step(() => platform.Shell.SetFileAssociations(null));
        Step(() => platform.Shell.SetStartMenuShortcut(null, AppId));
        Step(() => platform.RemoveAppIdentity(AppId));
        return failures == 0 ? 0 : 1;
    }

    /// <summary>SI-02 / SI-07: the Windows AppUserModelID (also for the future installer's Start menu shortcut).</summary>
    public const string AppId = "BigApps.AudioTastic";

    private static AudioTastic.App.Integration.SingleInstance? AcquireOrForward(AppPaths paths, StartupOptions options,
        AudioTastic.Platform.IPlatformServices platform, out bool forwarded)
    {
        forwarded = false;
        var command = AudioTastic.App.Integration.InstanceCommand.From(options);
        var deadline = Environment.TickCount64 + 15_000;
        while (true)
        {
            if (AudioTastic.App.Integration.SingleInstance.TryAcquire(paths.Root) is { } instance)
            {
                return instance;
            }
            platform.AllowOtherProcessToActivate();
            if (AudioTastic.App.Integration.SingleInstance.TrySend(paths.Root, command, TimeSpan.FromSeconds(1)))
            {
                forwarded = true;
                return null;
            }
            if (Environment.TickCount64 > deadline)
            {
                return null;
            }
            Thread.Sleep(100);
        }
    }

    /// <summary>EQ-05 / PG-01: one chain effect per enabled DSP plugin instance; faults go to the plugin host (PG-03).</summary>
    private static List<AudioTastic.Core.Audio.Dsp.PluginEffect> PluginEffects(PluginHost host)
    {
        var effects = new List<AudioTastic.Core.Audio.Dsp.PluginEffect>();
        foreach (var plugin in host.Instances<AudioTastic.Sdk.Plugins.IDspPlugin>())
        {
            if (host.RecordOf(plugin) is not { } record)
            {
                continue;
            }
            var name = record.Manifest.Name;
            host.Guard(record, () => name = string.IsNullOrWhiteSpace(plugin.Name) ? record.Manifest.Name : plugin.Name.Trim());
            effects.Add(new AudioTastic.Core.Audio.Dsp.PluginEffect(record.Id, name, plugin.CreateEffect, ex => host.ReportFault(record, ex)));
        }
        return effects;
    }

    /// <summary>Avalonia configuration; also used by the XAML previewer.</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
