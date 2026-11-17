# Native libraries

AudioTastic uses a few native (C/C++) libraries. They are **never committed**; these scripts download or build them into `native/out/<rid>/native/`. The app project copies them into its output automatically when that folder exists.

| Library | Licence | Needed from | How it is obtained |
|---|---|---|---|
| FFmpeg (avformat, avcodec, avutil, swresample) | LGPL (shared, no GPL parts) | P1 | `fetch_ffmpeg.py` (Windows, Linux: BtbN LGPL builds); `build_ffmpeg_macos.sh` (macOS, built from source) |
| miniaudio + AudioTastic shim (`miniaudio/at_miniaudio.{h,c}`) | Public domain / MIT-0 (shim: ours) | P1 | `CMakeLists.txt` |
| SoundTouch + SoundTouchDLL | LGPL-2.1 (shared) | P6 | `CMakeLists.txt` |
| libopenmpt | BSD-3 | P4 | Added in P4 (FM-07) |
| VST3 host shim, ASIO shim | MIT (VST3 SDK), Steinberg ASIO licence | P6 / P12 | Added in those phases |

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
```

CI (`.github/workflows/ci.yml`, job `native`) runs the same steps and publishes a `native-<rid>` artifact.

## miniaudio shim

`miniaudio/at_miniaudio.h` is a small, flat C API (opaque handles, fixed-layout structs) compiled into the `miniaudio` library. The .NET side (`AudioTastic.Core/Audio/Output/MiniaudioNative.cs`) P/Invokes only this API, never miniaudio's own structs, whose layout differs per platform. When the shim changes, bump `AT_MA_API_VERSION` and `MiniaudioNative.ApiVersionExpected` together.

## Rules

- FFmpeg must stay an LGPL build: never `--enable-gpl` or `--enable-nonfree`.
- Every library and its licence must be listed in `THIRD-PARTY-NOTICES.md`.
- Each FFmpeg folder contains `FFMPEG-BUILDINFO.txt` (source URL and SHA-256) so a build can be traced.
