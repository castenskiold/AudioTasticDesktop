using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;
using Microsoft.Extensions.Logging;

namespace AudioTastic.Input.OpenMpt;

/// <summary>FM-07: the tracker-module input plugin.</summary>
public sealed class OpenMptInputPlugin : IInputPlugin
{
    private readonly OpenMptSourceFactory _factory = new();

    public IReadOnlyList<IAudioSourceFactory> SourceFactories => OpenMptNative.IsAvailable ? [_factory] : [];

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (OpenMptNative.IsAvailable)
        {
            context.Logger.LogInformation("libopenmpt {Version}", OpenMptNative.GetString("library_version"));
        }
        else
        {
            context.Logger.LogWarning("libopenmpt is not available for this platform; tracker modules cannot be played");
        }
    }

    public void Dispose()
    {
    }
}

internal sealed class OpenMptSourceFactory : IAudioSourceFactory
{
    public string Name => "libopenmpt";

    public IReadOnlyCollection<string> Extensions { get; } = [".mod", ".xm", ".it", ".s3m", ".mptm", ".mtm", ".669", ".stm", ".okt", ".med"];

    public bool CanOpen(string path) => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public IAudioSource Open(string path) => OpenMptSource.Open(path);
}
