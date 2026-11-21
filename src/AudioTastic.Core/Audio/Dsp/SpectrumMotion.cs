namespace AudioTastic.Core.Audio.Dsp;

/// <summary>
/// UI-07 / CN-09: how spectrum bars move, shared by the main window and the cast visualisation: a bar rises at once
/// and falls smoothly (3 units a second); its peak cap holds the highest value and falls at the set speed.
/// </summary>
public sealed class SpectrumMotion
{
    public float[] Shown { get; private set; } = [];

    public float[] Peaks { get; private set; } = [];

    public void Resize(int bars)
    {
        if (Shown.Length != bars)
        {
            Shown = new float[bars];
            Peaks = new float[bars];
        }
    }

    /// <summary>Moves the bars towards <paramref name="levels"/> (0–1) over <paramref name="seconds"/>.</summary>
    public void Update(ReadOnlySpan<float> levels, double seconds, double peakFall)
    {
        var fall = (float)(seconds * 3);
        var capFall = (float)(seconds * peakFall);
        for (var i = 0; i < Shown.Length; i++)
        {
            var level = i < levels.Length ? levels[i] : 0;
            Shown[i] = Math.Max(level, Shown[i] - fall);
            Peaks[i] = Math.Max(Shown[i], Peaks[i] - capFall);
        }
    }
}
