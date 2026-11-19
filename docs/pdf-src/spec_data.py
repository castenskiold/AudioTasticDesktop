# Feature specification content. Each feature: (id, name, what, ui, build, settings)
# UI references (MW1, PW3, EQ2 ...) point to the UI map in section 2.

SECTIONS = []

def S(title, intro, feats):
    SECTIONS.append((title, intro, feats))

S("Core playback", "The basic player functions every other feature builds on.", [
("CO-01", "Application shell (cross-platform)",
 "Starts the player, owns the window set (main, playlist, equalizer, art & lyrics, mini player), loads settings, starts background services (analysis, plugin host, casting, IPC) and shuts them down cleanly. Runs on Windows (primary), macOS and Linux from one code base.",
 "No visible UI of its own. On first start, the main window, playlist and equalizer open docked together in their default positions.",
 "<b>Components:</b> AudioTastic.App (Avalonia 12, MIT) with a DI container (Microsoft.Extensions.DependencyInjection, MIT); AudioTastic.Core, .Data, .Platform.* projects. "
 "<b>Platform:</b> one IPlatformServices implementation per OS, picked at start-up via OperatingSystem.IsWindows()/IsMacOS()/IsLinux(). "
 "<b>Packaging:</b> self-contained publish per runtime identifier (win-x64, win-arm64, osx-arm64, osx-x64, linux-x64, linux-arm64) with native libraries (FFmpeg LGPL build, miniaudio, SoundTouch, libopenmpt) in the runtimes folder. "
 "<b>Data paths:</b> %APPDATA%\\AudioTastic (Windows), ~/Library/Application Support/AudioTastic (macOS), ~/.config/AudioTastic (Linux), or the portable folder (TL-05).", None),
("CO-02", "Transport controls",
 "Play starts or resumes the selected/current track; Pause toggles pause; Stop stops and resets to the track start; Previous goes to the previous track (or restarts the current track if more than 3 s has played); Next goes to the next track (respecting shuffle, repeat and the queue). Open adds files and starts playing them.",
 "MW8: buttons Previous, Play, Pause, Stop, Next, Open. Also in the main menu (MW1 logo), tray menu (SI-04), OS media controls (SI-02), hotkeys (SI-01) and CLI (SI-08). Keyboard (Winamp defaults): Z previous, X play, C pause, V stop, B next, L open.",
 "<b>Components:</b> PlaybackEngine (state machine: Stopped, Playing, Paused, Buffering) in AudioTastic.Core; commands exposed through a single IPlayerController used by UI, plugins, IPC and remote control. "
 "<b>Depends on:</b> FM-01 decoding, PB-04 output, PL-01 playlist. "
 "<b>Seeking:</b> FFmpeg av_seek_frame plus decode-to-position for sample accuracy.", None),
("CO-03", "Shuffle",
 "When on, the next track is chosen from a shuffled order of the playlist. The order is generated once (Fisher–Yates) when shuffle is switched on or the playlist changes, so every track plays once before any repeats. Previous walks back through the shuffled history.",
 "MW10: SHUF toggle button (filled white when on). Keyboard S. Main menu › Playback.",
 "<b>Components:</b> PlayOrder service in Core, holding the shuffled index list per playlist tab. "
 "<b>Persistence:</b> shuffle state saved in settings.", "Saved as part of the playback state (not a settings page item)."),
("CO-04", "Repeat",
 "Three modes, cycled by the button: Off, Repeat playlist, Repeat track. Repeat track replays the current track forever; Repeat playlist starts again from the top after the last track.",
 "MW10: REP button; a small '1' badge shows repeat-track mode. Keyboard R. Main menu › Playback.",
 "<b>Components:</b> PlayOrder service (CO-03). Works with crossfading (repeat track crossfades into itself only if crossfade is on).", None),
("CO-05", "Volume and mute",
 "Sets output volume from 0–100 % on a perceptual (logarithmic) curve. Mute silences output without losing the volume level. Volume is applied after the DSP chain, before output; in exclusive / bit-perfect mode it is applied as digital gain (with a warning icon that bit-perfect is lost when not at 100 %).",
 "MW9: horizontal volume slider; mouse wheel over the main window changes volume; click the speaker icon to mute. Keyboard Up/Down. The mini player has a speaker icon (click mutes; mouse wheel changes the volume).",
 "<b>Components:</b> GainStage in the mixer graph (Core). Volume of cast devices is controlled through the cast protocol instead (CN-01/02/03).", None),
("CO-06", "Time display",
 "Shows the playback position as m:ss (h:mm:ss for tracks over an hour). Clicking it toggles between elapsed time and remaining time (shown with a leading minus sign). For streams without a length it always shows elapsed time. Blinks while paused.",
 "MW3: large digits in the display panel, left side. Mini player shows a compact version.",
 "<b>Components:</b> TimeDisplay custom Avalonia control drawn with the skin's digit font or vector glyphs; updated from the PlaybackEngine position at 10 Hz. "
 "<b>Persistence:</b> elapsed/remaining choice saved in settings.", None),
("CO-07", "Waveform display and seek",
 "Shows the whole current track as a waveform (min/max peaks) with the played part drawn brighter than the rest. Clicking or dragging on it seeks to that position. While the waveform is being generated, a thin progress line is shown; streams show a scrolling live waveform instead. Waveforms are cached so a track only needs analysing once.",
 "MW7: full-width strip directly beneath the display panel (beneath the time display).",
 "<b>Components:</b> WaveformAnalyzer in AudioTastic.Analysis (decodes the file through the input plugins on the analysis queue, reduces to 2,000 min/max pairs per channel); WaveformControl (Avalonia), drawn scaled to the track's own peak; skin element type 'waveform' (trackColor, fillColor, thumbColor); AnalysisCoordinator analyses the current track first and prefetches the next; live streams draw a lock-free peak tap after the deck mix. "
 "<b>Cache:</b> SQLite table keyed by file path + size + modified time, peaks stored as a compressed blob. "
 "<b>Depends on:</b> FM-01, AN-01 analysis queue.", None),
])

