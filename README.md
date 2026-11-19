# AudioTastic Desktop

A Winamp replacement with extra features: a Winamp-style desktop audio player for Windows, macOS and Linux, built with .NET 10 and Avalonia.

- **Specification and plan:** the PDFs in this folder (Required Features, Feature Specification, Implementation Plan). Their sources are in `docs/pdf-src/`.
- **Skins and plugins:** `docs/skins/` (skin format) and `docs/plugins/` (plugin guide; the SDK is `src/AudioTastic.Sdk`).

## Requirements

- .NET SDK 10.0.100 or newer (`global.json` rolls forward to the latest feature band).
- **VS Code** with C# Dev Kit (recommended; see `.vscode/`), **Visual Studio 2026** (needed for .NET 10), or JetBrains Rider.
- Python 3 and FFmpeg for the test media generator; CMake 3.24+ and a C compiler for the native libraries.

## Build, test and run

```
dotnet restore AudioTastic.slnx
dotnet build AudioTastic.slnx
dotnet test AudioTastic.slnx
dotnet run --project src/AudioTastic.App
dotnet run --project src/AudioTastic.App -- --crash-test   # app switches go after --
dotnet run --project src/AudioTastic.App -- song.flac other.mp3   # P1: play files
```

In Visual Studio, open `AudioTastic.slnx` and set **AudioTastic.App** as the startup project.

Useful switches:

- `--portable`: keep all data in a `Data` folder next to the executable (TL-05). Creating an empty file named `portable` next to the executable does the same.
- `--crash-test`: throw an exception after start-up, to check crash reporting (TL-03). The next start offers the report.
- `--open files…`: add the files, folders or playlists to the active playlist and play the first one.
- `--enqueue files…`: only add them to the end of the active playlist.
- Any other argument is a file, folder or playlist to open; *Settings › System integration* decides whether it is played (default) or enqueued.
- Player commands (SI-08), sent to the running player: `--play`, `--pause`, `--playpause`, `--stop`, `--next`, `--prev`, `--volume N` (0–100), `--seek s` (seconds, e.g. `12.5`), `--quit`. They can be combined, e.g. `AudioTastic --next --volume 40`, and run after any files. If AudioTastic is not running, it starts (except for `--quit`).
- On Windows every option can also be written with a slash: `/next`, `/volume 40`, `/enqueue song.flac`.
- `--help` lists everything, `--version` prints the version; mistakes (an unknown option, `--volume 300`) are reported on the console with exit code 1 and the player is not started.

**Single instance, file types and context menu (Windows):**
- AudioTastic runs once per user (a portable copy runs on its own). Starting it again hands the files to the running player and brings it to the front; files opened together, e.g. several selected in Explorer, are added as one batch.
- The first start asks whether to use AudioTastic for audio files. *Yes, set up* registers it for every audio and playlist type it plays and opens Windows' *Settings › Default apps*, where you choose it as the default (Windows lets only you do that). Double-clicked files then go to the running player. Change it later in *Settings › System integration* (*Choose default apps…*, or switch the registration off). Portable mode doesn't ask.
- Right-clicking audio files and playlists in Explorer shows **Open in AudioTastic** and **Enqueue in AudioTastic** with the app icon. On Windows 11 they are under *Show more options* (or Shift+right-click). The entries are per user (no administrator rights), follow the program if you move it (they are rewritten at every start) and cover every file type the enabled plugins play. Switch them off in *Settings › System integration* before deleting the program; in portable mode they are off unless you switch them on.

