using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Input.OptimFrog;

/// <summary>
/// FM-06: OptimFROG (.ofr, .ofs) through the closed-source freeware OptimFROG SDK (licence-policy exception; bundled
/// under its redistribution terms). Only x64 builds of the library exist; elsewhere the plugin offers no formats.
/// </summary>
public sealed class OptimFrogInputPlugin : IInputPlugin
{
    private readonly OptimFrogSourceFactory _factory = new();

    public IReadOnlyList<IAudioSourceFactory> SourceFactories => OptimFrogNative.IsAvailable ? [_factory] : [];

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (OptimFrogNative.IsAvailable)
        {
            var version = OptimFrogNative.OptimFROG_getVersion();
            context.Logger.LogInformation("OptimFROG library {Major}.{Minor:000}", version / 1000, version % 1000);
        }
        else
        {
            context.Logger.LogInformation("The OptimFROG library is not available for this platform; .ofr/.ofs files are not listed");
        }
    }

    public void Dispose()
    {
    }
}

internal sealed class OptimFrogSourceFactory : IAudioSourceFactory
{
    public string Name => "OptimFROG";
    public IReadOnlyCollection<string> Extensions { get; } = [".ofr", ".ofs"];

    public bool CanOpen(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public IAudioSource Open(string path) => OptimFrogSource.Open(path);
}
