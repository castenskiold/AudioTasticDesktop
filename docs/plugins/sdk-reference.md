# AudioTastic SDK 1.0.0 reference

Generated from the SDK's XML documentation by `scripts/make_sdk_reference.py`; do not edit by hand. How to write a plugin, the manifest, packaging and versioning: [plugin-guide.md](plugin-guide.md).

Namespaces: [AudioTastic.Sdk.Audio](#audiotasticsdkaudio), [AudioTastic.Sdk.Interop](#audiotasticsdkinterop), [AudioTastic.Sdk.Metadata](#audiotasticsdkmetadata), [AudioTastic.Sdk.Playlists](#audiotasticsdkplaylists), [AudioTastic.Sdk.Plugins](#audiotasticsdkplugins), [AudioTastic.Sdk.Video](#audiotasticsdkvideo)

## AudioTastic.Sdk.Audio

### AudioDeviceInfo

A playback device as reported by the output backend.

- `Name`: Display name; also what Settings › Output stores.
- `IsDefault`: True for the system default device.
- `Id`: Backend-specific identifier (opaque).

| Member | Kind | Description |
|---|---|---|
| `AudioDeviceInfo` | constructor | A playback device as reported by the output backend. `Name`: Display name; also what Settings › Output stores.; `IsDefault`: True for the system default device.; `Id`: Backend-specific identifier (opaque). |
| `Name` | property | Display name; also what Settings › Output stores. |
| `IsDefault` | property | True for the system default device. |
| `Id` | property | Backend-specific identifier (opaque). |

### AudioFormat

PG-02: format of a block of decoded audio. Samples are always interleaved 32-bit float in the range [-1, 1].

| Member | Kind | Description |
|---|---|---|
| `FramesToTime` | method | Converts a frame count (one sample per channel) to a duration. |
| `TimeToFrames` | method | Converts a duration to the nearest frame count. |

### AudioOutputLostEventArgs

Why the output stopped on its own.

| Member | Kind | Description |
|---|---|---|
| `AudioOutputLostEventArgs` | constructor | Why the output stopped on its own. |

### AudioSourceException

PG-02: thrown by input plugins when a file cannot be opened or decoded.

### AudioStreamInfo

PG-02: describes an audio stream as stored in the source (what the user sees in MW5 and the file info dialog). Decoded audio is always interleaved 32-bit float at `SampleRate` and `Channels`.

- `Codec`: Short codec name, e.g. "flac", "mp3", "pcm_s24le".
- `SampleRate`: Samples per second per channel.
- `Channels`: Number of channels.
- `BitsPerSample`: Bit depth of the source samples; 0 for lossy codecs where it has no meaning.
- `Bitrate`: Average bitrate in bits per second; 0 when unknown.
- `IsLossless`: True for lossless and PCM codecs.

| Member | Kind | Description |
|---|---|---|
| `AudioStreamInfo` | constructor | PG-02: describes an audio stream as stored in the source (what the user sees in MW5 and the file info dialog). Decoded audio is always interleaved 32-bit float at `SampleRate` and `Channels`. `Codec`: Short codec name, e.g. "flac", "mp3", "pcm_s24le".; `SampleRate`: Samples per second per channel.; `Channels`: Number of channels.; `BitsPerSample`: Bit depth of the source samples; 0 for lossy codecs where it has no meaning.; `Bitrate`: Average bitrate in bits per second; 0 when unknown.; `IsLossless`: True for lossless and PCM codecs. |
| `Codec` | property | Short codec name, e.g. "flac", "mp3", "pcm_s24le". |
| `SampleRate` | property | Samples per second per channel. |
| `Channels` | property | Number of channels. |
| `BitsPerSample` | property | Bit depth of the source samples; 0 for lossy codecs where it has no meaning. |
| `Bitrate` | property | Average bitrate in bits per second; 0 when unknown. |
| `IsLossless` | property | True for lossless and PCM codecs. |
| `OutputFormat` | property | Format of the decoded output of this stream. |

### DeviceFormat

PB-05: a format the device supports natively. 0 channels / 0 Hz mean "any".

| Member | Kind | Description |
|---|---|---|
| `DeviceFormat` | constructor | PB-05: a format the device supports natively. 0 channels / 0 Hz mean "any". |

### EncodeFormat

PG-02 (SDK 0.4.1; 0.4.3 added Opus, AAC, ALAC, Vorbis and WAV): formats an encoder plugin can write.

| Member | Kind | Description |
|---|---|---|
| `Flac` | field | FLAC (lossless), in a .flac stream. |
| `Mp3` | field | MP3, in a .mp3 stream. |
| `Opus` | field | SDK 0.4.3: Opus in Ogg (.opus). |
| `Aac` | field | SDK 0.4.3: AAC in MP4 (.m4a). The output stream must be seekable. |
| `Alac` | field | SDK 0.4.3: Apple Lossless in MP4 (.m4a). The output stream must be seekable. |
| `Vorbis` | field | SDK 0.4.3: Vorbis in Ogg (.ogg). |
| `Wav` | field | SDK 0.4.3: PCM in a WAV file (16 or 24 bit). Sizes in the header need a seekable stream. |

### EncodeSettings

What to encode to.

- `Format`: The output format.
- `BitsPerSample`: Lossless formats: 16 or 24.
- `BitrateKbps`: Lossy formats: the bit rate, e.g. 320 (an average for Vorbis).

| Member | Kind | Description |
|---|---|---|
| `EncodeSettings` | constructor | What to encode to. `Format`: The output format.; `BitsPerSample`: Lossless formats: 16 or 24.; `BitrateKbps`: Lossy formats: the bit rate, e.g. 320 (an average for Vorbis). |
| `Format` | property | The output format. |
| `BitsPerSample` | property | Lossless formats: 16 or 24. |
| `BitrateKbps` | property | Lossy formats: the bit rate, e.g. 320 (an average for Vorbis). |
| `Vbr` | property | SDK 0.4.3: MP3 with a variable bit rate (`VbrQuality`) instead of `BitrateKbps`. |
| `VbrQuality` | property | SDK 0.4.3: VBR quality like LAME's -V: 0 (best, about 245 kbps) to 9 (smallest). Default 2 (about 190 kbps). |

### IAudioEncoder

PG-02 (SDK 0.4.1): writes encoded audio to a stream. Used for cast transcoding (CN-04) and conversion (TL-01). Not thread-safe; the caller writes from one thread. The output stream may be non-seekable (a network response); when it is seekable the encoder may go back to finish headers (MP4, WAV, the MP3 gapless header).

| Member | Kind | Description |
|---|---|---|
| `Write` | method | Encodes interleaved float samples (any count of whole frames) in the format given at creation. |
| `Finish` | method | Encodes what is left and writes the end of the stream; call once, after the last `Single})`. |

### IAudioEncoderFactory

PG-02 (SDK 0.4.1): creates encoders. Input plugins offer them through `IInputPlugin.EncoderFactories`. `Stream)` throws `AudioSourceException` when the format cannot be written here.

| Member | Kind | Description |
|---|---|---|
| `Name` | property | Display name, e.g. "FFmpeg". |
| `Formats` | property | The formats this machine can write (an FFmpeg build without an MP3 encoder leaves out MP3). |
| `Create` | method | A new encoder writing `settings`' format for input in `format` to `output`. |
| `InputFormatFor` | method | SDK 0.4.3: the input format an encoder for `settings` accepts, closest to `input` (Opus needs 48 kHz, MP3 at most 48 kHz and two channels). The caller converts to it before `Stream)`. Default: `input` unchanged. |

### IAudioOutput

PB-04 / PG-01: an audio output (miniaudio in the app; output plugins implement it too). Shared or exclusive mode (ASIO is optional and deferred).

| Member | Kind | Description |
|---|---|---|
| `BackendName` | property | Backend name, e.g. "WASAPI", "Core Audio", "PulseAudio". |
| `GetDevices` | method | Current playback devices. |
| `DevicesChanged` | event | Raised (on a background thread) when devices are added or removed or the default changes. |
| `OutputLost` | event | Raised (on a background thread) when the open device stops without being asked to, e.g. it was unplugged. The engine reacts by reopening on the default device. |
| `DeviceName` | property | Name of the open device. |
| `Format` | property | Format the device was opened with (what the renderer must produce). |
| `IsExclusive` | property | Exclusive mode is in use. |
| `SampleFormat` | property | Sample format sent to the device. |
| `DeviceSampleRate` | property | PB-05: the rate the hardware runs at (in shared mode the OS may resample); 0 when closed. |
| `GetDeviceFormats` | method | PB-05: native formats of `device` (null = default device); empty when unknown. |
| `Open` | method | Opens `device` (null = system default, following default-device changes) for `format`. Throws `InvalidOperationException` when the device cannot be opened so (e.g. an exclusive format it does not support). |
| `SetDither` | method | PB-05: switches TPDF dither for integer output on or off while open (per track). |
| `StartOutput` | method | Starts pulling audio from the renderer. |
| `StopOutput` | method | Stops pulling audio; the device stays open. |

### IAudioRenderer

Produces audio on the audio thread. Implementations must not allocate, lock or block.

| Member | Kind | Description |
|---|---|---|
| `Render` | method | Fills `output` with interleaved float samples. |

### IAudioSource

PG-02 / FM-01: a decoded audio stream (input plugin output). Not thread-safe: the engine calls it from one decoder thread at a time, never from the audio thread.

| Member | Kind | Description |
|---|---|---|
| `Info` | property | Source stream description (codec, rate, bit depth, ...). |
| `Format` | property | Format of the samples returned by `Single})`. |
| `LengthFrames` | property | Length in frames, or null when unknown (e.g. live streams). |
| `PositionFrames` | property | Frame index of the next frame `Single})` returns. |
| `CanSeek` | property | True when `Int64)` is supported. |
| `Tags` | property | Raw tags from the container/stream (key → value); keys as stored, compared case-insensitively. |
| `Read` | method | Fills `buffer` with interleaved float samples. `buffer` length must be a multiple of `Channels`. Returns the number of frames written; 0 means end of stream. May return fewer frames than requested before the end. |
| `Seek` | method | Moves to `frame` sample-accurately; the next `Single})` starts exactly there. |

### IAudioSourceFactory

PG-02: an input/decoder plugin. The engine asks each registered factory, in priority order, whether it can open a file and uses the first that can.

| Member | Kind | Description |
|---|---|---|
| `Name` | property | Display name, e.g. "FFmpeg". |
| `Extensions` | property | File extensions this factory handles, lower case with leading dot (".flac"). |
| `CanOpen` | method | Quick check based on the path (extension); must not open the file. |
| `Open` | method | Opens the file. Throws `AudioSourceException` when the file cannot be decoded. |

### IDopSourceFactory

FM-05: implemented by decoder factories that can deliver DSD as DoP (DSD over PCM) for DACs that support it. The player uses it only in exclusive mode with Settings › Output › DSD mode = DoP; otherwise DSD is decoded to PCM.

| Member | Kind | Description |
|---|---|---|
| `IsDsd` | method | True for DSD files (.dsf, .dff) this factory can deliver as DoP. |
| `OpenDop` | method | Opens a DSD file as DoP: 24-bit words (marker byte + 16 DSD bits) as floats (value / 2^23). |

### IStreamMetadata

FM-10: implemented by sources of live streams (internet radio) that carry a now-playing title (ICY metadata). `StreamTitleChanged` is raised on the decoder thread.

| Member | Kind | Description |
|---|---|---|
| `StreamTitle` | property | The current title, e.g. "Artist - Title"; null when the stream sends none. |
| `StationName` | property | The station name (icy-name), when known. |

### IStreamMetadataSource

Implemented by wrappers around a source (guards, buffers) to expose the wrapped source's stream metadata.

### OutputOptions

PB-04 / PB-05: how to open an output.

- `BufferMilliseconds`: Total buffer, about two device periods.
- `Exclusive`: Exclusive (bit-perfect) mode: no OS mixer, the device gets the samples unchanged.
- `SampleFormat`: Sample format for the device (F32 in shared mode).
- `Dither`: TPDF dither when converting to an integer format (used when reducing bit depth).

| Member | Kind | Description |
|---|---|---|
| `OutputOptions` | constructor | PB-04 / PB-05: how to open an output. `BufferMilliseconds`: Total buffer, about two device periods.; `Exclusive`: Exclusive (bit-perfect) mode: no OS mixer, the device gets the samples unchanged.; `SampleFormat`: Sample format for the device (F32 in shared mode).; `Dither`: TPDF dither when converting to an integer format (used when reducing bit depth). |
| `BufferMilliseconds` | property | Total buffer, about two device periods. |
| `Exclusive` | property | Exclusive (bit-perfect) mode: no OS mixer, the device gets the samples unchanged. |
| `SampleFormat` | property | Sample format for the device (F32 in shared mode). |
| `Dither` | property | TPDF dither when converting to an integer format (used when reducing bit depth). |

### OutputSampleFormat

PB-05: sample format sent to the device. Integer formats are converted by the output, exactly inverse to decoding.

| Member | Kind | Description |
|---|---|---|
| `S24` | field | 24-bit packed (3 bytes per sample). |

## AudioTastic.Sdk.Interop

### NativeLibraries

Finds native libraries (FFmpeg, miniaudio, ...) in the two places the build puts them: next to the application (publish for one runtime identifier) or in `runtimes/<rid>/native` (plain build, which copies every RID). Plugins with native code use the same helper.

| Member | Kind | Description |
|---|---|---|
| `SearchDirectories` | property | Directories searched, in order. Only existing directories are returned. |
| `FindDirectoryContaining` | method | The first search directory that contains `fileName`, or null. `fileName` is a real file name such as "avcodec-62.dll" or "libavcodec.so.62". |
| `RegisterResolver` | method | Installs a `DllImportAttribute` resolver for `assembly` that probes `SearchDirectories` using the platform's naming (name.dll, libname.so, libname.dylib). Safe to call more than once. |
| `PlatformFileNames` | method | Candidate file names for a library base name on the current OS. |
| `PortableRid` | property | Portable RID such as win-x64 even when the runtime reports a distro-specific one. |

## AudioTastic.Sdk.Metadata

### ReplayGainInfo

PB-03: ReplayGain values from tags (REPLAYGAIN_* in ID3/Vorbis/APE/MP4; Opus R128_* converted to the ReplayGain reference of -18 LUFS). Gains in dB, peaks as linear sample values (1.0 = full scale).

| Member | Kind | Description |
|---|---|---|
| `ReplayGainInfo` | constructor | PB-03: ReplayGain values from tags (REPLAYGAIN_* in ID3/Vorbis/APE/MP4; Opus R128_* converted to the ReplayGain reference of -18 LUFS). Gains in dB, peaks as linear sample values (1.0 = full scale). |

### TrackTags

MD-01: the tags the player displays and sorts by. All fields are optional. Read by TagService (TagLibSharp, FFmpeg fallback) or taken from playlist files and CUE sheets.

| Member | Kind | Description |
|---|---|---|
| `TrackTags` | constructor | MD-01: the tags the player displays and sorts by. All fields are optional. Read by TagService (TagLibSharp, FFmpeg fallback) or taken from playlist files and CUE sheets. |
| `DisplayName` | property | "Artist – Title", or the title alone; null when there is no title. |
| `Merge` | method | Fields of `other` fill the gaps of this instance. |

## AudioTastic.Sdk.Playlists

### IPlaylistFormat

PL-03: a playlist file format (M3U, PLS, XSPF, ...). Plugins can add formats (PG-01).

| Member | Kind | Description |
|---|---|---|
| `Name` | property | Display name, e.g. "M3U8 playlist". |
| `Extensions` | property | Extensions, lower case with leading dot. |
| `Read` | method | Reads `file`; relative paths are resolved against its folder. |
| `Write` | method | Writes `items`; with `relativePaths` paths under the file's folder are written relative. |

### PlaylistItem

PG-02 / PL-03: one entry as read from or written to a playlist file. Paths are absolute (or URLs) after reading; formats resolve relative paths against the playlist file's folder.

- `Path`: Absolute file path or URL.
- `Title`: Display title stored in the playlist (#EXTINF, PLS TitleN, XSPF title), if any.
- `Artist`: Artist stored in the playlist (XSPF creator, CUE PERFORMER), if any.
- `Duration`: Length stored in the playlist, if any.
- `Start`: FM-09: start of a virtual track inside `Path` (CUE INDEX 01).
- `End`: FM-09: end of a virtual track (next INDEX 01), or null for "to the end of the file".
- `TrackNumber`: CUE TRACK number, if any.
- `Album`: CUE album TITLE, if any.

| Member | Kind | Description |
|---|---|---|
| `PlaylistItem` | constructor | PG-02 / PL-03: one entry as read from or written to a playlist file. Paths are absolute (or URLs) after reading; formats resolve relative paths against the playlist file's folder. `Path`: Absolute file path or URL.; `Title`: Display title stored in the playlist (#EXTINF, PLS TitleN, XSPF title), if any.; `Artist`: Artist stored in the playlist (XSPF creator, CUE PERFORMER), if any.; `Duration`: Length stored in the playlist, if any.; `Start`: FM-09: start of a virtual track inside `Path` (CUE INDEX 01).; `End`: FM-09: end of a virtual track (next INDEX 01), or null for "to the end of the file".; `TrackNumber`: CUE TRACK number, if any.; `Album`: CUE album TITLE, if any. |
| `Path` | property | Absolute file path or URL. |
| `Title` | property | Display title stored in the playlist (#EXTINF, PLS TitleN, XSPF title), if any. |
| `Artist` | property | Artist stored in the playlist (XSPF creator, CUE PERFORMER), if any. |
| `Duration` | property | Length stored in the playlist, if any. |
| `Start` | property | FM-09: start of a virtual track inside `Path` (CUE INDEX 01). |
| `End` | property | FM-09: end of a virtual track (next INDEX 01), or null for "to the end of the file". |
| `TrackNumber` | property | CUE TRACK number, if any. |
| `Album` | property | CUE album TITLE, if any. |
| `IsVirtual` | property | True for a CUE virtual track (part of a file). |

## AudioTastic.Sdk.Plugins

### ICastTargetPlugin

PG-01 Cast target: new kinds of cast devices (CN-xx). The contract is completed in P11 (SDK 0.x).

### IDspEffect

An effect instance. `Int32)` runs on the audio thread: it must not allocate, lock, block or throw, and must stay within its time budget (PG-03), or the plugin is disabled.

| Member | Kind | Description |
|---|---|---|
| `Process` | method | Processes interleaved samples in place. |
| `Reset` | method | Clears internal state (after a seek or track change). |

### IDspPlugin

PG-01 DSP: one effect in the DSP chain (EQ-05), added switched off; the user orders and enables it. `AudioFormat)` is called for each output format. Time budget: at most 10 % of real time per second of audio, or the plugin is disabled (PG-03).

### IGeneralPlugin

PG-01 General: background features using `IPlayerApi` (e.g. scrobblers, loggers).

### IInputPlugin

PG-01 Input: decoders for new formats (FM-xx), and optionally playlist formats (PL-03).

| Member | Kind | Description |
|---|---|---|
| `EncoderFactories` | property | SDK 0.4.1: encoders this plugin offers (cast transcoding, CN-04; conversion, TL-01). Default: none. |
| `VideoEncoderFactories` | property | SDK 0.4.2: live video encoders this plugin offers (casting visualisations, CN-09). Default: none. |

### IOffscreenVisualisation

SDK 0.4.2 (CN-09): a visualisation that draws frames without a window, so it can be cast to a TV. The player creates a separate instance for casting (with `Create`, on the UI thread) and then calls `AudioFormat)` and `TimeSpan)` alternately on one background thread, never at the same time; `CreateView` is not called for that instance, so create your Avalonia control lazily there.

| Member | Kind | Description |
|---|---|---|
| `RenderFrame` | method | Draws one frame. `pixels` is `width` × `height` pixels of 32-bit premultiplied BGRA (4 bytes per pixel, rows top to bottom, no padding) holding the previous frame; `time` is the frame's time since casting started (for animation). Typical sizes: 1920 × 1080 at 30 frames a second; keep a frame well under 30 ms. |

### IOutputPlugin

PG-01 Output: new output devices, listed in Settings › Output as "Name: device" (or just "Name" when `GetDevices` returns none). The host calls `CreateOutput` once and disposes the output before the plugin. `IAudioRenderer)` throws `InvalidOperationException` to refuse a format or exclusive mode (the player then falls back); any other exception counts as a plugin fault (PG-03). The output calls the renderer on its own audio thread and must catch its own exceptions there.

### IPlayerApi

PG-01: controlled access to the player for plugins. Events are raised on background threads; plugins with UI must marshal to the UI thread themselves. Commands are queued and safe to call from any thread.

| Member | Kind | Description |
|---|---|---|
| `Volume` | property | 0–100. |
| `TrackChanged` | event | A new track started (or playback ended: null), or a stream's title changed. |
| `GetActivePlaylist` | method | The entries of the playlist tab shown in the playlist window. |
| `AddToPlaylistAsync` | method | Adds files, folders, playlists or URLs to the active tab; with `play` the first one starts. |
| `ReadTags` | method | Reads a file's tags (cached). Slow for uncached files: call from a background thread. |

### IPlugin

PG-01: base of every plugin. The host creates the instance, calls `IPluginContext)` once, and `Dispose` when the plugin is disabled, uninstalled or the player exits. Exceptions thrown by a plugin are caught and counted; 3 within a minute disable the plugin (PG-03).

### IPluginContext

What the host gives a plugin.

| Member | Kind | Description |
|---|---|---|
| `PluginId` | property | The id from plugin.json. |
| `PluginFolder` | property | The folder the plugin was loaded from (read-only: may be in the program folder). |
| `DataFolder` | property | A writable folder for the plugin's own files (created on demand). |
| `Settings` | property | PG-05: the plugin's own settings, stored by the player. |
| `Player` | property | Controlled access to playback, playlists and tags. |
| `Logger` | property | Logger writing to the player's log files, tagged with the plugin id. |

### IPluginSettings

PG-05: per-plugin settings, saved with the player's settings (JSON). Values must be JSON-serialisable. Thread-safe.

| Member | Kind | Description |
|---|---|---|
| `GetValue``1` | method | The stored value, or `defaultValue` when missing or not convertible. |
| `Changed` | event | Raised with the key after `String,``0)` or `String)`. |

### IPluginSettingsPage

PG-05: a plugin that shows a settings page in Settings › Plugins.

| Member | Kind | Description |
|---|---|---|
| `CreateSettingsPage` | method | Returns an Avalonia `Control` (typed as object so the SDK does not depend on Avalonia). |

### IUiPanelPlugin

PG-01 UI panel: a dockable window opened from Main menu › Windows.

| Member | Kind | Description |
|---|---|---|
| `CreatePanel` | method | Returns an Avalonia `Control` for the panel. |

### IVisualisationPlugin

PG-01 Visualisation: spectrum/scope/full-screen visuals (UI-07) in their own window (Main menu › Visualisation).

| Member | Kind | Description |
|---|---|---|
| `CanRenderOffscreen` | property | SDK 0.4.2 (CN-09): true when the visualisations from `Create` also implement `IOffscreenVisualisation`, so the player can cast them to a screen. Default: false. |

### NowPlaying

The current track.

- `Path`: File path or stream URL.
- `Tags`: Title, artist, album, ... (for streams the title follows ICY metadata).
- `Stream`: Source format.
- `Duration`: Length, or null for streams.

| Member | Kind | Description |
|---|---|---|
| `NowPlaying` | constructor | The current track. `Path`: File path or stream URL.; `Tags`: Title, artist, album, ... (for streams the title follows ICY metadata).; `Stream`: Source format.; `Duration`: Length, or null for streams. |
| `Path` | property | File path or stream URL. |
| `Tags` | property | Title, artist, album, ... (for streams the title follows ICY metadata). |
| `Stream` | property | Source format. |
| `Duration` | property | Length, or null for streams. |

### PlayerState

Playback state as plugins see it.

### PluginManifest

PG-02: plugin.json, at the root of a plugin folder or .atplugin package.

| Member | Kind | Description |
|---|---|---|
| `Id` | property | Unique, stable id in reverse-domain style (letters, digits, '.', '-', '_'). |
| `SdkVersion` | property | The AudioTastic.Sdk version the plugin was built against. |
| `EntryAssembly` | property | The assembly (relative to the plugin folder) containing the plugin classes. |
| `Priority` | property | Decoder order when several input plugins can open a file: higher first (default 0). The built-in FFmpeg decoder uses -100, so a specialised decoder for the same extension wins. |
| `Validate` | method | Problems that make the manifest unusable; empty when valid. |
| `IdPattern` | method |  |

### PluginType

PG-01: the seven plugin types. A plugin class implements one or more of the interfaces below (one assembly may contain several plugin classes). Every plugin class needs a public parameterless constructor.

### SdkInfo

The SDK's own version and the compatibility rule for plugins (PG-02, PG-03).

| Member | Kind | Description |
|---|---|---|
| `Version` | field | Version of these contracts. While the SDK is 0.x (until P8) a plugin needs the same 0.minor; from 1.0 the same major and a minor not newer than the player's. |
| `IsCompatible` | method | Whether a plugin built for `pluginSdkVersion` runs on this player. 0.x: the same minor version. From 1.0: a plugin built for 1.y runs on every player with SDK 1.z where z ≥ y. SDK 1.0.0 has exactly the contracts of 0.4.3, so plugins built for 0.4.x also run on 1.x. |

## AudioTastic.Sdk.Video

### IVideoEncoder

PG-02 (SDK 0.4.2): writes a live H.264 video stream (with AAC audio when asked) to a stream that may not be seekable. Each frame carries its time from the start of the stream (frames may be skipped when the computer falls behind; times must increase); audio is timed by its sample count. The caller paces both in real time. All calls come from one thread.

| Member | Kind | Description |
|---|---|---|
| `EncoderName` | property | The H.264 encoder in use (e.g. "h264_mf"), for logs. |
| `WriteFrame` | method | The frame for `time` (from the start of the stream, rounded to the frame rate): Width × Height × 4 bytes of premultiplied BGRA, rows top to bottom. A frame at or before the previous one's time is dropped. |
| `WriteAudio` | method | Interleaved float audio in `Audio`, in any amount. |
| `Finish` | method | Flushes the encoders and ends the stream. |

### IVideoEncoderFactory

PG-02 (SDK 0.4.2): creates video encoders. Input plugins offer them through `IInputPlugin.VideoEncoderFactories`.

| Member | Kind | Description |
|---|---|---|
| `Containers` | property | The containers this machine can write (empty when it has no H.264 encoder). |
| `Create` | method | Creates an encoder writing to `output`; "cannot encode here" is an `AudioSourceException`. |

### VideoContainer

PG-02 (SDK 0.4.2, CN-09): containers for live video sent to cast devices.

| Member | Kind | Description |
|---|---|---|
| `FragmentedMp4` | field | Fragmented MP4 (H.264 + AAC), played live by Chromecasts. |
| `MpegTs` | field | MPEG transport stream (H.264 + AAC), played live by DLNA TVs. |

### VideoEncodeSettings

PG-02 (SDK 0.4.2): what to encode. Frames are `Width` × `Height` premultiplied BGRA at `FramesPerSecond`; `Audio` is the format of the audio written alongside, or null for video only.

| Member | Kind | Description |
|---|---|---|
| `VideoEncodeSettings` | constructor | PG-02 (SDK 0.4.2): what to encode. Frames are `Width` × `Height` premultiplied BGRA at `FramesPerSecond`; `Audio` is the format of the audio written alongside, or null for video only. |

