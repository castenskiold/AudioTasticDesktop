using Avalonia;
using AudioTastic.Core.Audio;
using AudioTastic.Core.Audio.Output;
using AudioTastic.Core.Diagnostics;
using AudioTastic.Core.Playback;
using AudioTastic.Data;
using AudioTastic.Data.Cache;
using AudioTastic.Data.Settings;
using AudioTastic.Input.Ffmpeg;
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
                // P1 audio core. Input plugins are registered directly until the plugin loader (PG-01, P4).
                .AddSingleton<IAudioSourceFactory, FfmpegSourceFactory>()
                .AddSingleton<IAudioOutput>(sp => new MiniaudioOutput(sp.GetRequiredService<ILogger<MiniaudioOutput>>()))
                .AddSingleton<PlaybackEngine>()
                .AddSingleton<IPlayerController>(sp => sp.GetRequiredService<PlaybackEngine>())
                // P2 player UI: skins (UI-03) and the shell that owns the skinned windows.
                .AddSingleton<AudioTastic.App.Skins.SkinManager>()
                .AddSingleton<AudioTastic.App.Ui.PlayerShell>()
                .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

            AppHost.Services = services;

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