**Windows and controls:**
- The player has a skinned main window, mini player (Ctrl+W), playlist (Alt+E), equalizer (Alt+G) and Art & Lyrics (Alt+L) windows, docked together. Windows snap to each other and to the screen edges within 10 px when moved, and when the playlist or Art & Lyrics window is resized by its corner (e.g. its right edge lines up with the player's).
- Open files with L or the eject button, or drop files and folders on any player window; they are added to the active playlist and played.
- Internet radio: Ctrl+L (Open URL) takes stream URLs and `.m3u`/`.pls` links.
- Winamp keys work: Z/X/C/V/B transport, J search, S shuffle, R repeat, Up/Down volume, Ctrl+P settings.
- Alt+G equalizer; Ctrl+Shift+K visualisation plugin window.
- Playback extras: Q queues the selected rows (play next), Shift+V stops after the current track, [ sets A-B repeat
  (markers on the waveform can be dragged); Main menu › Playback › Speed… and Main menu › Sleep timer….

**Formats** (all played through plugins):
- FFmpeg: WAV, FLAC, MP3, AIFF, APE, AAC/M4A, WavPack, Ogg Vorbis, Opus, ALAC, WMA, Musepack, TTA, Shorten, DSD (DSF/DFF, as PCM or DoP), http streams;
- MIDI through a SoundFont (GeneralUser GS is downloaded from the MIDI plugin's settings, or use any .sf2);
- tracker modules (MOD, XM, IT, S3M, ...);
- OptimFROG (x64; the optional `.atplugin` from the Releases page).

**Waveform and loudness:** the strip under the display is a waveform seek bar (click or drag to seek). ReplayGain (Settings › Playback) uses tags or a background EBU R128 analysis; "RG" shows while a gain is applied.

**Equalizer and effects:** Alt+G opens the equalizer (10 bands or parametric, presets, Winamp .eqf import, auto EQ per file or genre); its DSP button opens the chain with compressor, limiter, reverb and stereo widening. The display shows a spectrum or oscilloscope (click to switch).

**Playback engine:** gapless playback, short fades on pause/stop/seek, session and per-file resume (Settings › Playback), and exclusive bit-perfect output with 16/24/32-bit formats and DoP (Settings › Output).

**Plugins** are managed in Settings › Plugins. Skins are documented in `docs/skins/skin-format.md`; plugins in `docs/plugins/plugin-guide.md`.

The tests need the native libraries, the plugin assets and the generated test media; without them the decoder and output tests are reported as skipped. `python scripts/check_plugin_template.py` checks that a plugin made from the `dotnet new` template and built only from the SDK package loads, runs and unloads.

## Publish (self-contained)

```
dotnet publish src/AudioTastic.App -c Release -r win-x64 --self-contained -o publish/win-x64
```

Runtime identifiers: `win-x64`, `win-arm64`, `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`.

## Native libraries and test media

- **Native libraries:** see `native/README.md` (FFmpeg LGPL builds, miniaudio, SoundTouch; for plugins libopenmpt, OptimFROG and the GeneralUser GS SoundFont).
- **Test media:** run `python scripts/generate_test_media.py` (add `--ffmpeg <path>` when ffmpeg is not on PATH; the Windows FFmpeg download in `native/downloads` contains one). It writes to `tests/media/generated/`, which is ignored by git.
- **Soak test (P1 exit criterion):** `dotnet run -c Release --project tools/AudioTastic.SoakTest -- --minutes 60 --buffer 20` plays a long file silently through the real output and reports underruns, slow audio callbacks, memory and handles.

## Repository layout

| Folder | Contents |
|---|---|
| `src/` | Application and library projects (see CLAUDE.md, section 8) |
| `tests/` | xUnit v3 test projects and shared test helpers (`AudioTastic.TestSupport`) |
| `plugins/` | Built-in plugins written against the SDK: FFmpeg, MIDI, libopenmpt and OptimFROG input plugins; copied to `plugins/` next to the app |
| `samples/` | Sample plugins (input, DSP, visualisation, UI panel/general), built to `artifacts/atplugins/` |
| `templates/` | `dotnet new audiotastic-plugin` template |
| `tools/` | Developer tools (soak test) |
| `native/` | Scripts that fetch or build the native libraries |
| `scripts/` | Developer scripts (test media, sample skin, plugin template check) |
| `assets/branding/` | Logo and app icons |
| `docs/skins/`, `docs/plugins/` | Skin format and schema; plugin author guide |
| `docs/pdf-src/` | Sources of the PDF documents |
| `.vscode/` | VS Code tasks, launch configurations and recommended extensions |
| `.github/workflows/` | CI workflow (build/test on 3 OSes, publish 6 RIDs, native libraries) |

## License

AudioTastic is free software under the MIT license (`LICENSE`), copyright Big Apps ApS. Third-party components keep their own licenses; see `THIRD-PARTY-NOTICES.md`.

## Downloads

Releases are on the [Releases page](https://github.com/castenskiold/AudioTasticDesktop/releases): archives for Windows, macOS and Linux (x64 and ARM64) and the optional OptimFROG plugin. Each release's notes say what was tested.

## Code signing policy

Windows releases are **not code-signed yet**, so Windows SmartScreen may say the publisher is unknown: choose *More info › Run anyway*. Each release is built by this repository's GitHub Actions workflow from its own source code (see the workflow run linked from the release).

When the project is signed (we plan to apply to the [SignPath Foundation](https://signpath.org) again once AudioTastic is more widely used), only those release builds will be signed and every signing request will be approved by hand:

- **Committers and reviewers:** [Big Apps ApS](https://github.com/castenskiold) (repository owner)
- **Approvers:** [Big Apps ApS](https://github.com/castenskiold)

The optional OptimFROG plugin (closed-source freeware libraries) and the GeneralUser GS SoundFont are separate downloads and are not part of the app package.

## Privacy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it. Specifically, it connects to the internet only when you:

- play an internet radio stream or URL you opened;
- click *Look up online…* in the tag editor or playlist (sends the track's title, artist and length to MusicBrainz, and fetches covers from the Cover Art Archive);
- click *Download GeneralUser GS* in the MIDI plugin's settings (downloads the SoundFont from this repository's releases).

It sends no telemetry and has no automatic updates. Settings, playlists and caches stay on your computer.
