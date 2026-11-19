import sys, os; sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import theme as T
from theme import *
from reportlab.lib.units import mm
def HH1(t): return T.h1(t)
def HH2(t): return T.h2(t)
_t=T.table
def table(rows,widths=None,head=("Feature","Requirement")): return _t(rows,widths,head)
story=[]
story+=[*HH1("1. Purpose"),Paragraph(
"This document lists every feature agreed for AudioTastic Desktop. All features here are <b>required</b>. "
"It combines the original brief with the suggested features that were accepted from the Feature Suggestion Document. "
"The companion <b>Feature Specification</b> document describes each feature in detail: exactly what it does, where it sits in the UI and what is needed to build it. "
f"Features marked {GS} must be configurable (and where relevant, switched on or off) in the application's General settings window (see section 14). "
f"Features marked {WO} depend on a Windows API with no equivalent elsewhere; they are hidden on other platforms.",B),Spacer(1,3),
Paragraph("<b>Two project-wide constraints:</b> (1) every third-party library must be free and open source; closed-source freeware is allowed only when no open-source option exists, needs no licence key and costs the end user nothing (licence policy in section 15); "
"(2) the app should run cross-platform on Windows, macOS and Linux, with Windows as the primary platform.",B)]

sections=[
("2. Platform and core",[
("Platform","Cross-platform desktop application on .NET (current LTS, e.g. .NET 8 or 10) with Avalonia UI. Windows is the primary platform; macOS and Linux are also supported. Platform-specific code sits behind interfaces so everything else is shared."),
("Free libraries","Only free, open-source libraries may be used. Exception: closed-source freeware when no open-source option exists, as long as it needs no licence key and the end user never pays. No paid or commercial-licence components (e.g. no BASS/ManagedBass). See section 15."),
("Transport controls","Play, pause, stop, previous, next and seeking."),
("Shuffle","Randomised playback order."),
("Repeat","Repeat modes (off, repeat track, repeat playlist)."),
("Volume","Volume control (with mute)."),
("Time display","Shows playback time; clicking toggles between elapsed and remaining time."),
("Waveform display","Waveform of the current track shown beneath the time display. Clicking the waveform seeks to that position. Generated waveforms are cached per file."),
]),
("3. Audio formats and sources",[
("Core formats","WAV, FLAC, MP3, AIFF, APE (Monkey's Audio), AAC, WV (WavPack)."),
("Ogg Vorbis / Opus","Common open lossy formats."),
("ALAC / M4A","Apple Lossless and AAC in MP4 containers."),
("WMA","Windows Media Audio."),
("DSD (DSF / DFF)","High-resolution formats, via DSD-to-PCM conversion or DoP output."),
("Musepack, TTA, Shorten, OptimFROG","Niche lossy and lossless archival formats. OptimFROG uses the closed-source freeware OptimFROG SDK (allowed under the licence-policy exception)."),
("Tracker modules","MOD, XM, IT, S3M."),
("MIDI","Playback through a SoundFont-based synthesizer."),
("CUE sheets","Single-file album rips with a .cue file are shown and played as separate tracks."),
("Internet radio / streams","HTTP, Icecast and SHOUTcast streams with ICY metadata (now-playing title). M3U/PLS stream URLs."),
("Audio CD","CD playback and ripping. Needs a separate implementation per platform (Windows first)."),
]),
("4. Playback engine",[
("Gapless playback",f"No silence between consecutive tracks. {GS}"),
("Fade on pause/stop/seek",f"Short fades instead of abrupt cuts on pause, stop and seek. Separate from DJ-style crossfading. {GS}"),
("ReplayGain / loudness normalisation",f"Track and album gain from tags, or on-the-fly EBU R128 analysis. {GS}"),
("Output device selection",f"Choose output device. Exclusive / bit-perfect output where the platform allows it: WASAPI exclusive (Windows), CoreAudio exclusive (macOS), direct ALSA hw device (Linux). ASIO is optional and deferred (section 17). {GS}"),
("High-resolution audio",f"24/32-bit playback up to 384 kHz without forced resampling. {GS}"),
("Playback speed / pitch",f"Speed adjustment with or without keeping the pitch. {GS}"),
("Resume position",f"Remembers the last track and position on restart; per-file resume for long files. {GS}"),
("A-B repeat","Loop a chosen section of a track."),
("Queue and stop after current",f"'Play next' queue separate from the playlist; option to stop after the current track. {GS}"),
("Sleep timer",f"Stop or fade out after a set time or at the end of the playlist. {GS}"),
]),
]
for t,rows in sections: story+=[KeepTogether([*HH1(t),table(rows)])]

