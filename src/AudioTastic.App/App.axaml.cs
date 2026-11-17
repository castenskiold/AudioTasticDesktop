using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Microsoft.Extensions.DependencyInjection;

namespace AudioTastic.App;

public sealed partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop && AppHost.Services is { } services)
        {
            // The shell creates the skinned main window and mini player (UI-01, UI-05).
            services.GetRequiredService<Ui.PlayerShell>().Start(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }
}
