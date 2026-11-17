using AudioTastic.Core.Diagnostics;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Core.Tests;

public sealed class CrashReporterTests : IDisposable
{
    private readonly string _exeDir = Path.Combine(Path.GetTempPath(), "audiotastic-tests", Guid.NewGuid().ToString("N"));
    private readonly AppPaths _paths;

    public CrashReporterTests()
    {
        Directory.CreateDirectory(_exeDir);
        _paths = AppPaths.Resolve(new AppPathsContext(AppPathsContext.CurrentOs, _exeDir, _exeDir, null, null, ["--portable"]))
            .EnsureCreated();
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_exeDir, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Write_creates_report_and_marks_it_pending()
    {
        var reporter = new CrashReporter(_paths, enabledPlugins: () => ["Sample plugin 1.0"]);
        var file = reporter.Write(new InvalidOperationException("boom"), "test");

        Assert.NotNull(file);
        Assert.Equal(file, reporter.GetPendingReport());
        var text = File.ReadAllText(file!);
        Assert.Contains("boom", text, StringComparison.Ordinal);
        Assert.Contains("Source:      test", text, StringComparison.Ordinal);
        Assert.Contains("Sample plugin 1.0", text, StringComparison.Ordinal);
        Assert.Contains("portable=True", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Acknowledge_clears_pending_but_keeps_report()
    {
        var reporter = new CrashReporter(_paths);
        var file = reporter.Write(new InvalidOperationException("boom"), "test");
        reporter.AcknowledgePending();
        Assert.Null(reporter.GetPendingReport());
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void No_pending_report_initially()
    {
        Assert.Null(new CrashReporter(_paths).GetPendingReport());
    }

    [Fact]
    public void Report_includes_log_tail()
    {
        using (var logging = Logging.Create(_paths, LogLevelSetting.Information))
        {
            logging.Factory.CreateLogger("Test").LogInformation("marker-line-{Number}", 12345);
            logging.Flush();
        }

        var file = new CrashReporter(_paths).Write(new InvalidOperationException("boom"), "test");
        Assert.Contains("marker-line-12345", File.ReadAllText(file!), StringComparison.Ordinal);
    }

    [Fact]
    public void Logging_writes_daily_file_with_prefix()
    {
        using (var logging = Logging.Create(_paths, LogLevelSetting.Information))
        {
            logging.Factory.CreateLogger("Test").LogInformation("hello");
            logging.Flush();
        }
        var files = Directory.GetFiles(_paths.LogsFolder, Logging.FilePrefix + "*.log");
        Assert.Single(files);
    }

    [Fact]
    public void Log_level_switch_follows_setting()
    {
        using var logging = Logging.Create(_paths, LogLevelSetting.Warning);
        Assert.Equal(Serilog.Events.LogEventLevel.Warning, logging.LevelSwitch.MinimumLevel);
        logging.SetLevel(LogLevelSetting.Debug);
        Assert.Equal(Serilog.Events.LogEventLevel.Debug, logging.LevelSwitch.MinimumLevel);
    }
}
