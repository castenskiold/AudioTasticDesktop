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
                .AddSingleton<IAudioOutput>(sp => new MiniaudioOutput(sp.GetRequiredService<ILogger<MiniaudioOutput>>()))
                .AddSingleton<PlaybackEngine>()
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
                .AddSingleton<AudioTastic.App.Ui.AnalysisCoordinator>()
                .AddSingleton<AudioTastic.App.Ui.Playlist.PlaylistController>()
                .AddSingleton<AudioTastic.App.Ui.PlayerShell>()
                .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

            AppHost.Services = services;

            // PG-01: plugins are loaded before the UI so that start-up files can use every decoder.
            var plugins = services.GetRequiredService<PluginHost>();
            plugins.AttachPlayer(services.GetRequiredService<PlayerApi>());
            plugins.LoadAll();

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
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
}