S("Audio formats and sources", "All decoding goes through one FFmpeg-based decoder unless stated. Each format below must open, play, seek and show tags (where the format has them).", [
("FM-01", "Core formats: WAV, FLAC, MP3, AIFF, APE, AAC, WV",
 "Plays these formats at their native sample rate and bit depth. Handles gapless information in MP3 (LAME header) and AAC/M4A (iTunSMPB / edit lists) so tracks start and end sample-accurately.",
 "Files are added via Open (MW8), Add (PW5), drag and drop (PL-04), file associations (SI-05) or CLI (SI-08). Format shown in MW5 (e.g. FLAC 24/96).",
 "<b>Libraries:</b> FFmpeg (LGPL build, dynamically linked) through the FFmpeg.AutoGen (MIT) bindings, version matched to the FFmpeg major: avformat for demuxing, avcodec for decoding; decoded frames of any sample format are converted to interleaved float by own code (swresample is kept for resampling, PB-01/PB-05). "
 "<b>Components:</b> FfmpegDecoder implementing the SDK's IAudioSource (Open, Read float frames, Seek, Duration, Format, Tags). Registered as the default input plugin (PG-01).", None),
("FM-02", "Ogg Vorbis and Opus", "Plays .ogg/.oga (Vorbis) and .opus files, including chained Ogg streams.",
 "As FM-01.", "<b>Libraries:</b> FFmpeg built-in Vorbis decoder and libopus (BSD) in the FFmpeg build. Opus always decodes at 48 kHz.", None),
("FM-03", "ALAC and M4A", "Plays Apple Lossless and AAC in MP4 containers (.m4a, .mp4, .alac).", "As FM-01.", "<b>Libraries:</b> FFmpeg mov demuxer and alac/aac decoders.", None),
("FM-04", "WMA", "Plays Windows Media Audio: WMA 1/2, WMA Pro and WMA Lossless (.wma, .asf).", "As FM-01.", "<b>Libraries:</b> FFmpeg asf demuxer and wmav1/wmav2/wmapro/wmalossless decoders. DRM-protected files are not supported and show an error.", None),
("FM-05", "DSD (DSF / DFF)",
 "Plays DSD files. Default: converted to PCM (FFmpeg dsd decoders, at DSD rate / 8, i.e. 352.8 kHz for DSD64; resampled when the device cannot play that rate). Optional: DoP (DSD over PCM) output to DACs that support it, which sends the raw DSD bits packed in 24-bit PCM frames with alternating 0x05/0xFA markers, at DSD rate / 16 (176.4 kHz for DSD64). "
 "While DoP plays nothing may alter the samples: volume, ReplayGain, fades and speed are bypassed (mute still works), and there is no gapless preload into or out of a DoP track. If the device has no 24/32-bit exclusive format at the DoP rate, or refuses exclusive mode, the file plays as PCM instead.",
 "As FM-01. MW5 shows DSD64/DSD128 (PCM) or DSD64 + DOP when DoP is active.",
 "<b>Libraries:</b> FFmpeg dsf/iff demuxers and dsd_lsbf/dsd_msbf decoders for PCM conversion. "
 "<b>Components:</b> DopSource (own code, FFmpeg plugin) reading raw DSD packets from the demuxer, sample-exact seeking; the plugin implements IDopSourceFactory (SDK) so the engine can ask for DoP. DoP needs exclusive output mode (PB-04). Verified by comparing the 24-bit words a (fake) exclusive device receives with the raw DSD bitstream.",
 "Settings › Output: DSD mode (Convert to PCM / DoP)."),
("FM-06", "Musepack, TTA, Shorten, OptimFROG",
 "Plays Musepack (.mpc), True Audio (.tta), Shorten (.shn) and OptimFROG (.ofr/.ofs).",
 "As FM-01.",
 "<b>Libraries:</b> FFmpeg musepack7/musepack8, tta and shorten decoders. "
 "<b>OptimFROG:</b> FFmpeg has no OptimFROG decoder, so OptimFROG uses the closed-source freeware OptimFROG SDK (native library per platform, called via P/Invoke), allowed under the licence-policy exception (no open-source option, no licence key, free for the end user). "
 "Built as a separate input plugin (OptimFrogSource) so the rest of the app has no dependency on it. <b>Redistribution (checked 17 Nov 2026):</b> the OptimFROG licence allows bundling the unmodified binary libraries under their original names (OptimFROG.dll, libOptimFROG.so, libOptimFROG.dylib) with software that uses them via the SDK, for free and commercial software, without notification; the library is bundled with its license.html. Version 5.100 / SDK 1.600 has builds for Windows x64, Linux x64 and macOS x64 only, so ARM64 builds of AudioTastic do not list the format. Packages are verified against published SHA-1 checksums.", None),
("FM-07", "Tracker modules (MOD, XM, IT, S3M)",
 "Plays tracker music, including the module's own title. Loops are played once, then the track ends (so the playlist continues).",
 "As FM-01. MW5 shows the module type and channel count instead of bitrate (e.g. 'XM 8ch').",
 "<b>Libraries:</b> libopenmpt (BSD) via P/Invoke (openmpt_module_create_from_memory2, openmpt_module_read_interleaved_float_stereo at 48 kHz, set_repeat_count 0); shipped inside the plugin folder per runtime (official Windows builds; built from source on Linux/macOS). "
 "<b>Components:</b> OpenMptSource as a separate input plugin. Interpolation and stereo separation use libopenmpt defaults.", None),
("FM-08", "MIDI",
 "Plays .mid/.midi/.kar/.rmi files through a SoundFont (SF2) synthesizer. A General MIDI SoundFont is bundled; the user can choose another. Seeking fast-forwards the sequence with new notes muted.",
 "As FM-01. The SoundFont is chosen on the plugin's settings page (Settings › Plugins › MIDI synthesizer › Settings…).",
 "<b>Libraries:</b> MeltySynth (MIT, pure C#). <b>Bundled SoundFont:</b> GeneralUser GS v2.0.3 by S. Christian Collins (32 MB; custom permissive licence: free use in software projects, private or commercial, no attribution required; its LICENSE.txt is shipped). Chosen over FluidR3_GM (MIT, but 148 MB). "
 "<b>Components:</b> MidiSource input plugin rendering at 44.1 kHz; the SoundFont is loaded once and shared.", "Settings › Plugins › MIDI synthesizer: SoundFont file (default: the bundled GeneralUser GS)."),
("FM-09", "CUE sheets",
 "A single-file album rip with a .cue sheet (or a CUESHEET tag embedded in FLAC/APE/WV) appears as separate tracks with their own titles, performers and track numbers. Seeking, time display and the waveform work per virtual track. Gapless between virtual tracks is automatic (same file).",
 "Opening a .cue file, or a file with an embedded cue sheet, adds one playlist entry per track (PW4).",
 "<b>Components:</b> CueParser (own code; FILE, TRACK, TITLE, PERFORMER, INDEX 01 mm:ss:ff at 75 frames per second, REM GENRE/DATE); VirtualTrack (file + start/end sample) used by the playlist and PlaybackEngine. "
 "<b>Text encoding:</b> UTF-8 with fallback to the system code page.", None),
("FM-10", "Internet radio and streams",
 "Plays HTTP, Icecast and SHOUTcast streams (MP3, AAC, Ogg, Opus, FLAC). Shows the now-playing title from ICY metadata in the scrolling title. Reconnects automatically after drops (up to 5 tries with back-off). Opening an .m3u/.pls with stream URLs adds them as stream entries.",
 "Main menu › Open URL… (Ctrl+L) opens a dialog with a URL field; a link to an .m3u/.pls/.xspf is downloaded and its entries added. Stream entries in the playlist show a stream icon and no duration. MW5 shows the stream bitrate; the scrolling title shows the now-playing title, else the station name.",
 "<b>Libraries:</b> FFmpeg http protocol with icy=1 (reads icy_metadata_packet). "
 "<b>Components:</b> LiveStreamSource wrapper (in the FFmpeg plugin) adding buffering (2 s, again after stalls), reconnect logic (up to 5 tries, back-off 0.5–8 s) and metadata change events (IStreamMetadata in the SDK).", None),
("FM-11", "Audio CD playback and ripping",
 "Lists audio CD tracks, plays them, and rips them to files. Track names come from CD-Text or MusicBrainz disc ID lookup (MD-03). Ripping reads raw audio sectors, writes WAV, then converts with TL-01 to the chosen format and tags the result.",
 "Main menu › Play CD (lists drives). Rip: playlist context menu on CD tracks › Rip… opens the conversion dialog (TL-01) pre-filled.",
 "<b>Windows (first):</b> DeviceIoControl on \\\\.\\X: with IOCTL_CDROM_READ_TOC_EX and IOCTL_CDROM_RAW_READ (own P/Invoke code). "
 "<b>macOS:</b> audio CDs are mounted as .aiff files under /Volumes and play through FM-01. "
 "<b>Linux:</b> ioctl CDROMREADTOCHDR/CDROMREADAUDIO via P/Invoke (no GPL libraries such as libcdio/cdparanoia). "
 "<b>Components:</b> ICdDrive in AudioTastic.Platform; MusicBrainz disc ID calculation (own code, SHA-1 of the TOC).", None),
])

