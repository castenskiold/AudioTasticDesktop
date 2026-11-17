using AudioTastic.Sdk.Audio;

namespace AudioTastic.Plugins.Host;

/// <summary>
/// PG-03: wraps a plugin's decoder factory. An <see cref="AudioSourceException"/> is a normal "cannot play this file"
/// and passes through; any other exception is a plugin fault: it is reported (3 in a minute disable the plugin) and
/// turned into an <see cref="AudioSourceException"/>, so playback moves on instead of crashing.
/// </summary>
internal sealed class GuardedSourceFactory(IAudioSourceFactory inner, PluginRecord plugin, PluginHost host) : IAudioSourceFactory
{
    public IAudioSourceFactory Inner => inner;
    public PluginRecord Plugin => plugin;
    public string Name => inner.Name;
    public IReadOnlyCollection<string> Extensions => inner.Extensions;

    public bool CanOpen(string path)
    {
        if (plugin.Status != PluginStatus.Enabled)
        {
            return false;
        }
        try
        {
            return inner.CanOpen(path);
        }
#pragma warning disable CA1031 // A plugin fault must never escape into the player.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            host.ReportFault(plugin, ex);
            return false;
        }
    }

    public IAudioSource Open(string path)
    {
        try
        {
            return new GuardedAudioSource(inner.Open(path), plugin, host);
        }
        catch (AudioSourceException)
        {
            throw;
        }
#pragma warning disable CA1031 // Converted to AudioSourceException after reporting.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            host.ReportFault(plugin, ex);
            throw new AudioSourceException($"The {plugin.Manifest.Name} plugin failed: {ex.Message}", ex);
        }
    }
}

/// <summary>PG-03: the same guard around a decoder instance (Read and Seek run on a deck's decoder thread).</summary>
internal sealed class GuardedAudioSource(IAudioSource inner, PluginRecord plugin, PluginHost host) : IAudioSource, IStreamMetadataSource
{
    public AudioStreamInfo Info => inner.Info;
    public AudioFormat Format => inner.Format;
    public long? LengthFrames => inner.LengthFrames;
    public long PositionFrames => inner.PositionFrames;
    public bool CanSeek => inner.CanSeek;
    public IReadOnlyDictionary<string, string> Tags => inner.Tags;

    /// <summary>Streams expose their metadata through the wrapper (FM-10).</summary>
    public IStreamMetadata? StreamMetadata => inner as IStreamMetadata;

    public int Read(Span<float> buffer)
    {
        try
        {
            return inner.Read(buffer);
        }
        catch (AudioSourceException)
        {
            throw;
        }
#pragma warning disable CA1031 // Converted to AudioSourceException after reporting.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            host.ReportFault(plugin, ex);
            throw new AudioSourceException($"The {plugin.Manifest.Name} plugin failed while decoding: {ex.Message}", ex);
        }
    }

    public void Seek(long frame)
    {
        try
        {
            inner.Seek(frame);
        }
        catch (Exception ex) when (ex is not AudioSourceException and not NotSupportedException)
        {
            host.ReportFault(plugin, ex);
            throw new AudioSourceException($"The {plugin.Manifest.Name} plugin failed while seeking: {ex.Message}", ex);
        }
    }

    public void Dispose()
    {
        try
        {
            inner.Dispose();
        }
#pragma warning disable CA1031 // A plugin fault must never escape into the player.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            host.ReportFault(plugin, ex);
        }
    }
}
