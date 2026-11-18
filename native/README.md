# Native libraries

AudioTastic uses a few native (C/C++) libraries and assets. They are **never committed**; these scripts download or build them.

- **Player libraries** (FFmpeg, miniaudio, SoundTouch) go to `native/out/<rid>/native/`. The app copies them into its output automatically when that folder exists.
- **Plugin libraries** (P4) go to `native/out/<library>/runtimes/<rid>/native/`. Each plugin copies them into its own folder (`plugins/<plugin>/runtimes/<rid>/native/`).
- **Downloads** are kept in `native/downloads/`, e.g. the SoundFont that the MIDI plugin bundles.

| Library | Licence | Needed from | How it is obtained |
|---|---|---|---|
| FFmpeg (avformat, avcodec, avutil, swresample) | LGPL (shared, no GPL parts) | P1 | `fetch_ffmpeg.py` (Windows, Linux: BtbN LGPL builds); `build_ffmpeg_macos.sh` (macOS, built from source) |
| miniaudio + AudioTastic shim (`miniaudio/at_miniaudio.{h,c}`) | Public domain / MIT-0 (shim: ours) | P1 | `CMakeLists.txt` |
| SoundTouch + SoundTouchDLL | LGPL-2.1 (shared) | P6 | `CMakeLists.txt` |
| libopenmpt 0.8.10 (+ mpg123 LGPL, ogg/vorbis BSD, zlib on Windows) | BSD-3-Clause | P4 (FM-07) | `fetch_libopenmpt.py` (Windows x64/arm64, official package, SHA-256 pinned) → `out/libopenmpt/`; `build_libopenmpt_unix.sh <rid>` (Linux/macOS from source, untested until CI) |
| OptimFROG Libraries 5.100 | closed-source freeware (policy exception, bundled) | P4 (FM-06) | `fetch_optimfrog.py` (win-x64, linux-x64, osx-x64 only; HTTP + SHA-1 from the Internet Archive) → `out/optimfrog/` |
| GeneralUser GS v2.0.3 SoundFont (asset, not code) | GeneralUser GS License v2.0 | P4 (FM-08) | `fetch_soundfont.py` (SHA-256 pinned) → `downloads/soundfont/` |
| VST3 host shim | MIT (VST3 SDK) | P12 | Added in that phase |
| ASIO shim | Steinberg ASIO licence (free, proprietary) | optional, deferred | Only if ASIO is revisited |

Runtime identifiers: `win-x64`, `win-arm64`, `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`.

## Commands

```
# FFmpeg (Windows and Linux targets; works from any OS with Python 3)
python native/fetch_ffmpeg.py --rid win-x64 linux-x64

# FFmpeg on macOS (run on a Mac, once per architecture)
native/build_ffmpeg_macos.sh arm64
native/build_ffmpeg_macos.sh x86_64

# miniaudio and SoundTouch (run on the target OS)
cmake -S native -B native/build/win-x64 -DCMAKE_BUILD_TYPE=Release -DAT_RID=win-x64
cmake --build native/build/win-x64 --config Release
cmake --install native/build/win-x64 --config Release

# Plugin libraries and assets (P4)
python native/fetch_libopenmpt.py --rid win-x64 --rid win-arm64
native/build_libopenmpt_unix.sh linux-x64            # on Linux/macOS
python native/fetch_optimfrog.py --rid win-x64 --rid linux-x64 --rid osx-x64
python native/fetch_soundfont.py
```

After fetching, rebuild the solution so the plugins pick the files up.

CI (`.github/workflows/ci.yml`) runs the same steps:
- the `native` job publishes `native-<rid>` and `libopenmpt-<rid>` artifacts;
- the `plugin-assets` job publishes the SoundFont and the OptimFROG libraries.

## miniaudio shim

`miniaudio/at_miniaudio.h` is a small, flat C API (opaque handles, fixed-layout structs) compiled into the `miniaudio` library. The .NET side (`AudioTastic.Core/Audio/Output/MiniaudioNative.cs`) P/Invokes only this API, never miniaudio's own structs, whose layout differs per platform. When the shim changes, bump `AT_MA_API_VERSION` and `MiniaudioNative.ApiVersionExpected` together.

API version 2 (P6) added sample formats (`AT_MA_FORMAT_F32/S16/S24/S32`), exclusive mode, `at_ma_device_formats` (the device's native formats, used for exclusive-mode negotiation) and `at_ma_device_internal_format`. The data callback receives a raw `void*` buffer in the opened format; integer conversion and dither are done in C# (`SampleConverter`), and `noClip` is set so miniaudio never alters the samples.

## Rules

- FFmpeg must stay an LGPL build: never `--enable-gpl` or `--enable-nonfree`.
- Every library and its licence must be listed in `THIRD-PARTY-NOTICES.md`.
- Downloads are verified against a pinned SHA-256 (or, for OptimFROG, published SHA-1) checksum. When upstream changes, check the new version and its licence before updating the checksum.
- OptimFROG: ship only the unmodified Libraries under their original names, with `license.html`. Never ship the command-line encoders (`ofr`, `ofs`, `off`); `scripts/generate_test_media.py` uses them locally for test files only.
- Each FFmpeg folder contains `FFMPEG-BUILDINFO.txt` (source URL and SHA-256) so a build can be traced.