S("Playback engine", "Engine-level behaviour. All features in this section are configured in Settings › Playback or Settings › Output.", [
("PB-01", "Gapless playback",
 "The next track is opened and pre-decoded before the current one ends, so there is no silence between tracks. Encoder delay and padding are removed (FM-01). If the next track has a different sample rate, it is resampled to the current output rate; if the setting 'Match output rate to each track' is on, the device is reopened instead (which causes a short gap).",
 "No UI apart from the setting.",
 "<b>Components:</b> two decoder 'decks' in the MixerGraph (also used by crossfading, XF-01); preload starts 10 s before the end into the idle deck, which is 'armed' and started by the mixer in the same render call in which the current deck runs out, so the join is sample-exact. The next deck gets its own ReplayGain before it starts. No preload for streams, during A-B repeat, with stop after current, or for DoP. "
 "<b>Resampling:</b> own windowed-sinc resampler (Kaiser window, ResamplingSource) per deck; no extra native library. Verified by sample comparison for MP3, AAC, FLAC, Opus, Vorbis and CUE tracks.", "Settings › Playback: Gapless (on/off); Settings › Output: Match output rate to each track."),
("PB-02", "Fade on pause, stop and seek",
 "Applies a short volume ramp instead of a hard cut when pausing, stopping or seeking (and a fade-in when resuming). Separate from DJ-style crossfading.",
 "No UI apart from settings.",
 "<b>Components:</b> FadeStage after the master volume in the MixerGraph: exponential ramps (linear in dB, from −60 dB) with no allocations on the audio thread; seek = fade out, seek, fade in; a new track always starts at full level.",
 "Settings › Playback: on/off and fade length per action (default on, 150 ms, range 10–2,000 ms)."),
("PB-03", "ReplayGain and loudness normalisation",
 "Adjusts playback volume so tracks sound equally loud. Modes: Off, Track gain, Album gain, Auto (album gain when consecutive tracks come from the same album, otherwise track gain). Reads REPLAYGAIN_* tags (and R128_* in Opus); for untagged files, it can analyse loudness (EBU R128) in the background and cache the result. Prevents clipping by limiting gain to the track peak.",
 "MW5 shows 'RG' when a gain is being applied. File info dialog (MD-05) shows the values.",
 "<b>Libraries:</b> own EBU R128 / BS.1770-4 implementation (K-weighting for any sample rate, 400 ms blocks, absolute and relative gates; checked against FFmpeg's ebur128 within 0.1 LU); TagLibSharp for tags. "
 "<b>Components:</b> LoudnessAnalyzer (AN-01 queue; keeps every block's energy so album loudness is exact); ReplayGainService decides the gain (tags first, then analysis, reference −18 LUFS; Auto = album gain when a neighbouring playlist entry is from the same album); the engine applies it to the deck's GainStage before the track starts. Optionally writes analysed values back to tags (off by default). ReplayGain is off by default.",
 "Settings › Playback: mode, pre-amp for tagged files (dB), pre-amp for untagged files (dB), analyse untagged files (on/off), prevent clipping (on/off), write tags (on/off)."),
("PB-04", "Output device selection and exclusive mode",
 "Chooses which audio device plays the sound and how. Shared mode (default) mixes with other apps. Exclusive / bit-perfect mode sends the audio unchanged to the device (no OS mixer, no resampling, volume at 100 %). (ASIO output on Windows is optional and deferred; it may be revisited later.) The device list updates when devices are plugged in or removed; if the selected device disappears, playback falls back to the default device.",
 "Settings › Output. Right-click the volume slider (MW9) for a quick device menu.",
 "<b>Libraries:</b> miniaudio (public domain/MIT-0) through a small own C shim (native/miniaudio, flat API with opaque handles) and P/Invoke: WASAPI shared/exclusive (Windows), CoreAudio (macOS), ALSA/PulseAudio/PipeWire (Linux). "
 "<b>Exclusive per platform:</b> WASAPI exclusive (miniaudio share mode exclusive); macOS 'hog mode' needs own CoreAudio interop (not in miniaudio); Linux opens an ALSA hw: device directly. "
 "<b>ASIO (optional, deferred 18 Nov 2026; may be revisited):</b> if revisited, a small native C++ shim around the Steinberg ASIO SDK using Steinberg's free proprietary ASIO licence (not the GPLv3 option, which would make the app GPL) [Windows only]. "
 "<b>Components:</b> IAudioOutput (SDK) with OutputOptions (exclusive, sample format F32/S16/S24/S32, dither); the shim (API v2) opens the device in the requested format, and integer formats are converted exactly in our code. If exclusive mode is refused (another app holds the device, or the endpoint has no exclusive mode, e.g. Remote Desktop audio), playback continues in shared mode. "
 "<b>Output plugins (PG-01):</b> the engine plays through an OutputRouter that lists the built-in devices plus each enabled output plugin's devices as 'Plugin name: device' and forwards every call to the owner. A refusal from a plugin (InvalidOperationException) falls back like the built-in output; any other exception is a plugin fault (PG-03) and playback falls back to the default device; switching the plugin off while it plays continues on the default device.",
 "Settings › Output: device, mode (Shared / Exclusive), buffer size (ms), bit depth (Auto / 16 / 24 / 32)."),
("PB-05", "High-resolution audio",
 "Plays 24-bit and 32-bit (integer and float) audio at sample rates up to 384 kHz without forced resampling, when the device supports it. The pipeline is 32-bit float internally; output is converted to the device's best format.",
 "MW5 shows the source format (e.g. 24 BIT, 192 KHZ); the second badge row shows 'OUT' if the device runs at another rate than the source.",
 "<b>Components:</b> format negotiation in the engine (device formats from miniaudio): exclusive mode picks the source rate if the device has it (else the nearest higher, else the highest, resampled) and the smallest integer format that holds the source's bits; TPDF dither only when reducing bit depth. Bit-perfect verified: 16- and 24-bit WAV reach a (fake) exclusive device sample-identical at 100 % volume. "
 "<b>Depends on:</b> PB-04 (exclusive mode needed for true bit-perfect).", "Settings › Output: match output rate to each file (on/off), allow resampling (on/off), dither (on/off)."),
("PB-06", "Playback speed and pitch",
 "Changes playback speed from 0.5× to 2.0×. 'Keep pitch' on: tempo changes, pitch stays (time-stretch). 'Keep pitch' off: speed and pitch change together, like a turntable.",
 "Main menu › Playback › Speed… opens a small dialog with a slider (0.05 steps), Keep pitch checkbox and Reset; changes apply at once. The second badge row in MW5 shows the speed (e.g. 1.25×) when not 1.0×.",
 "<b>Libraries:</b> SoundTouch 2.4.1 (LGPL, dynamically linked SoundTouchDLL) through own P/Invoke: setTempo for keep-pitch, setRate otherwise. "
 "<b>Components:</b> TimeStretchSource per deck (shared with beat matching, XF-02); bypassed exactly at 1.0× (bit-identical output); positions are mapped back to the source through checkpoints, so the time display and seeking stay in track time.", "Settings › Playback: remember speed per file (on/off)."),
("PB-07", "Resume position",
 "On restart, the player reopens the last playlist, track and position (paused or playing, as set). For long files (longer than a set length, default 20 min, e.g. podcasts and audiobooks), the position is remembered per file and restored when that file is played again.",
 "Playlist context menu › Reset resume position for files that have one (shown with a '»' marker in PW4).",
 "<b>Components:</b> ResumeStore in cache.db (kinds 'resume' and 'speed', keyed like the other cached data); the session (playlist id, index, path, position, playing) is kept in settings.json. Positions are saved every 10 s and on exit; a file within 10 s of its end is cleared.",
 "Settings › Playback: on start (off / reopen the last track paused (default) / reopen and play); remember the position in long files (on/off) and their minimum length (default 20 min)."),
("PB-08", "A-B repeat",
 "Loops a section of the current track. First press sets point A at the current position, second press sets point B and starts looping, third press clears the loop. A and B can be dragged on the waveform.",
 "Main menu › Playback › A-B repeat (keyboard: [ ). A and B markers and a lightly shaded region appear on the waveform (MW7); dragging a marker moves it. The second badge row in MW5 shows 'A-B', and each press is confirmed in the title area.",
 "<b>Components:</b> LoopSource in the deck's source chain (sample-exact jump back to A, no gap); the loop is at least 1 s long. Disabled for streams.", None),
("PB-09", "Queue and stop after current",
 "'Play next' puts tracks in a queue that plays before the normal playlist order, in the order queued. 'Stop after current' stops playback when the current track ends (one-shot; switches itself off).",
 "Playlist context menu › Play next (keyboard Q; Shift+Enter in the search box queues the result) and Remove from queue; queued entries show their queue position, e.g. [1], in PW4, and queuing is confirmed in the title area. Main menu › Playback › Stop after current (Shift+V) and Clear queue; playlist context menu › Stop after this track. The second badge row in MW5 shows 'STOP AFTER' when set.",
 "<b>Components:</b> QueueService in Core, consulted before shuffle/repeat (also for gapless preloading).", "Settings › Playback: clear queue on stop (on/off)."),
("PB-10", "Sleep timer",
 "Stops playback after a chosen time (15, 30, 45, 60, 90 min or custom) or at the end of the current track or playlist. Optionally fades out over the last 30 s. Optionally closes the app afterwards.",
 "Main menu › Sleep timer… (dialog: 15/30/45/60/90 min, custom, end of track, end of playlist, turn off); tray menu in P10. The second badge row in MW5 shows 'SLEEP n MIN' (or 'SLEEP' for end of track/playlist).",
 "<b>Components:</b> SleepTimerService (Core) using PeriodicTimer; the fade-out lowers the master gain over the last n seconds.", "Settings › Playback: fade out (on/off), fade length, close app when finished."),
])