# Crossfading
xf=[
("Mode","Two modes: <b>Intro/Outro</b> and <b>Beat matching</b>. The user selects one."),
("Intro/Outro: behaviour","The current track fades out while the next track fades in over it."),
("Intro/Outro: overlap duration","Seconds. How long the tracks overlap. At the end the current track is fully faded out and the new track is at full volume."),
("Beat matching: behaviour","The tempo (BPM) of the incoming track is adjusted to match the BPM of the playing track, and the beats are lined up so the two play in time."),
("Beat matching: fade-in","Seconds. The incoming track fades in over this time instead of appearing at full volume; its bass starts low and rises to the normal level as it fades in (bass fade-in can be switched off). The overlap at full volume follows the fade-in."),
("Beat matching: overlap duration","Seconds. How long both tracks play together at full volume before the first track is turned down or cut off."),
("Beat matching: mix before beat end","Seconds. The beat start and beat end of each track are detected (where its steady drums begin and stop, intro and outro not counted). The mix starts this many seconds before the playing track's beat ends, on a phrase boundary."),
("Beat matching: mix in after beat start","Seconds. The incoming track enters this many seconds after its own beat starts, on a phrase boundary."),
("Beat matching: end behaviour","<b>Cut</b>: the first track stops at once when the overlap ends. <b>Fade out</b>: it fades out over a further period."),
("Beat matching: fade-out duration","Optional, seconds. Length of the fade-out. Only used when end behaviour is <b>Fade out</b>."),
("Beat matching: tempo limit","Percent (e.g. &plusmn;8%). Largest tempo change allowed. If the tracks are further apart, that transition uses Intro/Outro instead."),
("Beat matching: tempo return","After the first track has ended, the new track goes back to its original BPM. <b>Cut at next break</b>: switches to the original BPM at once at the next break in the track. <b>Gradual</b>: moves slowly back to the original BPM."),
("Beat matching: tempo return duration","Optional, seconds. How long the gradual return takes. Only used when tempo return is <b>Gradual</b>."),
("Break detection fallback","If no break is found in the track, the player falls back to <b>Gradual</b> return or switches at the next phrase boundary."),
]
story+=[KeepTogether([*HH1("5. DJ-style crossfading"),
 Paragraph(f"When the current track nears its end, the next track starts and the two are mixed together like a DJ set. All settings below are in {GS}.",B),Spacer(1,3),
 table(xf,head=("Setting","Behaviour")),Spacer(1,4),
 Paragraph("<b>Implementation notes:</b> BPM, beat positions and breaks (e.g. a drop in energy or a section with no drums) are analysed once per track and cached. "
 "Tempo changes must use time-stretching that keeps the pitch (SoundTouch), so the incoming track does not sound sped up.",B)])]

