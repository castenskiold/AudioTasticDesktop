using System.Globalization;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Extensions.Logging;

namespace AudioTastic.Core.Diagnostics;

/// <summary>
/// TL-03: structured logging to rolling files in the logs folder (one file per day, 7 days kept).
/// Components log through Microsoft.Extensions.Logging (ILogger&lt;T&gt;); Serilog writes the files.
/// </summary>
public sealed class Logging : IDisposable
{
    public const string FilePrefix = "audiotastic-";
    public const int RetainedDays = 7;

    private readonly Serilog.Core.Logger _serilog;
    private int _closed;

    private Logging(Serilog.Core.Logger serilog, LoggingLevelSwitch levelSwitch, string logsFolder)
    {
        _serilog = serilog;
        LevelSwitch = levelSwitch;
        LogsFolder = logsFolder;
        Factory = new SerilogLoggerFactory(serilog, dispose: false);
    }

    /// <summary>Factory for ILogger&lt;T&gt; instances.</summary>
    public ILoggerFactory Factory { get; }

    /// <summary>Changes the minimum level at runtime (Settings › General › Log level).</summary>
    public LoggingLevelSwitch LevelSwitch { get; }

    public string LogsFolder { get; }

    public static Logging Create(AppPaths paths, LogLevelSetting level)
    {
        ArgumentNullException.ThrowIfNull(paths);
        Directory.CreateDirectory(paths.LogsFolder);
        var levelSwitch = new LoggingLevelSwitch(ToSerilog(level));
        var logger = new LoggerConfiguration()
            .MinimumLevel.ControlledBy(levelSwitch)
            .Enrich.FromLogContext()
            .WriteTo.File(
                Path.Combine(paths.LogsFolder, FilePrefix + ".log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: RetainedDays,
                shared: true,
                formatProvider: CultureInfo.InvariantCulture,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}")
            .CreateLogger();
        return new Logging(logger, levelSwitch, paths.LogsFolder);
    }

    public void SetLevel(LogLevelSetting level) => LevelSwitch.MinimumLevel = ToSerilog(level);

    public static LogEventLevel ToSerilog(LogLevelSetting level) => level switch
    {
        LogLevelSetting.Verbose => LogEventLevel.Verbose,
        LogLevelSetting.Debug => LogEventLevel.Debug,
        LogLevelSetting.Information => LogEventLevel.Information,
        LogLevelSetting.Warning => LogEventLevel.Warning,
        LogLevelSetting.Error => LogEventLevel.Error,
        _ => LogEventLevel.Information,
    };

    /// <summary>Writes buffered log events to disk and closes the files (called before the process dies). Safe to call more than once.</summary>
    public void Flush()
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0)
        {
            _serilog.Dispose();
        }
    }

    public void Dispose()
    {
        Factory.Dispose();
        Flush();
    }
}