S("DJ-style crossfading", "When the current track nears its end, the next track starts and the two are mixed like a DJ set. All settings are in Settings › Crossfading. The XF button (MW10) switches crossfading on/off; a badge shows the mode (IO = Intro/Outro, BM = Beat matching).", [
("XF-01", "Intro/Outro mode",
 "The current track fades out while the next track fades in over it. The overlap starts 'Overlap seconds' before the end of the current track; at the end of the overlap the current track is silent and the new track is at full volume. Uses equal-power curves so the loudness stays even. If either track is shorter than twice the overlap, the overlap is shortened to fit. With 'Bass fade-in' on, the new track's bass (below 200 Hz) starts 24 dB lower and rises to normal over the overlap.",
 "MW10: XF button and IO badge. Waveform (MW7) shows the overlap region as a shaded area at the end of the track.",
 "<b>Components:</b> CrossfadePlanner (pure planning, unit-tested) and PlaybackEngine in Core; MixerGraph with two decks (A/B), each with its own GainStage (ReplayGain), DeckEnvelope (fade) and BassRamp (low shelf), summed before the DSP chain. The incoming deck starts at an exact frame of the outgoing deck (MixerGraph.NextStartFrame). "
 "<b>Depends on:</b> PB-01 preloading, PB-03 (gain applied per deck so both tracks are normalised).",
 "Overlap duration (seconds, 0.5–30, default 8); Bass fade-in (on/off, default on)."),
("XF-02", "Beat matching mode",
 "Changes the tempo of the incoming track (keeping its pitch) to the BPM of the playing track and lines up the beats. The incoming track fades in over 'Fade-in seconds' (equal-power; with 'Bass fade-in' its bass starts 24 dB lower and rises to normal over the fade-in), then both play together at full volume for 'Overlap seconds'. When the overlap ends, the first track either stops at once (Cut, a 10 ms ramp) or fades out over 'Fade-out seconds' (Fade out). "
 "<b>Mix point:</b> the phrase boundary (8 beats) at or before 'Mix before beat end' seconds before the playing track's beat end (shifted earlier if the fade-in, overlap and fade-out would not fit); the incoming track enters on the phrase boundary at or after 'Mix in after beat start' seconds past its own beat start. After a seek past the mix point, the next phrase at least 3 s ahead is used. "
 "<b>Alignment:</b> the beat grids place the incoming track, corrected by both grids' local phase corrections over the full-volume overlap. Then the audio of the overlap itself is measured (CrossfadeAligner): in each track the high-band envelope is averaged over all beats there, the kick is found with the analyser's kick detector, and the beat is the strongest transient from 150 ms before that kick to 20 ms after it. The incoming entry moves by the difference (up to half a beat) when both halves of the overlap agree; otherwise the grids decide. Blind listening tests on Anders's tracks chose this point over the click alone and over the low thump.",
 "MW10: XF button and BM badge. During a mix, MW5 shows both BPMs (e.g. 150 → 153). The waveform (MW7) shades the mix region. Playlist row menu: 'Adjust beat grid…' moves a file's beat grid by ±250 ms (stored per file) when the automatic alignment is wrong for it.",
 "<b>Libraries:</b> SoundTouch for tempo change with pitch kept (TimeStretchStage, shared with PB-06). "
 "<b>Components:</b> CrossfadePlanner (tempo ratio, mix point, entry point and grid alignment, unit-tested); ICrossfadeAligner / CrossfadeAligner (Analysis; decodes the overlap of both files off the audio thread when the transition is planned); PlaybackEngine (preload, per-deck speed, envelopes, exact start frame; plans again if the mix point passes while aligning). "
 "<b>Depends on:</b> AN-02 (BPM, beat grid, beat start/end and breaks; the engine waits for the analysis while there is time, otherwise this transition uses Intro/Outro). Beat matching needs 'keep pitch' (PB-06) or speed 1.0×. "
 "<b>Tools:</b> tools/AudioTastic.MixLab renders transitions offline through the real engine, prints the measured alignment and writes click tracks for listening tests.",
 "Fade-in duration (seconds, 0.5–30, default 8); Bass fade-in (on/off, default on); Overlap duration (seconds, 0.5–30, default 8); End behaviour (Cut / Fade out, default Fade out); Fade-out duration (seconds, default 4, only for Fade out); Mix before beat end (seconds, default 30); Mix in after beat start (seconds, default 0)."),
("XF-03", "Tempo limit",
 "Sets the largest tempo change beat matching may apply. If the needed change is bigger than the limit, that transition uses Intro/Outro instead. Half/double-time matches (e.g. 70 vs 140 BPM) are considered before giving up. Beat matching also needs analysis confidence of at least 0.5 for both tracks; below that it uses Intro/Outro.",
 "No UI apart from the setting; the log names the reason for each fallback.", "<b>Components:</b> CrossfadePlanner decision step (tempo factors 1, 2 and ½).", "Tempo limit (percent, 1–20, default 8)."),
("XF-04", "Tempo return",
 "After the first track has ended, the new track goes back to its original BPM. 'Cut at next break': jumps to the original tempo at the next detected break, where the change is least noticeable. 'Gradual': changes smoothly over 'Tempo return seconds'. If no break is found, it falls back to Gradual, or switches at the next phrase boundary.",
 "MW5 BPM readout shows the tempo moving back.",
 "<b>Components:</b> PlaybackEngine tempo timer driving the deck's TimeStretchSource (ramped every 20 ms for Gradual; a per-deck speed override keeps the playing speed setting apart). "
 "<b>Depends on:</b> AN-02 break detection.",
 "Tempo return (Cut at next break / Gradual, default Gradual); Tempo return duration (seconds, 1–120, default 16, only for Gradual)."),
("AN-01", "Background analysis queue",
 "Shared job queue for waveform (CO-07), loudness (PB-03) and BPM/beat/break analysis (AN-02). Prioritises the current and next tracks, runs at low priority with a configurable number of worker threads, and skips files whose cached results are still valid.",
 "Optional status text in the playlist bottom bar (PW6): 'Analysing 3/120'.",
 "<b>Components:</b> AnalysisService in AudioTastic.Analysis: a priority queue (current, next, background) with merged requests and dedicated worker threads; one decode per file feeds every requested analyser; the current track's job runs at normal priority, others below normal; results in CacheStore (SQLite via Microsoft.Data.Sqlite), CUE virtual tracks cached separately.", "Settings › General: analysis threads (default 2)."),
("AN-02", "Track analysis: BPM, beat grid and breaks",
 "Analyses each track once and stores its BPM, beat grid (first beat and period), local phase corrections per 32 beats, phrase boundaries (8 beats), beat start and beat end (where the steady drums begin and stop) and breaks (16 beats or more with little low-frequency energy). Runs in the background for the current playlist, the current and next tracks first.",
 "Background only. File info dialog (MD-05) shows BPM (P9). Playlist row menu: 'Adjust beat grid…' (a per-file nudge, ±250 ms).",
 "<b>Components (own C# code; no GPL aubio):</b> BeatAnalyzer (AN-01 analyser kind 'beats', data version 3) and BeatDetector: the signal is decimated to about 11 kHz; log spectral flux onsets; tempo by autocorrelation with a prior around 130 BPM, refined by folding; phase from the kick (a click in the high band across which the low band rises), first over the whole track, then as the most common offset per 32 beats inside the drum sections; confidence from dynamic-programming beat tracking; phrase offset from low-frequency rises and accents. "
 "The beat start and end come from low-frequency energy and then move to where the groove is steady: each beat's low-band pattern over its four 16ths must match the median of the next 16 beats in at least 14 (intros whose low hits wander across the 16ths do not count). "
 "<b>Cache:</b> SQLite kind 'beats' (same key as waveforms); the nudge is kind 'beatnudge'. <b>Depends on:</b> AN-01.", "Settings › Crossfading: analyse in background (on/off)."),
])

S("Playlists", "Everything shown in the playlist window (PW).", [
("PL-01", "Playlist editor",
 "Shows the tracks of the active playlist with number, artist – title and duration. Double-click or Enter plays a track. Supports multi-select (Shift/Ctrl), delete, crop (keep selected), move up/down (Alt+arrows), and undo/redo of edits. The now-playing track is highlighted. Playlists are saved automatically.",
 "PW4: track list. PW5 buttons: ADD (files, folder, URL), REM (selected, duplicates, dead, all), SEL (all, none, invert), SORT (PL-06), LIST (new, load, save, export).",
 "<b>Components:</b> PlaylistService (Core) with observable Playlist/PlaylistEntry models; PlaylistView (Avalonia virtualised list for 100,000+ entries). "
 "<b>Persistence:</b> each tab saved as M3U8 plus a JSON state file in the data folder. Tags read lazily via MD-01.", None),
("PL-02", "Multiple playlists (tabs)",
 "Several playlists open as tabs. Each tab has its own entries, scroll position and selection. Tabs can be added, renamed (double-click), reordered and closed. Playing a track makes its tab the 'playing' tab; Next/Previous follow the playing tab even when another tab is shown.",
 "PW2: tab strip at the top of the playlist window with a + button. Right-click a tab: Rename, Duplicate, Save as…, Move left, Move right, Close. The playing tab is marked ▸.",
 "<b>Components:</b> PlaylistService holds a list of playlists and the playing playlist id.", None),
("PL-03", "Playlist formats",
 "Imports and exports M3U, M3U8 (#EXTM3U/#EXTINF), PLS, XSPF, WPL and CUE. Relative paths are resolved against the playlist file's folder; on export the user can choose relative or absolute paths. Unknown or unreadable entries are kept and shown as missing.",
 "PW5 › LIST › Load / Save / Export. Opening a playlist file from the OS (SI-05) loads it into a new tab.",
 "<b>Components:</b> IPlaylistFormat implementations (own code; System.Xml for XSPF/WPL, INI-style parser for PLS); CueParser shared with FM-09. Plugins can add formats.", None),
("PL-04", "Drag and drop",
 "Files and folders dropped from the OS file manager are added at the drop position (folders recursively, playlist files expanded). Entries can be dragged within the list to reorder, and between tabs to copy (Ctrl to move). Dropping on the main window adds to the end and plays.",
 "PW4 and the main window. A white insertion line shows where items will land.",
 "<b>Components:</b> Avalonia DragDrop (DataFormats.Files) handlers; recursive folder scan filtered by known extensions on a background task.", None),
("PL-05", "Search / jump to file",
 "Pressing J opens a search box that filters the active playlist as you type (matches title, artist, album and file name; all words must match). Enter plays the selected result, Shift+Enter queues it (PB-09), Esc closes.",
 "PW3: search field that slides open at the top of the track list; keyboard J from any player window.",
 "<b>Components:</b> SearchFilter over PlaylistEntry tag fields (case- and accent-insensitive via CompareInfo.IndexOf with IgnoreCase|IgnoreNonSpace).", None),
("PL-06", "Sorting and duplicates",
 "Sorts the playlist (or the selection) by title, artist, album, track number, path, file name or duration; reverse and randomise. Removes duplicate entries (same file and same cue range) and dead entries (missing files).",
 "PW5 › SORT menu; PW5 › REM › Remove duplicates / Remove dead.",
 "<b>Components:</b> PlaylistService operations, all undoable (PL-01). Natural sort order for numbers in file names.", None),
("PL-07", "Playlist duration info",
 "Shows the total duration of the playlist and the duration of the current selection (e.g. 4:12 / 1:03:55). Entries with unknown length show a '+' after the total.",
 "PW6: right side of the playlist bottom bar.",
 "<b>Components:</b> computed from cached durations; durations probed by FFmpeg in the background (AN-01).", None),
])

