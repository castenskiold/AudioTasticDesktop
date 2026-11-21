using AudioTastic.Core.Playlists;

namespace AudioTastic.Core.Playback;

/// <summary>
/// CN-01 / CN-02: a device that plays the engine's tracks instead of the local output (casting). While the engine has
/// a remote player (<see cref="PlaybackEngine.SetRemoteAsync"/>) it keeps the playlist, order, queue and state as
/// usual but sends each track and every transport command to the device, takes the position from it, and advances
/// when the device reports the end of the track. Gapless preloading, crossfading, speed, A-B loops, ReplayGain and
/// the DSP chain do not apply to a remote player (the device plays the file itself).
/// </summary>
public interface IRemotePlayer
{
    /// <summary>The device's name, e.g. "Living room TV".</summary>
    string Name { get; }

    /// <summary>Plays <paramref name="entry"/> from <paramref name="start"/> (track time); paused when <paramref name="paused"/>.</summary>
    Task LoadAsync(PlaylistEntry entry, TrackInfo track, TimeSpan start, bool paused);

    Task PauseAsync();

    Task ResumeAsync();

    Task StopAsync();

    Task SeekAsync(TimeSpan position);

    /// <summary>The player's volume (0–100) and mute, applied to the device's own volume.</summary>
    Task SetVolumeAsync(int volume, bool muted);

    /// <summary>The current position in track time (from the device's reports, moved on while it plays).</summary>
    TimeSpan Position { get; }

    /// <summary>True once the loaded track has played to its end on the device.</summary>
    bool HasEnded { get; }

    /// <summary>The device went away or was taken over by another app; the engine returns to local playback, paused.</summary>
    event EventHandler? Lost;
}