sections2=[
("6. Playlists",[
("Playlist editor","Create, edit, reorder, load and save playlists."),
("Multiple playlists / tabs","Several open playlists side by side (tabs)."),
("Playlist formats","Import and export M3U, M3U8, PLS, XSPF, WPL and CUE."),
("Drag and drop","Drop files and folders from Explorer; reorder by dragging within the playlist."),
("Search / jump to file","Winamp's 'J' key: instant filtering of the playlist to find and play a track."),
("Sorting and duplicates","Sort by title, artist, album, path or duration. Remove duplicates and dead (missing) entries."),
("Playlist info","Total duration and selection duration shown in the playlist window."),
]),
("7. Metadata",[
("Tag reading and editing","ID3v1/v2, Vorbis comments, APEv2 and MP4 atoms."),
("Album art",f"Embedded and folder cover art in the main window. Can be switched on/off in {GS}."),
("Online lookup",f"Fetch tags and cover art from MusicBrainz and the Cover Art Archive. Can be switched on/off in {GS}. (A Discogs fallback for covers is deferred, section 17.)"),
("Lyrics",f"Show embedded or synced (LRC) lyrics. Can be switched on/off in {GS}."),
("File info dialog","Format, bitrate, sample rate, bit depth, channels, codec and file size."),
]),
("8. User interface and skins",[
("Layout","Classic Winamp layout: main window, playlist editor and equalizer as separate windows that dock and snap together."),
("Default skin","Slim, sleek, dark and contemporary. Keeps Winamp's compact, uncluttered layout and avoids a bloated UI. Ships as the default skin/theme. The design is based on the AudioTastic logo (see section 8.1)."),
("Skins / themes","Custom skins/themes that change the look of all player windows, in AudioTastic's own skin format. Classic Winamp .wsz skins are not supported."),
("App icon","The AudioTastic logo is the application icon on all platforms (.ico on Windows, .icns on macOS, PNG set on Linux), and is used in the taskbar/dock, tray, file associations and About dialog."),
("Mini player","Windowshade mode collapses the player to a compact two-row mini player, never just a title bar: title on top; time, seek bar, transport, shuffle, playlist and mute below. Includes 'always on top' option."),
("Scrolling title","Marquee track title with bitrate, kHz and stereo/mono indicators."),
("Visualisations","Spectrum analyser and oscilloscope in the main window. Plugin interface for full-screen visualisations."),
("High-DPI and scaling","Crisp rendering on 4K screens. Double-size mode as in Winamp."),
("Dark/light mode","Non-skinned UI elements (dialogs, settings) follow the Windows theme."),
]),
("9. System integration",[
("Global hotkeys","Configurable system-wide hotkeys; keyboard media keys supported. On Linux under Wayland, global key capture is limited by the desktop environment; media keys go through MPRIS instead."),
("OS media controls","Windows: System Media Transport Controls (media overlay, lock screen, Bluetooth headset buttons). macOS: Now Playing / media keys. Linux: MPRIS."),
("Taskbar integration",f"Thumbnail toolbar buttons (previous/play/next) and progress on the taskbar icon {WO}. macOS: Dock menu."),
("System tray","Minimise to tray with a tray menu (menu bar icon on macOS)."),
("File associations","Register as handler for audio and playlist files on all platforms. The first start asks whether to use AudioTastic for audio files (not in portable mode); the answer can be changed in General settings. 'Open in AudioTastic' and 'Enqueue in AudioTastic' context menu entries with the app icon: Explorer (Windows), Finder Quick Action (macOS), .desktop actions (Linux)."),
("Single instance","Opening files sends them to the running instance (play or enqueue)."),
("Notifications","Optional native notification on track change. Every kind of notification the player shows has its own on/off setting (Anders)."),
("Command-line interface","Control playback from scripts (e.g. /play, /next, /enqueue)."),
]),
("10. Equalizer and DSP",[
("Graphic equalizer","10-band graphic equalizer with preamp (Winamp classic)."),
("EQ presets","Built-in presets (Rock, Classical, etc.), user presets, import of Winamp .eqf files."),
("Per-track / auto EQ","Automatically load presets per genre or per file."),
("Parametric EQ","Advanced mode with adjustable frequency, gain and Q."),
("DSP chain","Ordered chain of effects (EQ, compressor, limiter, reverb, stereo widening), exposed to plugins."),
("VST plugin hosting","Load VST3 effects into the DSP chain on all platforms. VST3 only; no VST2 support."),
("Room correction","Load convolution impulse responses (e.g. from REW)."),
]),
("11. Casting and network",[
("Cast modes","Three ways to cast, chosen per device in the device picker (Anders, 19 November 2026): <b>audio only</b> (the music plays on the cast device); <b>audio and video</b> (the cast device plays the music and shows the visualisation on its screen); <b>video only</b> (only the visualisation goes to a screen, while the music keeps playing on the computer's own speakers, with the visuals kept in time with them). The video is the visualisation: the built-in spectrum/oscilloscope, or a visualisation plugin such as the MilkDrop-style plugin planned later. Video modes are offered only for devices with a screen (Chromecast on a TV, DLNA TVs); AirPlay (RAOP) is audio only."),
("Chromecast","Cast audio, audio and video, or video only to Chromecast devices."),
("DLNA / UPnP renderers","Cast to Sonos, smart TVs and network receivers; video modes to TVs that play video streams."),
("AirPlay","Stream to AirPlay speakers and Apple TV using original AirPlay (AirPlay 1 / RAOP). AirPlay 2 is not in scope for now. No AirPlay test device is available, so this needs community or later testing."),
("Local HTTP streaming server","Serves the current track to cast targets that pull a URL. Transcodes formats the target cannot play on the fly (e.g. APE to FLAC). For the video modes it serves the visualisation as a live video stream (with the audio in audio-and-video mode)."),
("Device discovery","mDNS and SSDP discovery with a device picker in the UI."),
("Remote control","Web or mobile remote through a local REST/WebSocket API."),
("Configuration",f"All casting and network features are configured in {GS}."),
]),
("12. Plugin architecture",[
("Plugin types","Input/decoder, output, DSP/effect, visualisation, general-purpose, UI panel and cast-target plugins (mirrors Winamp's in_/out_/dsp_/vis_/gen_ model)."),
("Stable SDK","Separate contracts assembly (e.g. AudioTastic.Sdk NuGet package) with versioned interfaces."),
("Isolation","Plugins load in their own AssemblyLoadContext. Faults are caught so a broken plugin cannot crash the player."),
("Plugin manager","UI to enable, disable, configure and view plugin details."),
("Plugin settings","Each plugin has its own configuration store and optional settings page."),
("Native plugin bridge",f"Load classic native Winamp plugins (32-bit) through a separate bridge process {WO}."),
]),
("13. Tools and quality of life",[
("Format conversion","Convert/transcode files (e.g. FLAC to MP3 or Opus) and keep tags."),
("Settings import/export","Back up and move configuration and playlists."),
("Crash logging","Structured logging and crash reports."),
("Accessibility","Keyboard navigation and screen reader support for non-skinned UI."),
("Portable mode","Run from a folder or USB stick with settings stored next to the executable."),
]),
]
for t,rows in sections2:
    story+=[KeepTogether([*HH1(t),table(rows)])]
    if t.startswith("8."):
        story+=[KeepTogether([*HH2("8.1 Default skin design direction"),
        Paragraph("The default skin takes its look from the logo: white line art on black, rounded line ends and even line weight.",B),Spacer(1,3),
        table([
        ("Colours","Monochrome: black / near-black background, white for lines, text and active elements, greys for inactive or secondary elements."),
        ("Line style","Thin white outlines with rounded ends and corners, matching the logo's stroke. No gradients, bevels or heavy chrome."),
        ("Controls","Buttons, sliders and toggles drawn as white outline shapes; filled white when pressed or active."),
        ("Waveform, spectrum, oscilloscope","Drawn as thin white lines on black, in the same stroke style."),
        ("Logo use","Logo in the main window's title area and About dialog. Cloud shapes from the logo may be used sparingly as motifs (e.g. idle visualisation); never as clutter."),
        ("Assets","Logo source files and generated icons are in the repository's assets folder (see CLAUDE.md)."),
        ],head=("Element","Direction"))])]

