using AudioTastic.Sdk.Audio;
using AudioTastic.Sdk.Plugins;

namespace AudioTastic.Input.Ffmpeg;

/// <summary>FM-01: the built-in FFmpeg decoder as an input plugin (PG-01), loaded by the plugin host like any other.</summary>
public sealed class FfmpegInputPlugin : IInputPlugin
{
    private readonly FfmpegSourceFactory _factory = new();

    public IReadOnlyList<IAudioSourceFactory> SourceFactories => [_factory];

    public void Initialize(IPluginContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
    }

    public void Dispose()
    {
    }
}