S("Metadata", "Reading, editing and showing information about the tracks.", [
("MD-01", "Tag reading and editing",
 "Reads and writes tags: ID3v1/v2 (MP3, AIFF, WAV), Vorbis comments (FLAC, Ogg, Opus), APEv2 (APE, WV, MPC), MP4 atoms (M4A/ALAC), ASF (WMA). Edits title, artist, album artist, album, track n of m, disc n of m, year, genre, composer, comment, lyrics and the front cover. Several files can be edited at once: fields that differ show '(multiple values)' and are left alone unless typed in; only changed fields are written. Files and formats TagLib cannot write (TTA, DSD, MIDI, tracker modules, streams, read-only files) are left out with a note. "
 "A file the player has open (the current, preloaded or fading track) is written when it is released (next track or stop); a file another reader still holds is tried again for about 3 s and then waits the same way. The waveform, loudness, beats, resume position and other cached data move to the edited file.",
 "Playlist context menu › Edit tags… (Ctrl+E): the selected files, or the playing track when nothing is selected. The dialog is resizable (the lyrics field grows). Changes refresh the playlist rows in every tab.",
 "<b>Libraries:</b> TagLibSharp (LGPL). Formats TagLibSharp cannot write are read-only (read via FFmpeg metadata). "
 "<b>Components:</b> TagService (reading, cached), TagWriter (editable fields, write, CacheStore.Rekey keeps the cached data), TagWriteService (off the UI thread; waits for files in use: PlaybackEngine.IsDecoding), TagEditorWindow. "
 "<b>Tested:</b> every field and the cover round-trip on WAV, AIFF, FLAC, MP3, Opus, Vorbis, AAC/ALAC M4A, WavPack and WMA with the decoded audio bit-identical (P9 exit criterion).", None),
("MD-02", "Album art",
 "Shows the cover of the current track: the embedded front cover (or first picture) first, then an image in its folder named cover, folder, front or albumart (.jpg, .jpeg, .png). A small thumbnail sits in the main window; clicking it opens the Art & Lyrics window with the large image. Without a cover (or with album art off) the logo is shown.",
 "MW2: thumbnail in the display panel. AL1: large art at the top of the Art & Lyrics window (Alt+L, Main menu › Windows), which docks to the right of the main window on first show, snaps, resizes, is hidden in the mini player and remembers its position and size.",
 "<b>Components:</b> ArtService (TagLibSharp pictures + folder lookup), ArtCoordinator (finds and decodes the cover off the UI thread on track changes, after tag writes and when the setting changes; no disk cache needed), skin element <i>art</i> and skin window <i>art</i>. Rendered in the skin's rounded frame (cornerRadius).",
 "Settings › Metadata: album art on/off (default on)."),
("MD-03", "Online lookup",
 "Looks up tags and covers for one file on request. MusicBrainz for tags (recording search by title, artist and length ±10 s; retried without the length when nothing matches; CD disc ID for FM-11 later), Cover Art Archive for the release's front cover. Never overwrites tags without confirmation: matches are listed, the chosen one is shown next to the current tags with a tick per field (ticked where they differ), and Use copies the ticked values and the cover into the tag editor; nothing is written until Save. (A Discogs fallback for covers is deferred.)",
 "Tag editor › Look up online… (single file); playlist context menu › Look up online… opens the tag editor and starts the search.",
 "<b>APIs:</b> MusicBrainz WS/2 (JSON; at most one request per second, also for concurrent requests; User-Agent 'AudioTastic/version ( https://the-end.dk )'), Cover Art Archive (front-500 images; no account). "
 "<b>Components:</b> OnlineLookupService with RequestSpacer (own minimum-interval limiter, tested on a fake clock) and a session cache of answers and covers; LookupWindow.",
 "Settings › Metadata: online lookup on/off, services to use (MusicBrainz tags, Cover Art Archive covers); all on, and lookups only happen when asked for."),
("MD-04", "Lyrics",
 "Shows lyrics for the current track, from (in order) a .lrc file with the same name next to it (UTF-8, else Latin-1), an ID3v2 SYLT frame, or embedded lyrics (USLT, LYRICS, ©lyr), which count as synced when they carry LRC time tags. Synced lyrics highlight the current line, keep it in the middle and seek when a line is clicked; plain lyrics scroll as text. LRC: several time tags per line, [mm:ss.xx] and [mm:ss:xx], [offset:], enhanced word tags ignored. CUE tracks use the whole file's lyrics times.",
 "AL2: lyrics pane below the art in the Art & Lyrics window, taking most of its height (Anders); the mouse wheel scrolls, no scroll bar is drawn. Hidden when the feature is off. Lyrics are edited in the tag editor.",
 "<b>Components:</b> LyricsService and LrcParser (Metadata), LyricsCoordinator (follows the 10 Hz position), skin element <i>lyrics</i>.", "Settings › Metadata: lyrics on/off (default on)."),
("MD-05", "File info dialog",
 "Shows the technical details of a file, read-only: location, type, size and date; codec (with TagLib's description, e.g. MPEG Version 1 Audio, Layer 3), lossless or lossy, sample rate, bit depth, channels, exact length in samples, bitrate (the file's average when the decoder gives none), encoder; ReplayGain from the tags and from our own loudness analysis (LUFS, gain, sample peak); BPM, confidence, beat start/end and breaks; all tags; the CUE range for CUE tracks. Values can be selected; Copy puts everything on the clipboard.",
 "Playlist context menu › File info… (Alt+3); Alt+3 in the main window or a double-click on the format badges (MW5) shows the playing track.",
 "<b>Components:</b> FileInfoReader (decoder info and metadata, TagLib codec description, file size) plus TagService, AnalysisService and BeatInfoService values; FileInfoWindow.", None),
])

S("User interface and skins", "Window layout, skins and the visual elements of the player. See section 2 for the UI map.", [
("UI-01", "Classic window layout and snapping",
 "Main window, playlist editor, equalizer and Art & Lyrics are separate, borderless windows. Dragging one near another (within 10 px) or near a screen edge snaps it into place; docked windows move together when the main window is dragged. Playlist and Art & Lyrics windows can be resized; main and equalizer windows have fixed sizes (scaled by UI-08). Each window can be shown or hidden; positions are saved.",
 "Toggle with MW10 buttons EQ and PL, and main menu › Windows. Keyboard Alt+G (equalizer), Alt+E (playlist), Alt+L (Art & Lyrics).",
 "<b>Components:</b> WindowSnapManager (own code, works on window bounds; it moves the windows itself so it can snap during the drag); Avalonia windows with WindowDecorations=None and custom title bars. The playlist and other docked windows are owned by the main window, so they come to the front and minimise together with it. "
 "<b>Platform note:</b> on Linux, window positioning works under X11/XWayland; native Wayland does not allow apps to position windows, so Avalonia's X11 backend is used.", None),
("UI-02", "Default skin",
 "The built-in look: slim, compact and dark, based on the AudioTastic logo. Monochrome (near-black background, white lines and text, greys for inactive elements), thin outlines with rounded ends, no gradients or bevels. Controls are outline shapes that fill white when pressed or active. The logo sits in the main window title bar.",
 "All player windows. Selected by default in Settings › Interface › Skin.",
 "<b>Components:</b> shipped as a normal skin package (UI-03) built from vector (SVG) assets, so it is sharp at any scale. "
 "<b>Assets:</b> assets/branding (logo, transparent logo). Font: Inter (SIL OFL, free) for text, embedded in the skin.", None),
("UI-03", "Skin engine and skin format",
 "Loads skins that change the look of all player windows. AudioTastic's own format: a .atskin file (zip) containing skin.json (name, author, version, colours, fonts, element positions and sizes per window, and which image draws each element and state), plus SVG or PNG images (PNG @1x and @2x). Skins can be switched while playing. Classic Winamp .wsz skins are not supported.",
 "Settings › Interface › Skin: list with preview thumbnails, Install skin…, Open skins folder. Main menu › Skins for quick switching.",
 "<b>Components:</b> SkinEngine (parse skin.json with System.Text.Json and validate it with NJsonSchema (MIT) against the schema; elements bound to custom Avalonia controls; resizable windows such as the playlist anchor each element to window edges); SVG rendering via Svg.Controls.Skia.Avalonia (MIT). "
 "<b>Deliverables:</b> skin format documentation and JSON schema in /docs, so others can make skins. Invalid skins fall back to the default skin with an error message.", "Settings › Interface: skin."),
("UI-04", "App icon and branding",
 "The AudioTastic logo is the application icon on all platforms and appears in the taskbar/dock, tray, file-type icons, About dialog and title bar.",
 "Everywhere the OS shows the app. Main menu › About AudioTastic opens the About dialog (logo, version, licences of third-party components).",
 "<b>Assets:</b> assets/branding/audiotastic.ico (Windows, set as ApplicationIcon), audiotastic.icns (macOS bundle), png/audiotastic-*.png (Linux hicolor icon theme and Avalonia Window.Icon). "
 "<b>About dialog:</b> lists THIRD-PARTY-NOTICES.md content.", None),
("UI-05", "Mini player",
 "Collapses the whole player to one compact two-row window (not a title bar). Top row: logo (main menu), scrolling title, expand button. Bottom row: elapsed time, seek bar, track length, previous / play-pause (a circle that shows pause while playing) / stop / next, shuffle and playlist toggles, and a speaker icon (click mutes, mouse wheel changes the volume). Other windows are hidden while the mini player is shown and restored when expanding. Can be set to stay on top (off by default).",
 "Toggle: title bar button in MW1, double-click the main window title bar, or Ctrl+W (Winamp's windowshade key).",
 "<b>Components:</b> MiniPlayerWindow (separate skin section in skin.json); WindowSnapManager remembers the full layout.", "Settings › Interface: always on top for mini player and/or main window."),
("UI-06", "Scrolling title and format indicators",
 "Shows 'Artist – Title (duration)' scrolling when it does not fit (pauses briefly at the start). Next to it: bitrate (kbps), sample rate (kHz), mono/stereo/channel count, and status badges (RG, A-B, speed, STOP, sleep timer, cast target, DoP).",
 "MW4: scrolling title. MW5: indicator row below it.",
 "<b>Components:</b> MarqueeText control (smooth pixel scrolling at 30 fps, can be switched to static); IndicatorBar bound to PlaybackEngine state; a row of badges that does not fit is drawn slightly smaller so every badge stays whole.", "Settings › Interface: scroll title (on/off), scroll speed."),
("UI-07", "Visualisations",
 "Built-in spectrum analyser (log-spaced bars with falling peak caps) and oscilloscope, drawn in the display panel. Clicking cycles spectrum → oscilloscope → off. Full-screen or windowed visualisations come from visualisation plugins and open in their own window.",
 "MW6: visualisation area in the display panel. Main menu › Visualisation: Spectrum / Oscilloscope / Off, and Open &lt;plugin&gt;; Ctrl+Shift+K opens the first visualisation plugin or closes the open one.",
 "<b>Components:</b> VisualisationFeed in Core (lock-free ring of the newest 8,192 stereo frames, tapped after the DSP chain and before the volume); own radix-2 FFT and SpectrumAnalyzer (2,048 points, Hann window, log-spaced bars 40 Hz–16 kHz); VisualisationView drawn with Avalonia on the UI thread at the set frame rate (the audio thread only copies samples). No extra library (FftSharp was not needed). "
 "<b>Plugin API:</b> IVisualisationPlugin.Create() returns an IVisualisation whose CreateView() is an Avalonia control shown in its own window; OnAudio receives the newest 2,048 post-DSP frames about 60 times a second (PG-01). Faults close the window.", "Settings › Interface: visualisation type, bar count (8–64, default 20), peak fall speed, frame rate (15–60, default 60)."),
("UI-08", "High-DPI and double size",
 "All windows render sharply at any OS scale (100–300 %). Player size (100–200 %, default 150 %) scales the skinned windows; double size mode doubles that, like Winamp's double-size.",
 "Main menu › Options › Double size (Ctrl+D).",
 "<b>Components:</b> Avalonia per-monitor DPI handling; vector skin assets or @2x PNGs; a ScaleTransform on the window content for double size.", "Settings › Interface: player size, double size."),
("UI-09", "Dark/light mode for dialogs",
 "Non-skinned windows (settings, dialogs, tag editor, plugin manager) follow the OS light/dark setting.",
 "All dialogs.",
 "<b>Components:</b> Avalonia FluentTheme with RequestedThemeVariant=Default (follows the OS), with a dark accent style matching the logo.", "Settings › Interface: dialogs theme (System / Dark / Light)."),
("UI-10", "General settings window",
 "One window holding every setting, grouped as: Playback, Output, Crossfading, Metadata, Casting & network, Interface, System integration, Equalizer & DSP, Plugins, General. Changes apply immediately; each group has 'Reset to defaults'. Settings are stored in one JSON file.",
 "Main menu › Settings… (Ctrl+P). Left: list of groups; right: the selected group's options.",
 "<b>Components:</b> SettingsWindow (Avalonia, FluentTheme); SettingsStore (System.Text.Json, strongly typed settings classes, change notifications, schema version for migrations). Plugins add their own pages (PG-05).", None),
])

