using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using AudioTastic.Data;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AudioTastic.Core.Diagnostics;

/// <summary>
/// TL-03: writes crash reports (exception, versions, OS, enabled plugins, log tail) to the crash folder
/// and remembers the newest one as "pending" so the next start can offer to open it.
/// Nothing is uploaded.
/// </summary>
public sealed class CrashReporter
{
    public const string PendingMarkerFileName = "pending.txt";
    public const int LogTailLines = 200;

    private readonly AppPaths _paths;
    private readonly ILogger _logger;
    private readonly Func<IReadOnlyList<string>> _enabledPlugins;
    private readonly TimeProvider _time;

    public CrashReporter(AppPaths paths, ILogger<CrashReporter>? logger = null,
        Func<IReadOnlyList<string>>? enabledPlugins = null, TimeProvider? time = null)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _logger = logger ?? (ILogger)NullLogger.Instance;
        _enabledPlugins = enabledPlugins ?? (() => []);
        _time = time ?? TimeProvider.System;
    }

    private string PendingMarker => Path.Combine(_paths.CrashFolder, PendingMarkerFileName);

    /// <summary>Writes a report for <paramref name="exception"/> and marks it pending. Never throws.</summary>
    /// <returns>Path of the report, or null if it could not be written.</returns>
    public string? Write(Exception exception, string source)
    {
        try
        {
            Directory.CreateDirectory(_paths.CrashFolder);
            var now = _time.GetLocalNow();
            var file = Path.Combine(_paths.CrashFolder,
                $"crash-{now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture)}.txt");
            File.WriteAllText(file, BuildReport(exception, source, now));
            File.WriteAllText(PendingMarker, Path.GetFileName(file));
            _logger.LogCritical(exception, "Unhandled exception from {Source}; crash report written to {File}", source, file);
            return file;
        }
#pragma warning disable CA1031 // A crash reporter must never throw.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            _logger.LogError(ex, "Could not write crash report");
            return null;
        }
    }

    /// <summary>Path of a report from a previous run that has not been acknowledged, or null.</summary>
    public string? GetPendingReport()
    {
        if (!File.Exists(PendingMarker))
        {
            return null;
        }
        var name = File.ReadAllText(PendingMarker).Trim();
        var path = Path.Combine(_paths.CrashFolder, name);
        return name.Length > 0 && File.Exists(path) ? path : null;
    }

    /// <summary>Marks the pending report as seen, so it is not offered again.</summary>
    public void AcknowledgePending()
    {
        if (File.Exists(PendingMarker))
        {
            File.Delete(PendingMarker);
        }
    }

    /// <summary>Hooks AppDomain and TaskScheduler exception events. UI-thread exceptions are hooked by the app.</summary>
    public void InstallProcessHandlers(Action? beforeExit = null)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Write(ex, e.IsTerminating ? "AppDomain (terminating)" : "AppDomain");
            }
            beforeExit?.Invoke();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            // Not fatal: log it and keep running.
            _logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };
    }

    internal string BuildReport(Exception exception, string source, DateTimeOffset now)
    {
        var app = Assembly.GetEntryAssembly() ?? typeof(CrashReporter).Assembly;
        var version = app.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? app.GetName().Version?.ToString() ?? "unknown";
        var plugins = _enabledPlugins();

        var sb = new StringBuilder();
        sb.AppendLine("AudioTastic crash report");
        sb.AppendLine("========================");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Time:        {now:O}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Source:      {source}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"App version: {version}");
        sb.AppendLine(CultureInfo.InvariantCulture, $".NET:        {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS:          {RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Process:     {RuntimeInformation.ProcessArchitecture}, portable={_paths.IsPortable}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Data folder: {_paths.Root}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Plugins:     {(plugins.Count == 0 ? "(none)" : string.Join(", ", plugins))}");
        sb.AppendLine();
        sb.AppendLine("Exception");
        sb.AppendLine("---------");
        sb.AppendLine(exception.ToString());
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"Log tail (last {LogTailLines} lines)");
        sb.AppendLine("--------------------------");
        foreach (var line in ReadLogTail())
        {
            sb.AppendLine(line);
        }
        return sb.ToString();
    }

    private string[] ReadLogTail()
    {
        try
        {
            if (!Directory.Exists(_paths.LogsFolder))
            {
                return ["(no logs folder)"];
            }
            var newest = new DirectoryInfo(_paths.LogsFolder).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            if (newest is null)
            {
                return ["(no log file)"];
            }
            using var stream = new FileStream(newest.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var tail = new Queue<string>(LogTailLines);
            while (reader.ReadLine() is { } line)
            {
                if (tail.Count == LogTailLines)
                {
                    tail.Dequeue();
                }
                tail.Enqueue(line);
            }
            return [.. tail];
        }
        catch (IOException ex)
        {
            return [$"(could not read log: {ex.Message})"];
        }
    }
}