gs=[
("Playback","Gapless, fade on pause/stop/seek, ReplayGain/normalisation mode, playback speed/pitch, resume position, stop after current, sleep timer."),
("Output","Output device, exclusive/bit-perfect mode, high-resolution output (bit depth, sample rate, no resampling)."),
("Crossfading","Mode (Intro/Outro or Beat matching) and all settings in section 5."),
("Metadata","Album art on/off, online lookup on/off (and which services), lyrics on/off."),
("Casting and network","Chromecast, DLNA/UPnP, AirPlay, cast modes (default per device; video resolution, frame rate, visualisation used, visual offset), streaming server (port, transcoding), discovery, remote control."),
("Interface","Skin/theme, player size (default 150 %), mini player, always on top, double size, scrolling title, visualisation, notifications, dark/light mode."),
("System integration","Global hotkeys, tray behaviour, file associations, context menu."),
("Equalizer and DSP","EQ presets, auto EQ rules, DSP chain order, VST plugins, room correction."),
("Plugins","Plugin manager (links to each plugin's own settings page)."),
("General","Portable mode, settings import/export, logging level."),
]
story+=[KeepTogether([*HH1("14. General settings overview"),
 Paragraph("The General settings window groups the configurable features as follows.",B),Spacer(1,3),
 table(gs,head=("Group","Settings"))])]

