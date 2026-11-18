# AudioTastic Desktop

A Winamp-style desktop audio player for Windows, macOS and Linux, built with .NET 10 and Avalonia.

- **Specification and plan:** the PDFs in this folder (Required Features, Feature Specification, Implementation Plan).
- **Working notes for developers and AI sessions:** `CLAUDE.md`.

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
- Any other argument is a file, folder or playlist to add to the active playlist and play (the full command line, SI-08, comes in P10).

**Windows and controls:**
- The player has a skinned main window, mini player (Ctrl+W) and playlist window (Alt+E), docked together.
- Open files with L or the eject button, or drop files and folders on any player window; they are added to the active playlist and played.
- Internet radio: Ctrl+L (Open URL) takes stream URLs and `.m3u`/`.pls` links.
- Winamp keys work: Z/X/C/V/B transport, J search, S shuffle, R repeat, Up/Down volume, Ctrl+P settings.
- Alt+G equalizer; Ctrl+Shift+K visualisation plugin window.
- Playback extras: Q queues the selected rows (play next), Shift+V stops after the current track, [ sets A-B repeat
  (markers on the waveform can be dragged); Main menu › Playback › Speed… and Main menu › Sleep timer….

**Formats** (all played through plugins):
- FFmpeg: WAV, FLAC, MP3, AIFF, APE, AAC/M4A, WavPack, Ogg Vorbis, Opus, ALAC, WMA, Musepack, TTA, Shorten, DSD (DSF/DFF, as PCM or DoP), http streams;
- MIDI with a bundled SoundFont;
- tracker modules (MOD, XM, IT, S3M, ...);
- OptimFROG (x64).

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
