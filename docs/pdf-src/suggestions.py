import sys, os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import theme as T
from theme import *
from reportlab.lib.units import mm
def HH1(t): return T.h1(t)
def HH2(t): return T.h2(t)
W=[42,108,24]
_t=T.table
def table(rows,widths=None,head=("Feature","Description","Priority")): return _t(rows,widths,head)
story=[]

story += HH1("1. Purpose and scope")
story.append(Paragraph("This document lists the features for a Winamp-inspired desktop audio player for Windows built on modern .NET. "
 "Features are split into two groups: <b><font color='#FFFFFF'>Required</font></b> (stated in the original brief) and "
 "<b><font color='#FFFFFF'>Suggested</font></b> (additional features from Winamp, foobar2000, MusicBee and similar players). "
 "Suggested features carry a priority: <b>High</b> (strongly recommended for v1), <b>Medium</b> (v1.x) and <b>Low</b> (nice to have / later).",B))

story += HH1("2. Required features")
req=[
("Platform","Windows desktop application built on .NET (current LTS, e.g. .NET 8/10).","Required"),
("Audio formats","Playback of WAV, FLAC, MP3, AIFF, APE (Monkey's Audio), AAC and WV (WavPack), plus other common formats (see section 3.1).","Required"),
("Playlist","Create, edit, reorder, load and save playlists.","Required"),
("Transport controls","Play, pause, stop (plus previous/next and seeking as implied by a Winamp-style UI).","Required"),
("Shuffle","Randomised playback order.","Required"),
("Repeat","Repeat modes for playback.","Required"),
("Volume","Volume control.","Required"),
("Equalizer","Graphic equalizer (Winamp classic: 10-band with preamp).","Required"),
("Time display","Time display that toggles between elapsed and remaining time on click.","Required"),
("Waveform visualisation","Waveform view beneath the time display.","Required"),
("Plugin architecture","Extensibility via plugins.","Required"),
("Casting","Cast audio to network devices such as Chromecast.","Required"),
("DJ-style crossfading","Tracks are mixed together like a DJ set when one track changes to the next. Two modes: Intro/Outro and Beat matching (see section 2.1).","Required"),
]
story.append(table(req,W))

xfblock=[]
xfblock += HH2("2.1 DJ-style crossfading specification")
xfblock.append(Paragraph("When the current track nears its end, the next track starts and the two are mixed together. "
 "The user selects one of the two modes below and sets its options.",B))
xfblock.append(Spacer(1,3))
xf=[
("Mode: Intro/Outro","The current track fades out while the next track fades in over it.","Required"),
("Overlap duration (Intro/Outro)","Setting, in seconds: how long the two tracks overlap. At the end of this time the current track is fully faded out and the new track is at full volume.","Required"),
("Mode: Beat matching","The tempo (BPM) of the incoming track is adjusted to match the BPM of the track that is playing, and the beats are lined up so the two play in time.","Required"),
("Overlap duration (Beat matching)","Setting, in seconds: how long both tracks play together at full volume before the first track is turned down or cut off.","Required"),
("End behaviour (Beat matching)","Setting: what happens to the first track when the overlap ends. <b>Cut</b>: it stops at once. <b>Fade out</b>: it fades out over a further period.","Required"),
("Fade-out duration (Beat matching)","Optional setting, in seconds: how long the fade-out lasts. Only used when end behaviour is <b>Fade out</b>.","Required"),
("Tempo limit (Beat matching)","Setting, in percent: the largest tempo change allowed (e.g. &plusmn;8%). If the two tracks are further apart than this, the player uses Intro/Outro for that transition instead.","Required"),
("Tempo return (Beat matching)","After the first track has ended, the new track goes back to its original BPM. Setting with two options: <b>Cut at next break</b>: it switches to the original BPM at once when the next break in the track occurs. <b>Gradual</b>: it moves slowly back to the original BPM.","Required"),
("Tempo return duration","Optional setting, in seconds: how long the gradual return to the original BPM takes. Only used when tempo return is <b>Gradual</b>.","Required"),
]
xfblock.append(table(xf,W,head=("Setting","Behaviour","Status")))
xfblock.append(Spacer(1,4))
xfblock.append(Paragraph("<b>Implementation notes:</b> beat matching needs BPM and beat-position detection, best done once per track and cached in the library. "
 "Tempo changes should use time-stretching that keeps the pitch (e.g. SoundTouch or BASS_FX), so the incoming track does not sound sped up. "
 "<b>Cut at next break</b> needs break detection (e.g. a drop in energy or a section with no drums), which can be analysed and cached at the same time as the BPM. "
 "If no break is found, the player should fall back to <b>Gradual</b> or switch at the next phrase boundary.",B))

story.append(KeepTogether(xfblock))
story.append(PageBreak())
story += HH1("3. Suggested features")

