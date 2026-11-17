namespace AudioTastic.App;

/// <summary>
/// Holds the DI container for Avalonia's App class, which Avalonia creates itself (parameterless constructor).
/// Only App uses this; everything else receives its dependencies through constructors.
/// </summary>
internal static class AppHost
{
    public static IServiceProvider? Services { get; set; }
}
