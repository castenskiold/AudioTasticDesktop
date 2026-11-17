using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AudioTastic.Core.Diagnostics;
using AudioTastic.Data;
using AudioTastic.Data.Settings;
using AudioTastic.Platform;
using Microsoft.Extensions.Logging;

namespace AudioTastic.App;

/// <summary>
/// P0 main window: borderless, draggable by its title bar, remembers its position,
/// and offers the pending crash report from a previous run (TL-03).
/// </summary>
public sealed partial class MainWindow : Window
{
    private readonly SettingsStore? _settings;
    private readonly CrashReporter? _crashReporter;
    private readonly IPlatformServices? _platform;
    private readonly StartupOptions? _options;
    private readonly ILogger<MainWindow>? _logger;

    /// <summary>Used by the XAML previewer only.</summary>
    public MainWindow()
    {
        InitializeComponent();
    }

    public MainWindow(AppPaths paths, SettingsStore settings, CrashReporter crashReporter,
        IPlatformServices platform, StartupOptions options, ILogger<MainWindow> logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _settings = settings;
        _crashReporter = crashReporter;
        _platform = platform;
        _options = options;
        _logger = logger;
        InitializeComponent();

        StatusText.Text = $"Phase P0 · {platform.Name} · {(paths.IsPortable ? "portable · " : string.Empty)}{paths.Root}";
        ToolTip.SetTip(StatusText, paths.Root);
        RestorePosition(settings.Current.Interface.MainWindow);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _logger?.LogInformation("Main window opened");

        if (_crashReporter?.GetPendingReport() is { } report && _platform is not null)
        {
            Dispatcher.UIThread.Post(async () =>
            {
                await new CrashReportWindow(report, _crashReporter, _platform).ShowDialog(this);
            });
        }

        if (_options?.CrashTest == true)
        {
            // TL-03 exit criterion: a forced exception must produce a crash report.
            Dispatcher.UIThread.Post(() => throw new InvalidOperationException(
                $"Crash test requested with {StartupOptions.CrashTestSwitch}."));
        }
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        _settings?.Update(SettingsGroup.Interface,
            s => s.Interface.MainWindow = new WindowPlacement(Position.X, Position.Y));
    }

    private void RestorePosition(WindowPlacement? placement)
    {
        if (placement is null)
        {
            return;
        }
        var point = new PixelPoint(placement.X, placement.Y);
        // Only restore if the saved position is still on a connected screen.
        if (Screens.All.Any(s => s.WorkingArea.Contains(point)))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Position = point;
        }
    }

    private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void Minimise_OnClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_OnClick(object? sender, RoutedEventArgs e) => Close();
}