sections = [
("3.1 Additional audio formats", [
("Ogg Vorbis / Opus","Common open lossy formats; Opus is the modern standard for streaming and podcasts.","High"),
("ALAC / M4A","Apple Lossless and AAC in MP4 containers (iTunes libraries).","High"),
("WMA","Windows Media Audio, still common in older Windows libraries.","Medium"),
("DSD (DSF / DFF)","High-resolution audiophile formats, via DSD-to-PCM conversion or DoP output.","Low"),
("Musepack, TTA, Shorten, OptimFROG","Niche lossy/lossless formats popular in archival collections.","Low"),
("Tracker modules (MOD, XM, IT, S3M)","A Winamp classic; easy to add via libopenmpt or BASS.","Medium"),
("MIDI","Playback with a SoundFont-based synthesizer.","Low"),
("CUE sheets","Treat single-file album rips plus .cue as separate tracks.","High"),
("Internet radio / streams","HTTP/Icecast/SHOUTcast streams with ICY metadata; M3U/PLS stream URLs.","High"),
("Audio CD","CD playback and optional ripping.","Low"),
]),
("3.2 Playback engine", [
("Gapless playback","No silence between consecutive tracks (essential for live and classical albums).","High"),
("Fade on pause/stop/seek","Short fades instead of abrupt cuts on pause, stop and seek (separate from DJ-style crossfading).","Medium"),
("ReplayGain / loudness normalisation","Track and album gain from tags, or on-the-fly EBU R128 analysis.","High"),
("Output device selection","Choose output device; support WASAPI shared/exclusive mode and ASIO for low latency/bit-perfect output.","High"),
("High-resolution audio","24/32-bit and up to 384 kHz playback without forced resampling.","Medium"),
("Playback speed / pitch","Speed adjustment with or without pitch preservation (useful for podcasts and practice).","Low"),
("Resume position","Remember last track and position on restart; per-file resume for long files.","Medium"),
("A-B repeat","Loop a section of a track.","Low"),
("Stop after current / queue","Stop after the current track; 'play next' queue separate from the playlist.","High"),
("Sleep timer","Stop or fade out after a set time or at the end of the playlist.","Low"),
]),
("3.3 Playlist and library", [
("Multiple playlists / tabs","Several open playlists side by side, as in foobar2000/MusicBee.","Medium"),
("Playlist formats","Import/export M3U, M3U8, PLS, XSPF, WPL and CUE.","High"),
("Drag and drop","Drop files/folders from Explorer; reorder within the playlist.","High"),
("Search / jump to file","Winamp's 'J' key: instant filtering of the playlist.","High"),
("Sorting and duplicates","Sort by title, artist, album, path or duration; remove duplicates and dead entries.","Medium"),
("Playlist info","Total duration and selection duration shown in the playlist window.","Medium"),
("Media library","Watched folders with indexed database (e.g. SQLite) browsable by artist, album, genre and year.","Medium"),
("Smart playlists","Rule-based playlists (most played, recently added, rating >= 4, etc.).","Low"),
("Ratings and play count","Star ratings and play statistics, optionally written back to tags.","Low"),
]),
("3.4 Metadata", [
("Tag reading and editing","ID3v1/v2, Vorbis comments, APEv2, MP4 atoms (e.g. via TagLib#).","High"),
("Album art","Embedded and folder cover art, shown in the main window and library.","High"),
("Online lookup","Fetch tags and art from MusicBrainz / Cover Art Archive / Discogs.","Low"),
("Lyrics","Show embedded or synced (LRC) lyrics.","Low"),
("File info dialog","Format, bitrate, sample rate, bit depth, channels, codec, file size.","Medium"),
]),
("3.5 User interface", [
("Classic Winamp layout","Main window, playlist editor and equalizer as separate windows that dock and snap together.","High"),
("Skins / themes","Custom skins; consider support for classic Winamp .wsz skins as a signature feature.","Medium"),
("Windowshade / mini mode","Collapse windows to a thin bar; 'always on top' option.","Medium"),
("Scrolling title","Marquee track title with bitrate/kHz/stereo indicators.","High"),
("Visualisations","Spectrum analyser and oscilloscope in the main window, plus a plugin interface for full-screen visualisations.","Medium"),
("Waveform seekbar","Click on the waveform to seek; cache generated waveforms per file.","High"),
("High-DPI and scaling","Crisp rendering on 4K screens; double-size mode as in Winamp.","High"),
("Dark/light mode","Follow Windows theme for non-skinned UI elements.","Low"),
("Localisation","Translatable UI strings from the start.","Low"),
]),
("3.6 Windows integration", [
("Global hotkeys","Configurable system-wide hotkeys, plus support for keyboard media keys.","High"),
("System Media Transport Controls","Windows media overlay, lock screen and Bluetooth headset controls.","High"),
("Taskbar integration","Thumbnail toolbar buttons (prev/play/next) and progress on the taskbar icon.","Medium"),
("System tray","Minimise to tray with a tray menu.","Medium"),
("File associations","Register as handler for audio and playlist files; 'Enqueue in AudioTastic' Explorer context menu.","Medium"),
("Single instance","Opening files sends them to the running instance (play or enqueue).","High"),
("Notifications","Optional toast on track change.","Low"),
("Command-line interface","Control playback from scripts (/play, /next, /enqueue).","Low"),
]),
("3.7 Equalizer and DSP", [
("EQ presets","Built-in presets (Rock, Classical, etc.), user presets, import of Winamp .eqf files.","High"),
("Per-track / auto EQ","Automatically load presets per genre or file.","Low"),
("Parametric EQ","Advanced mode with adjustable frequency, gain and Q.","Low"),
("DSP chain","Ordered chain of effects (EQ, compressor, limiter, reverb, stereo widening) exposed to plugins.","Medium"),
("VST plugin hosting","Load VST2/VST3 effects into the DSP chain.","Low"),
("Room correction","Load convolution impulse responses (e.g. from REW).","Low"),
]),
("3.8 Casting and network", [
("DLNA / UPnP renderers","Cast to Sonos, smart TVs, network receivers.","High"),
("AirPlay","Stream to AirPlay speakers and Apple TV.","Medium"),
("Local HTTP streaming server","Serve the current track for cast targets that pull a URL; transcode unsupported formats on the fly (e.g. APE to FLAC).","High"),
("Device discovery","mDNS / SSDP discovery with a device picker in the UI.","High"),
("Remote control","Web or mobile remote via a local REST/WebSocket API.","Low"),
("Scrobbling","Last.fm and ListenBrainz scrobbling.","Medium"),
("Discord Rich Presence","Show 'now playing' status.","Low"),
]),
("3.9 Plugin architecture details", [
("Plugin types","Input/decoder, output, DSP/effect, visualisation, general-purpose, UI panel and cast-target plugins (mirrors Winamp's in_/out_/dsp_/vis_/gen_ model).","High"),
("Stable SDK","Separate contracts assembly (e.g. AudioTastic.Sdk NuGet package) with versioned interfaces.","High"),
("Isolation","Load plugins with AssemblyLoadContext; catch faults so a broken plugin cannot crash the player.","High"),
("Plugin manager","UI to enable, disable, configure and view plugin details.","Medium"),
("Plugin settings","Each plugin gets its own configuration store and optional settings page.","Medium"),
("Native plugin bridge","Optional: load classic Winamp native plugins (32-bit) via a bridge process.","Low"),
]),
("3.10 Tools and quality of life", [
("Format conversion","Convert/transcode files (e.g. FLAC to MP3/Opus) with tag preservation.","Low"),
("Settings import/export","Back up and move configuration and playlists.","Low"),
("Auto-update","Check for and install updates (e.g. Velopack).","Medium"),
("Crash logging","Structured logging and crash reports to aid debugging.","Medium"),
("Accessibility","Keyboard navigation and screen reader support for non-skinned UI.","Low"),
("Portable mode","Run from a folder or USB stick with settings stored alongside the executable.","Low"),
]),
]
for title, rows in sections:
    story.append(KeepTogether(HH2(title)+[table(rows,W)]) if len(rows)<12 else KeepTogether(HH2(title)+[table(rows[:1],W)]))
    if len(rows)>=12: story.append(table(rows[1:],W,head=None))

