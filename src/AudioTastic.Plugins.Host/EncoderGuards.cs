using AudioTastic.Sdk.Audio;

namespace AudioTastic.Plugins.Host;

/// <summary>
/// PG-03 for encoders (SDK 0.4.1): an encoder plugin's exceptions become <see cref="AudioSourceException"/> after being
/// reported as plugin faults (three a minute disable the plugin), like decoders.
/// </summary>
internal sealed class GuardedEncoderFactory(IAudioEncoderFactory inner, PluginRecord plugin, PluginHost host) : IAudioEncoderFactory
{
    public string Name => inner.Name;

    public IReadOnlyCollection<EncodeFormat> Formats
    {
        get
        {
            if (plugin.Status != PluginStatus.Enabled)
            {
                return [];
            }
            IReadOnlyCollection<EncodeFormat> formats = [];
            host.Guard(plugin, () => formats = inner.Formats);
            return formats;
        }
    }

    public IAudioEncoder Create(EncodeSettings settings, AudioFormat format, Stream output) =>
        Run(() => new GuardedEncoder(inner.Create(settings, format, output), plugin, host), plugin, host);

    internal static T Run<T>(Func<T> call, PluginRecord plugin, PluginHost host)
    {
        try
        {
            return call();
        }
        catch (AudioSourceException)
        {
            throw;
        }
        catch (IOException)
        {
            throw; // the output went away (e.g. the cast device closed the connection): not the plugin's fault
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

internal sealed class GuardedEncoder(IAudioEncoder inner, PluginRecord plugin, PluginHost host) : IAudioEncoder
{
    public void Write(ReadOnlySpan<float> samples)
    {
        // Spans cannot be captured by a lambda, so the guard is spelled out here.
        try
        {
            inner.Write(samples);
        }
        catch (Exception ex) when (ex is not AudioSourceException and not IOException)
        {
            host.ReportFault(plugin, ex);
            throw new AudioSourceException($"The {plugin.Manifest.Name} plugin failed: {ex.Message}", ex);
        }
    }

    public void Finish() => GuardedEncoderFactory.Run(() =>
    {
        inner.Finish();
        return 0;
    }, plugin, host);

    public void Dispose() => host.Guard(plugin, inner.Dispose);
}