lic=[
("Allowed","Permissive licences: MIT, BSD, Apache 2.0, zlib, MS-PL, public domain / MIT-0."),
("Allowed with care","LGPL, provided the library is <b>dynamically linked</b> (separate DLL/.so/.dylib) and can be replaced by the user. Applies to FFmpeg (LGPL build only, no --enable-gpl), SoundTouch and TagLibSharp."),
("Allowed as exception","Closed-source freeware, <b>only when no suitable open-source library exists</b>, provided it needs no licence key and the end user never has to pay for it. Check the redistribution terms; if bundling is not allowed, download it on first use. Example: OptimFROG SDK."),
("Not allowed","Paid or commercial-only licences, licence keys, or anything the end user would have to pay for (e.g. BASS/ManagedBass, which is only free for non-commercial use). Also 'open source, but the binaries carry a fee' terms such as the Open Source Maintenance Fee of JsonSchema.Net v8+: check the licence file, not just the package's licence label."),
("Ask first","GPL and AGPL (e.g. aubio, libcdio, cdparanoia, Rubber Band), since they would force the whole app to be GPL. Also custom, non-standard licences (e.g. the GeneralUser GS SoundFont, approved)."),
]
tech=[
("UI","<b>Avalonia 12</b> (MIT). Custom-drawn controls for skins, waveform and visualisations; built-in tray icon support. SVG skins via Svg.Controls.Skia.Avalonia (MIT); skin validation via NJsonSchema (MIT)."),
("Decoding","<b>FFmpeg</b> (LGPL build) via the FFmpeg.AutoGen bindings (MIT). Covers WAV, FLAC, MP3, AIFF, APE, AAC, WV, Vorbis, Opus, ALAC, WMA, DSD (to PCM), Musepack, TTA, Shorten, streams. Also encoding for conversion and cast transcoding."),
("Output","<b>miniaudio</b> (public domain / MIT-0) via own P/Invoke bindings over a small C shim: WASAPI (shared/exclusive), CoreAudio, ALSA/PulseAudio/PipeWire. (ASIO via the Steinberg ASIO SDK only if the deferred ASIO option is revisited.)"),
("Tracker / MIDI","libopenmpt (BSD); MeltySynth (MIT) with the bundled GeneralUser GS SoundFont (permissive licence); users can choose their own SF2."),
("Time-stretch","SoundTouch (LGPL, dynamically linked) for tempo change with pitch kept."),
("BPM, beats, breaks","Own C# implementation (onset detection, tempo estimation, energy-based break detection). Avoids aubio (GPL)."),
("Loudness / resampling","libebur128 (MIT) or own R128 code; FFmpeg swresample or libsoxr (LGPL) for resampling."),
("DSP / FFT","NWaves (MIT) or own code for EQ, filters, compressor and convolution; FftSharp (MIT) for spectrum/oscilloscope."),
("VST3","VST3 SDK (MIT since version 3.8, October 2025) through a small native C++ host shim."),
("Tags","TagLibSharp (LGPL, separate DLL); FFmpeg metadata as fallback."),
("Chromecast","SharpCaster (open source) or own Cast v2 client."),
("DLNA / UPnP / mDNS","Own SSDP, mDNS and AVTransport SOAP code (no library)."),
("AirPlay","No mature free .NET sender library; own RAOP (AirPlay 1) implementation needed."),
("Streaming server","Kestrel / ASP.NET Core (MIT), embedded."),
("Video encoding (cast modes)","FFmpeg (LGPL build) with the operating system's H.264 encoders (Media Foundation on Windows, VideoToolbox on macOS, VA-API on Linux) or a free software encoder such as libvpx (BSD) or OpenH264 (BSD); never x264 (GPL). Chosen in P11 after testing with the cast devices."),
("Local database","SQLite via Microsoft.Data.Sqlite (MIT)."),
("System integration","Global hotkeys: Windows RegisterHotKey, SharpHook (MIT) on macOS/Linux; SMTC through the Windows SDK projection (Windows); Tmds.DBus (MIT) for MPRIS (Linux); native interop for macOS Now Playing."),
("Logging","Serilog (Apache 2.0)."),
]
story+=[PageBreak(),*HH1("15. Technology stack and licence policy"),
 Paragraph("<b>Licence policy.</b> Every third-party component must be free and open source, except for the closed-source freeware exception below.",B),Spacer(1,3),
 table(lic,head=("Category","Licences")),Spacer(1,6),
 Paragraph("<b>Chosen stack.</b> Licences must still be checked when each package is added.",B),Spacer(1,3),
 table([(a,b.replace("{WO}",WO)) for a,b in tech],head=("Area","Library / approach"))]

