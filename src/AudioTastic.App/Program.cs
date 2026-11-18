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
        var options = StartupOptions.Parse(args);
        var paths = AppPaths.ForCurrentProcess(args).EnsureCreated();

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
                .AddSingleton(PlatformServicesFactory.Create())
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

            // PB-03: the engine asks the ReplayGain service for each track's gain.
            services.GetRequiredService<PlaybackEngine>().ReplayGainProvider = services.GetRequiredService<AudioTastic.Analysis.ReplayGainService>();

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

    /// <summary>Avalonia configuration; also used by the XAML previewer.</summary>
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

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
