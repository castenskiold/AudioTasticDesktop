using Avalonia.Controls;
using Avalonia.Interactivity;
using AudioTastic.Core.Diagnostics;
using AudioTastic.Platform;

namespace AudioTastic.App;

/// <summary>TL-03: dialog offering the crash report from the previous run.</summary>
public sealed partial class CrashReportWindow : Window
{
    private readonly string _reportPath = string.Empty;
    private readonly CrashReporter? _crashReporter;
    private readonly IPlatformServices? _platform;

    /// <summary>Used by the XAML previewer only.</summary>
    public CrashReportWindow()
    {
        InitializeComponent();
    }

    public CrashReportWindow(string reportPath, CrashReporter crashReporter, IPlatformServices platform)
    {
        _reportPath = reportPath;
        _crashReporter = crashReporter;
        _platform = platform;
        InitializeComponent();
        ReportPath.Text = reportPath;
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _crashReporter?.AcknowledgePending();
    }

    private void OpenReport_OnClick(object? sender, RoutedEventArgs e) => _platform?.OpenFile(_reportPath);

    private void OpenFolder_OnClick(object? sender, RoutedEventArgs e) =>
        _platform?.OpenFolder(Path.GetDirectoryName(_reportPath) ?? _reportPath);

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();
}
