namespace AudioTastic.Sdk.Audio;

/// <summary>
/// FM-05: implemented by decoder factories that can deliver DSD as DoP (DSD over PCM) for DACs that support it.
/// The player uses it only in exclusive mode with Settings › Output › DSD mode = DoP; otherwise DSD is decoded to PCM.
/// </summary>
public interface IDopSourceFactory
{
    /// <summary>True for DSD files (.dsf, .dff) this factory can deliver as DoP.</summary>
    bool IsDsd(string path);

    /// <summary>Opens a DSD file as DoP: 24-bit words (marker byte + 16 DSD bits) as floats (value / 2^23).</summary>
    IAudioSource OpenDop(string path);
}