S("System integration", "How the player works with the operating system. Platform-specific code lives in AudioTastic.Platform.Windows / .MacOS / .Linux behind shared interfaces.", [
("SI-01", "Global hotkeys and media keys",
 "Configurable system-wide shortcuts that work when the player is not focused (defaults: Ctrl+Alt+Home play/pause, Ctrl+Alt+End stop, Ctrl+Alt+PgUp/PgDn previous/next, Ctrl+Alt+Up/Down volume). Keyboard media keys work on all platforms.",
 "Settings › System integration › Hotkeys: list of actions with a 'press keys' field each.",
 "<b>Libraries:</b> SharpHook (MIT, libuiohook) for global hotkeys. macOS needs Accessibility permission (prompt on first use). Linux: works under X11; Wayland blocks global key capture, so media keys go through MPRIS (SI-02). "
 "<b>Components:</b> IHotkeyService.", "Settings › System integration: hotkeys on/off and key bindings."),
("SI-02", "OS media controls",
 "Publishes now-playing information (title, artist, album, art, position) and accepts play/pause/next/previous/seek from the OS: Windows media overlay, lock screen and Bluetooth headset buttons; macOS Now Playing (Control Centre, Touch Bar, AirPods); Linux MPRIS (desktop media widgets, playerctl).",
 "OS-provided UI only.",
 "<b>Windows:</b> SystemMediaTransportControls via CsWinRT using ISystemMediaTransportControlsInterop.GetForWindow on the main window HWND (TFM net8.0-windows10.0.19041). "
 "<b>macOS:</b> MPNowPlayingInfoCenter and MPRemoteCommandCenter through Objective-C runtime interop (P/Invoke objc_msgSend) or a small Swift helper dylib. "
 "<b>Linux:</b> MPRIS 2 (org.mpris.MediaPlayer2 and .Player) on the session bus via Tmds.DBus (MIT). "
 "<b>Components:</b> IMediaControls.", None),
("SI-03", "Taskbar integration",
 "Windows: thumbnail toolbar buttons (Previous, Play/Pause, Next) in the taskbar preview, and playback progress on the taskbar button. macOS: Dock menu with Play/Pause, Next, Previous and the current title.",
 "Windows taskbar preview; macOS Dock right-click menu.",
 "<b>Windows " + "[Windows only]" + ":</b> ITaskbarList3 COM (ThumbBarAddButtons, SetProgressValue, SetProgressState) via P/Invoke/ComWrappers. "
 "<b>macOS:</b> NSApplication dock menu via native interop (Avalonia NativeMenu on the dock if supported). "
 "<b>Components:</b> ITaskbarService.", "Settings › System integration: show progress on taskbar (on/off)."),
("SI-04", "System tray",
 "Tray icon (menu bar icon on macOS) with a menu: Play/Pause, Stop, Previous, Next, Sleep timer, Show/Hide AudioTastic, Exit. Tooltip shows the current title. Clicking the icon shows/hides the player. Optionally minimises/closes to tray.",
 "OS tray / menu bar.",
 "<b>Components:</b> Avalonia TrayIcon and NativeMenu (built in). Linux tray depends on the desktop environment supporting StatusNotifierItem.", "Settings › System integration: show tray icon, minimise to tray, close to tray."),
("SI-05", "File associations and context menu",
 "Registers AudioTastic as a handler for the supported audio and playlist file types and adds 'Open in AudioTastic' (adds the files and plays the first) and 'Enqueue in AudioTastic' (only adds them) entries, with the app icon, to the file manager's context menu.",
 "Settings › System integration: context menu on/off (built, Windows); file types checklist with Register / Unregister (to come). OS file managers.",
 "<b>Windows context menu (built, 19 Nov 2026):</b> per-user verbs AudioTastic.Play and AudioTastic.Queue under HKCU\\Software\\Classes\\SystemFileAssociations\\&lt;ext&gt;\\shell (MUIVerb, Icon '&lt;exe&gt;,0', MultiSelectModel Player; commands '&lt;exe&gt; --open %1' and '&lt;exe&gt; --enqueue %1') for every extension of the enabled decoder plugins and playlist formats. The written extensions are remembered under HKCU\\Software\\AudioTastic\\ShellIntegration, so entries for dropped types are removed. Rewritten at every start (the program may have moved) and on setting and plugin changes; on by default, off by default in portable mode (TL-05); switching it off removes the entries. No administrator rights. On Windows 11 classic verbs appear under 'Show more options'. <b>Windows file associations (to come):</b> ProgID and RegisteredApplications; Windows 10/11 still asks the user to confirm defaults in Settings › Default apps (open that page for them). "
 "<b>macOS:</b> CFBundleDocumentTypes in Info.plist; a Finder Quick Action (Automator workflow or app extension) for Enqueue. "
 "<b>Linux:</b> .desktop file with MimeType and Actions, installed to ~/.local/share/applications, plus xdg-mime. "
 "<b>Components:</b> IShellIntegration.", "Settings › System integration: file types, context menu on/off."),
("SI-06", "Single instance",
 "Only one AudioTastic runs at a time per user and data folder (a portable copy runs on its own). Opening files when it is already running sends them to the running player, which plays them or enqueues them (setting; --open and --enqueue override it) and comes to the front when files are played or it is started without files. Commands arriving within 300 ms form one batch, sorted by path when they come from several starts (Explorer starts one process per selected file, in no reliable order); commands that arrive while the player starts join its own start-up files. Also used by the CLI (SI-08).",
 "No UI apart from the setting. Enqueueing shows a short 'Enqueued …' notice in the title.",
 "<b>Components (built, 19 Nov 2026):</b> SingleInstance: a named mutex 'Local\\AudioTastic-&lt;hash of user and data folder&gt;' held by a thread of its own (a mutex belongs to the thread that took it; named mutexes work on all three OSes in .NET), and a named pipe of the same name (System.IO.Pipes with CurrentUserOnly; a Unix domain socket on macOS/Linux) carrying one JSON InstanceCommand (action, files) per connection. A second start keeps trying for up to 15 s (the first may still be starting or just exiting), calls AllowSetForegroundWindow on Windows so the player may come to the front, and exits. InstanceInbox keeps commands until the player window is ready; InstanceBatch merges them.", "Settings › System integration: files opened from the file manager or the command line: Play (default) / Enqueue."),
("SI-07", "Notifications",
 "Shows a native notification with title, artist and art when the track changes (optional; off while the player is focused).",
 "OS notification area.",
 "<b>Windows:</b> toast notifications via CommunityToolkit notifications package (MIT); unpackaged apps need an AppUserModelID registered with a Start menu shortcut (done by the installer). "
 "<b>macOS:</b> UNUserNotificationCenter via native interop (needs a signed app bundle). <b>Linux:</b> org.freedesktop.Notifications via Tmds.DBus. "
 "<b>Components:</b> INotificationService.", "Settings › Interface: notify on track change (on/off)."),
("SI-08", "Command-line interface",
 "Controls the player from scripts and shortcuts. Built (P10 step 1): AudioTastic [files…], --open files…, --enqueue files… (on Windows also /open, /enqueue). To come: /play, /pause, /playpause, /stop, /next, /prev, /volume N, /seek s, /quit. If the player is running, the command is sent to it (SI-06); if not, it starts.",
 "Terminal / shortcuts. AudioTastic --help prints usage.",
 "<b>Components:</b> System.CommandLine (MIT) parser; commands forwarded through the single-instance pipe.", None),
])