story.append(PageBreak())
story += HH1("4. Technical notes (candidate libraries)")
story.append(Paragraph("Suggestions only; licences and maintenance status should be checked before committing.",B))
story.append(Spacer(1,3))
tech=[
("UI framework","WPF (Windows-only, mature) or Avalonia (cross-platform, good for custom-drawn skinned UI). Both work on modern .NET."),
("Audio engine","<b>ManagedBass</b> (BASS + add-ons: FLAC, APE, WV, AAC, Opus, DSD, MIDI, mixer, FX, WASAPI/ASIO) covers almost all formats with one API but is commercial for paid apps. <b>NAudio</b> (MIT) for output/WASAPI/ASIO combined with <b>FFmpeg</b> (via FFmpeg.AutoGen or Sdcb.FFmpeg) for decoding is a free alternative."),
("Tags","TagLib# (TagLibSharp) for reading and writing tags and embedded art."),
("Chromecast","SharpCaster or a custom implementation of the Cast v2 protocol (protobuf over TLS) with the Default Media Receiver."),
("DLNA / UPnP","SSDP discovery plus AVTransport SOAP calls; small libraries exist or it can be hand-rolled."),
("Local stream server","Kestrel (ASP.NET Core) embedded in the app to serve tracks to cast devices."),
("BPM detection / time-stretch","BASS_FX (BPM and beat detection, tempo change) with ManagedBass; or SoundTouch (via SoundTouch.Net) for tempo change plus aubio or a custom onset detector for beats."),
("Library database","SQLite via Microsoft.Data.Sqlite or EF Core."),
("Plugins","AssemblyLoadContext with a shared contracts assembly; optional MEF-style discovery."),
("Windows integration","Windows.Media.SystemMediaTransportControls via CsWinRT; global hotkeys via RegisterHotKey."),
]
story.append(table(tech,[42*mm,132*mm],head=("Area","Options")))


T.build("AudioTastic_Feature_Suggestions.pdf","Feature Suggestions","Feature suggestion document for a Winamp-style desktop audio player",
 "Version 1.2  ·  17 November 2026  ·  Historical — see Required Features", story, short_title="Feature Suggestions  ·  v1.2")
