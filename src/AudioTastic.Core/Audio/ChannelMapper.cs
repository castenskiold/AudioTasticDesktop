namespace AudioTastic.Core.Audio;

/// <summary>
/// Maps decoded audio to the engine's channel count (stereo in P1). Runs on the decoder thread.
/// Mono is copied to both sides; multichannel is downmixed using FFmpeg's default channel order
/// (FL FR FC LFE BL/SL BR/SR ...): centre and surrounds at −3 dB, LFE dropped, then normalised to avoid clipping.
/// </summary>
public static class ChannelMapper
{
    private const float MinusThreeDb = 0.70710677f;

    public static void Map(ReadOnlySpan<float> source, int sourceChannels, Span<float> destination, int destinationChannels, int frames)
    {
        if (sourceChannels == destinationChannels)
        {
            source[..(frames * sourceChannels)].CopyTo(destination);
            return;
        }
        if (destinationChannels != 2)
        {
            throw new NotSupportedException($"Output with {destinationChannels} channels is not supported yet.");
        }

        if (sourceChannels == 1)
        {
            for (var i = 0; i < frames; i++)
            {
                destination[2 * i] = source[i];
                destination[(2 * i) + 1] = source[i];
            }
            return;
        }

        // Downmix: L = FL + c·FC + c·SL (+ extra channels alternating left/right), R likewise.
        var norm = 1f / (1f + MinusThreeDb + MinusThreeDb);
        for (var i = 0; i < frames; i++)
        {
            var frame = source.Slice(i * sourceChannels, sourceChannels);
            float l = frame[0], r = frame[1];
            if (sourceChannels > 2)
            {
                l += MinusThreeDb * frame[2];
                r += MinusThreeDb * frame[2];
            }
            for (var c = 4; c < sourceChannels; c++)
            {
                if ((c & 1) == 0)
                {
                    l += MinusThreeDb * frame[c];
                }
                else
                {
                    r += MinusThreeDb * frame[c];
                }
            }
            destination[2 * i] = l * norm;
            destination[(2 * i) + 1] = r * norm;
        }
    }
}