res=[
("UI framework","Avalonia 12 (cross-platform, MIT); moved from 11.3 before the skin engine was built. WPF dropped because it is Windows-only."),
("Audio engine","FFmpeg (decoding) + miniaudio (output). BASS/ManagedBass dropped because it is not free for commercial use; NAudio dropped because its output is Windows-only."),
("VST","VST3 only. No VST2 (the VST2 SDK is no longer available)."),
("AirPlay","Original AirPlay (AirPlay 1 / RAOP) to begin with. AirPlay 2 later, if at all. No test device is available."),
]
res+=[
("Skin format","AudioTastic's own skin format only. No import of classic Winamp .wsz skins."),
("Closed-source freeware","Allowed only when no open-source library exists, with no licence key and no cost to the end user."),
("OptimFROG","Supported through the closed-source freeware OptimFROG SDK, as a separate input plugin. Its licence allows bundling the unmodified libraries; builds exist for x64 only (Windows, Linux, macOS)."),
("MIDI SoundFont","GeneralUser GS v2.0.3 is bundled (permissive licence, 32 MB); FluidR3_GM (MIT, 148 MB) was considered."),
("Bindings and libraries","FFmpeg through FFmpeg.AutoGen; miniaudio through an own small C shim; tests with xunit v3."),
("Player look","Shown at 150 % of the skin's layout size by default (adjustable 100–200 %); two-row mini player."),
("Logo / visual identity","The supplied mushroom-cloud planet logo is the app icon and the base for the default skin."),
]
later=[
("Smart playlists","Deferred; to be revisited later. Would need a small track database (play count, last played, date added)."),
("Auto-update","Not included."),
("Media library, ratings and play count","Not included."),
("Localisation","Not included."),
("VST2","Not included (VST3 only)."),
("AirPlay 2","Not included for now (original AirPlay only)."),
("MilkDrop-style visualisation plugin","Planned later by Anders, as a visualisation plugin (for example on projectM, LGPL, dynamically linked, or own code). P11 prepares for it: visualisation plugins can render frames for casting, so its visuals can be cast to a screen."),
("Scrobbling (Last.fm, ListenBrainz) and Discord Rich Presence","Removed (Anders, 18 November 2026). Feature IDs CN-07 and CN-08 are retired, and no Last.fm, ListenBrainz or Discord accounts or keys are needed. A third-party general plugin (PG-01) could still add them."),
("Discogs cover fallback","Deferred (Anders, 19 November 2026); may be revisited later. Online lookup uses MusicBrainz and the Cover Art Archive, which need no account. Discogs would need each user's personal access token."),
("ASIO output (Windows)","Optional, deferred (decided 18 November 2026); may be revisited later. Bit-perfect output on Windows uses WASAPI exclusive mode. Would need the Steinberg ASIO SDK under its free proprietary licence (signed by the developer) and an ASIO device or ASIO4ALL for testing."),
]
story+=[KeepTogether([*HH1("16. Decisions"),table(res,head=("Decision","Outcome")),Spacer(1,6),
 *HH1("17. Out of scope / deferred"),table(later,head=("Feature","Status"))])]


T.build("AudioTastic_Required_Features.pdf","Required Features","Required features specification",
 "Version 1.16  ·  19 November 2026", story, short_title="Required Features  ·  v1.16")