S("Equalizer and DSP", "Sound-shaping features. The equalizer window (EQ) is one of the docked player windows.", [
("EQ-01", "10-band graphic equalizer",
 "Ten bands at 60, 170, 310, 600 Hz, 1, 3, 6, 12, 14 and 16 kHz (Winamp's bands), each ±12 dB, plus a preamp (±12 dB). The response curve is drawn above the sliders. ON switches the EQ in or out without losing settings.",
 "EQ window (Alt+G, EQ toggle in MW10, Main menu › Windows), docked below the main window the first time: EQ2 buttons (ON, AUTO, PARAM, PRESETS, DSP), EQ3 response curve with the preset name, EQ4 preamp and 10 band sliders. Sliders are heard while dragging and saved on release; the wheel moves 0.5 dB; double-click resets to 0 dB.",
 "<b>Components:</b> EqualizerStage in the DSP chain: peaking biquads (RBJ Audio EQ Cookbook, own code, Q 1.2), transposed direct form II with double-precision state; a new design is published to the audio thread as a whole on every change and on sample-rate changes. Automatic headroom: the overall gain is lowered by however far the curve (with the preamp) would rise above 0 dB. The measured response is within 0.1 dB of the calculated curve (tests at 44.1 and 96 kHz). All bands at 0 dB is bit-exact.", None),
("EQ-02", "EQ presets",
 "Built-in presets (Flat, Rock, Pop, Classical, Jazz, Dance, Full bass, Full treble, Vocal, Headphones, etc.), user presets (save, rename, delete), and import of Winamp .eqf preset files and Winamp EQ library files.",
 "EQ2 › PRESETS menu: Load (user presets, then built-in), Save as…, Delete, Import Winamp preset (.eqf, .q1)…, Reset to flat, Assign to this file / genre (EQ-03).",
 "<b>Components:</b> EqPresetStore (user presets in presets/equalizer.json; a user preset with a built-in name takes its place); EqfFile (own parser for Winamp's binary EQF/Q1 format: header, then per preset a 257-byte name and 11 values 0–63 for the ten bands and the preamp, 0 = +12 dB, 63 = −12 dB). Imported names that already exist get a number.", None),
("EQ-03", "Automatic EQ (per genre or file)",
 "When AUTO is on, a preset is loaded automatically when a track starts: first a preset assigned to that file, then one assigned to its genre tag, otherwise the current settings stay.",
 "EQ2: AUTO toggle. PRESETS › Assign to this file / Assign to genre… ; Settings › Equalizer & DSP › Auto EQ rules list.",
 "<b>Components:</b> AutoEqService listening to track-change events; genre rules in settings (genre → preset, case-insensitive), file rules in cache.db (kind 'autoeq', keyed like other cached data, so editing the file drops its rule). Loading a preset switches the equalizer on.", "Settings › Equalizer & DSP: auto EQ on/off and the genre rules (add, remove)."),
("EQ-04", "Parametric equalizer",
 "Advanced mode with up to 10 freely placed bands. Each band has type (peak, low shelf, high shelf, low pass, high pass, notch), frequency (20 Hz–20 kHz), gain (±24 dB) and Q (0.1–10). Bands can be dragged on the response curve.",
 "EQ2: PARAM switches the EQ window from sliders to the parametric editor: drag a point for frequency and gain, the wheel for Q, double-click empty space to add a band, right-click for type, on/off, delete and Bands… (a table with type, frequency, gain, Q and on/off per band). The graphic and parametric bands are kept separately.",
 "<b>Components:</b> the same EqualizerStage in parametric mode (same biquad code as EQ-01); ParametricEditor skin element (drawn with Avalonia).", None),
("EQ-05", "DSP chain",
 "An ordered list of effects applied to the audio after decoding and before output: equalizer, compressor, limiter, reverb, stereo widening, room correction, VST3 effects and plugin effects. Each effect can be switched on/off, reordered and configured.",
 "EQ2: DSP button opens the DSP chain window: the effects in processing order with on/off, Move up/down (Alt+Up/Down) and the selected effect's settings (heard at once). Also Settings › Equalizer & DSP.",
 "<b>Components:</b> DspChain in the MixerGraph running DspStage instances on the audio thread (no allocations or locks, tested; the stage list is an immutable array swapped atomically; parameters are published as immutable values). Built-in effects, all own code: compressor (stereo-linked, threshold/ratio/attack/release/make-up), look-ahead brick-wall limiter (5 ms, ceiling, release), reverb after the public-domain Freeverb design (room size, damping, mix, width), mid/side stereo widening. Every built-in effect together uses 2.5 % of one core at 96 kHz (Release). "
 "Plugin effects via IDspPlugin (PG-01) join the chain switched off; exceptions bypass them and count as faults, and each second over the time budget (10 % of real time) is a fault (PG-03). The chain is bypassed while DoP plays and reset after seeks and new tracks. "
 "<b>Order:</b> decks (crossfade) → ReplayGain → DSP chain → visualisation tap → volume → fades → output.", "Settings › Equalizer & DSP: chain order and effect settings (stored with the chain)."),
("EQ-06", "VST3 effect hosting",
 "Loads VST3 effect plugins into the DSP chain on all platforms, shows their own editor windows, and saves their state with the chain. Plugins are scanned from the standard VST3 folders.",
 "DSP chain window › Add › VST3 plugin… ; Settings › Equalizer & DSP › VST3: scan folders, Rescan.",
 "<b>Libraries:</b> Steinberg VST3 SDK (MIT since version 3.8). "
 "<b>Components:</b> native C++ host shim (built per platform with CMake) exposing a C API (scan, load, process float buffers, get/set state, open editor into a native window handle); .NET wrapper Vst3Stage. Editor hosted in an Avalonia NativeControlHost. "
 "<b>Scan folders:</b> C:\\Program Files\\Common Files\\VST3; /Library/Audio/Plug-Ins/VST3 and ~/Library/Audio/Plug-Ins/VST3; ~/.vst3, /usr/lib/vst3, /usr/local/lib/vst3. A plugin that crashes during scan is blacklisted (scan runs in a separate process).", "Settings › Equalizer & DSP: VST3 folders."),
("EQ-07", "Room correction (convolution)",
 "Applies a room-correction impulse response (WAV file, e.g. exported from REW) to the audio. The IR is resampled to the output rate. Separate IRs per sample rate can be set. Latency is compensated in the time display.",
 "DSP chain window › Room correction › choose IR file, on/off, gain.",
 "<b>Components:</b> ConvolutionStage (uniformly partitioned overlap-save FFT convolution, own code with FftSharp or NWaves); IR loaded with FFmpeg.", None),
])

S("Casting and network", "Sending audio to other devices and connecting to online services. All configured in Settings › Casting & network.", [
("CN-01", "Chromecast",
 "Plays the current track on a Chromecast or Cast-enabled speaker/TV. The device pulls the audio from the built-in streaming server (CN-04); formats the device cannot play (e.g. APE, WV, DSD, WMA) are transcoded to FLAC on the fly. Play, pause, seek, next/previous and volume control the cast device. Crossfading and DSP are applied before transcoding when 'Process audio before casting' is on (the stream is then always transcoded).",
 "MW10: CAST button opens the device picker (CN-05). While casting, MW5 shows the device name and the CAST button is filled.",
 "<b>Libraries:</b> SharpCaster (open source; licence to be verified) or own Cast v2 client (protobuf over TLS on port 8009, Default Media Receiver app). "
 "<b>Components:</b> ICastTarget implementation ChromecastTarget; uses CN-04 and CN-05. Supported without transcoding: MP3, AAC, FLAC, Opus, Vorbis, WAV.", "Settings › Casting & network: Chromecast on/off, process audio before casting."),
("CN-02", "DLNA / UPnP renderers",
 "Plays on DLNA/UPnP media renderers (Sonos, smart TVs, network receivers). Checks which formats the renderer supports and transcodes when needed. Supports play, pause, stop, seek and volume.",
 "Same device picker as CN-01.",
 "<b>Components (own code):</b> SSDP discovery (M-SEARCH for urn:schemas-upnp-org:device:MediaRenderer:1); device description XML parsing; SOAP calls to AVTransport (SetAVTransportURI with DIDL-Lite metadata, Play, Pause, Stop, Seek, GetPositionInfo), RenderingControl (SetVolume) and ConnectionManager (GetProtocolInfo). Uses CN-04.", "Settings › Casting & network: DLNA on/off."),
("CN-03", "AirPlay (original AirPlay / RAOP)",
 "Streams to AirPlay speakers and Apple TVs using the original AirPlay audio protocol. AirPlay 2 (multi-room) is out of scope for now. No test device is available, so this feature is tested against a software receiver (e.g. shairport-sync on Linux) and marked as untested on real hardware.",
 "Same device picker as CN-01.",
 "<b>Components (own code):</b> mDNS discovery of _raop._tcp; RTSP session (ANNOUNCE, SETUP, RECORD, FLUSH, TEARDOWN, SET_PARAMETER for volume and metadata); RTP audio packets with ALAC-encoded 44.1 kHz / 16-bit audio (FFmpeg alac encoder) and AES encryption where the receiver requires it; timing/sync packets. Devices that only accept AirPlay 2 pairing will not work.", "Settings › Casting & network: AirPlay on/off."),
("CN-04", "Local streaming server",
 "Built-in HTTP server that serves the current track (and the next one, for gapless on devices that support queueing) to cast devices. Serves the original file when the device supports the format, otherwise transcodes on the fly (FLAC by default, MP3 320 kbps as fallback). Supports HTTP range requests for seeking. Only reachable on the local network, with random per-session URLs.",
 "Settings only.",
 "<b>Libraries:</b> Kestrel / ASP.NET Core minimal APIs (MIT), embedded; FFmpeg encoders for transcoding (flac, libmp3lame (LGPL)). "
 "<b>Components:</b> StreamServer, Transcoder. Binds to the LAN interface; Windows firewall prompt on first use.", "Settings › Casting & network: server port (default automatic), transcode format."),
("CN-05", "Device discovery and picker",
 "Finds cast devices on the local network continuously while the picker is open (and in the background when casting is enabled), and shows them grouped by type with their names. Choosing a device moves playback to it at the current position; choosing 'This computer' moves it back.",
 "MW10: CAST button → device picker popover (list of devices with type icons, 'This computer' at the top, Refresh).",
 "<b>Libraries:</b> Makaretu.Dns.Multicast (MIT) for mDNS (_googlecast._tcp, _raop._tcp); own SSDP for DLNA. "
 "<b>Components:</b> CastService aggregating ICastTarget providers (cast-target plugins can add more, PG-01).", None),
("CN-06", "Remote control (web/mobile)",
 "A small web remote served by the player: now playing with art, transport buttons, volume, seek, and the current playlist with tap-to-play. Any phone browser on the same network can use it after pairing.",
 "Settings › Casting & network › Remote control: on/off, port, pairing QR code and PIN.",
 "<b>Components:</b> REST API + WebSocket (state push) on Kestrel; static HTML/JS remote page embedded as resources; pairing token required on every request. Off by default. "
 "<b>Also:</b> the same API documented for third-party remotes.", "Settings › Casting & network: remote control on/off, port, pairing."),
])

S("Plugin architecture", "How third parties (and AudioTastic itself) extend the player.", [
("PG-01", "Plugin types",
 "Seven plugin types: Input (decoders for new formats), Output (new output devices), DSP (effects in the DSP chain), Visualisation (spectrum/scope/full-screen visuals), General (background features using the player API, e.g. scrobblers), UI panel (adds a dockable window or a settings page), Cast target (new kinds of cast devices). One plugin assembly can contain several types.",
 "Plugins appear where their type belongs: input formats in Open dialogs, outputs in Settings › Output, DSP in the DSP chain, visualisations in main menu › Visualisation, UI panels as dockable windows, cast targets in the device picker.",
 "<b>Components:</b> interfaces IInputPlugin, IOutputPlugin, IDspPlugin, IVisualisationPlugin, IGeneralPlugin, IUiPanelPlugin, ICastTargetPlugin in AudioTastic.Sdk; IPlayerApi gives plugins controlled access to playback, playlists, tags and settings. "
 "<b>Built-ins:</b> FFmpeg decoder, libopenmpt, MIDI, built-in visualisations and DSP effects, and the cast targets are implemented as plugins against the same SDK.", None),
("PG-02", "Plugin SDK",
 "A separate, versioned contracts package that plugin authors reference. Contains only interfaces, data types and helpers, with no dependency on the player's internals. Comes with documentation and a sample plugin of each type.",
 "Not in the app UI.",
 "<b>Deliverables:</b> AudioTastic.Sdk project published as a NuGet package under the MIT licence, like the samples and template (Anders, 17 Nov 2026; the app's own licence is separate) (semantic versioning; major version changes break plugins; 0.x pre-release until P8, where a plugin needs the same minor version); 'dotnet new audiotastic-plugin' template; samples in /samples; plugin author guide in docs/plugins; plugin.json manifest format (id, name, version, author, description, website, licence, sdkVersion, entry assembly, plugin types, decoder priority).", None),
("PG-03", "Plugin loading and isolation",
 "Finds plugins in the plugins folder, checks their manifest and SDK version, and loads each one in its own load context so plugin dependencies cannot clash. Exceptions from a plugin are caught and logged; a plugin that keeps failing (3 errors within a minute) or exceeds its audio-thread time budget is disabled with a notification. Plugins can be unloaded and updated without restarting where possible.",
 "Notification when a plugin is disabled; details in the plugin manager (PG-04).",
 "<b>Components:</b> PluginHost in AudioTastic.Plugins.Host using a collectible AssemblyLoadContext per plugin (assemblies loaded from memory, so files are not locked), sharing only AudioTastic.Sdk, the logging abstractions, Avalonia/Skia (for plugin UI) and framework assemblies; native libraries from the plugin's runtimes/&lt;rid&gt;/native. Each plugin gets its own guarded view of IPlayerApi: exceptions in its event handlers count as its faults, and its handlers are dropped on unload. "
 "<b>Limit:</b> in-process loading cannot protect against native crashes or stack overflows inside a plugin; this is documented for plugin authors.", None),
("PG-04", "Plugin manager",
 "Lists installed plugins with name, version, author, types and status (enabled, disabled, failed, incompatible). Install from a .atplugin (zip) file, enable/disable, uninstall, open settings, view error details.",
 "Settings › Plugins.",
 "<b>Components:</b> PluginManagerPage (part of SettingsWindow); install = unzip to plugins folder after manifest validation.", None),
("PG-05", "Plugin settings",
 "Each plugin gets its own settings storage and can provide a settings page shown inside the settings window.",
 "Settings › Plugins › select plugin › Settings (or a page in the plugin's natural group, e.g. a DSP plugin under Equalizer & DSP).",
 "<b>Components:</b> IPluginSettings (typed JSON values per plugin, stored in settings.json under Plugins › Data[plugin id], removed on uninstall); IPluginSettingsPage.CreateSettingsPage() returning an Avalonia Control.", None),
("PG-06", "Native Winamp plugin bridge",
 "Runs classic native Winamp plugins: input plugins (in_*.dll) and DSP plugins (dsp_*.dll) first; general-purpose (gen_*) and visualisation (vis_*) plugins on a best-effort basis, since they rely on Winamp's main window messages.",
 "Settings › Plugins › Winamp plugins: folder to load from, list of found plugins with on/off and Configure (opens the plugin's own dialog).",
 "<b>Windows only.</b> "
 "<b>Components:</b> AudioTastic.NativeBridge, a separate 32-bit (win-x86) helper process that loads the DLLs (winampGetInModule2, winampDSPGetHeader2, winampGetGeneralPurposePlugin, winampVisGetHeader), emulates the parts of Winamp's API they call, and exchanges audio with the player through shared memory and commands through a named pipe. A crashing plugin only takes down the helper process, which is restarted.", None),
])

S("Tools and quality of life", "Supporting features.", [
("TL-01", "Format conversion",
 "Converts selected files to MP3 (LAME, VBR/CBR), FLAC, Opus, AAC, ALAC, Ogg Vorbis or WAV, keeping tags and cover art. Output folder and file name pattern (e.g. %artist%/%album%/%track% - %title%) are configurable. Runs several files in parallel with a progress list; can be cancelled.",
 "Playlist context menu › Convert… opens the Convert dialog (format, quality, destination, pattern, Start). Also used by CD ripping (FM-11).",
 "<b>Libraries:</b> FFmpeg encoders (flac, alac, aac native, libmp3lame (LGPL), libopus (BSD), libvorbis (BSD)); TagLibSharp to copy tags. "
 "<b>Components:</b> ConversionService with a job queue.", None),
("TL-02", "Settings import/export",
 "Exports settings, playlists, EQ presets, DSP chain, hotkeys and installed skins to one .atbackup file (zip); importing restores them (with a choice of what to import). Caches are not included.",
 "Settings › General › Export settings… / Import settings…",
 "<b>Components:</b> BackupService (System.IO.Compression); settings schema version checked and migrated on import.", None),
("TL-03", "Logging and crash reports",
 "Writes structured logs (rolling files, 7 days kept). Catches unhandled exceptions, writes a crash report (exception, log tail, versions, OS, enabled plugins) and on next start offers to open the report or its folder. Nothing is uploaded automatically.",
 "Settings › General: log level, Open log folder. Crash dialog on next start.",
 "<b>Libraries:</b> Serilog (Apache 2.0) with file sink. "
 "<b>Components:</b> handlers for AppDomain.UnhandledException, TaskScheduler.UnobservedTaskException and Avalonia's dispatcher exceptions.", "Settings › General: log level."),
("TL-04", "Accessibility",
 "All functions can be reached by keyboard (focus order, visible focus outline in the skin, keyboard shortcuts listed in menus). Non-skinned windows and the main controls expose names and states to screen readers.",
 "All windows.",
 "<b>Components:</b> Avalonia AutomationProperties on every control (Name, HelpText); UI Automation on Windows and NSAccessibility on macOS are supported by Avalonia; Linux (AT-SPI) support depends on the Avalonia version and is best-effort.", None),
("TL-05", "Portable mode",
 "Runs from a folder or USB stick with all settings, playlists, caches and plugins stored next to the program instead of in the user profile. Enabled when a file named 'portable' exists next to the executable (or with --portable). Shell integration (SI-05) is disabled in portable mode.",
 "Settings › General: shows 'Portable mode' status; 'Make portable copy…' copies the app and current settings to a chosen folder.",
 "<b>Components:</b> AppPaths service resolving all data paths at start-up (used by every component that stores files).", None),
])

OPEN_POINTS = []
_RESOLVED = [
("OptimFROG (FM-06)", "No open-source decoder exists; the only decoder is the closed-source freeware OptimFROG SDK, which breaks the free-and-open-source library rule. Options: drop OptimFROG, or allow the closed-source SDK as an optional plugin that users install themselves."),
]
