# Native libraries

AudioTastic uses a few native (C/C++) libraries and assets. They are **never committed**; these scripts download or build them.

- **Player libraries** (FFmpeg, miniaudio, SoundTouch) go to `native/out/<rid>/native/`. The app copies them into its output automatically when that folder exists.
- **Plugin libraries** (P4) go to `native/out/<library>/runtimes/<rid>/native/`. Each plugin copies them into its own folder (`plugins/<plugin>/runtimes/<rid>/native/`).
- **Downloads** are kept in `native/downloads/`, e.g. the SoundFont that the MIDI plugin bundles.

| Library | Licence | Needed from | How it is obtained |
|---|---|---|---|
| FFmpeg 8.1.3 (avformat, avcodec, avutil, swresample), our own audio-focused build with LAME, Opus, Ogg/Vorbis (+ OpenH264 on Linux, zlib on Windows) linked in | LGPL-2.1-or-later (shared, no GPL, nonfree or version3 parts) | P1 (own build P14) | `build_ffmpeg.sh <rid>` (from pinned, SHA-256-checked sources; Windows cross-built on Linux with llvm-mingw); `fetch_ffmpeg.py` downloads CI's build (needs `gh`) |
| miniaudio + AudioTastic shim (`miniaudio/at_miniaudio.{h,c}`) | Public domain / MIT-0 (shim: ours) | P1 | `CMakeLists.txt` |
| SoundTouch + SoundTouchDLL | LGPL-2.1 (shared) | P6 | `CMakeLists.txt` |
| libopenmpt 0.8.10 (+ mpg123 LGPL, ogg/vorbis BSD, zlib on Windows) | BSD-3-Clause | P4 (FM-07) | `fetch_libopenmpt.py` (Windows x64/arm64, official package, SHA-256 pinned) → `out/libopenmpt/`; `build_libopenmpt_unix.sh <rid>` (Linux/macOS from source, untested until CI) |
| OptimFROG Libraries 5.100 | closed-source freeware (policy exception, bundled) | P4 (FM-06) | `fetch_optimfrog.py` (win-x64, linux-x64, osx-x64 only; HTTP + SHA-1 from the Internet Archive) → `out/optimfrog/` |
| GeneralUser GS v2.0.3 SoundFont (asset, not code) | GeneralUser GS License v2.0 | P4 (FM-08) | `fetch_soundfont.py` (SHA-256 pinned) → `downloads/soundfont/` |
| VST3 host shim `at_vst3host` + scanner `at_vst3scan` (`vst3host/`), over the Steinberg VST3 SDK 3.8.1 | MIT (VST3 SDK) | P12 (EQ-06) | `CMakeLists.txt` (fetches the SDK at tag `v3.8.1_build_84`, submodules base, cmake, pluginterfaces, public.sdk only) |
| ASIO shim | Steinberg ASIO licence (free, proprietary) | optional, deferred | Only if ASIO is revisited |

Runtime identifiers: `win-x64`, `win-arm64`, `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`.

## Commands

```
# FFmpeg: download CI's build of native/build_ffmpeg.sh (any OS with Python 3 and a signed-in GitHub CLI)
python native/fetch_ffmpeg.py --rid win-x64

# FFmpeg: build it yourself (Linux for linux-* and win-*, a Mac for osx-*; needs curl, make, pkg-config, nasm, and
# libssl-dev on Linux). Takes 5-15 minutes.
native/build_ffmpeg.sh linux-x64
native/build_ffmpeg.sh win-x64        # cross build with llvm-mingw (downloaded once, pinned)
native/build_ffmpeg.sh sources        # only download and verify every source archive

# miniaudio and SoundTouch (run on the target OS)
cmake -S native -B native/build/win-x64 -DCMAKE_BUILD_TYPE=Release -DAT_RID=win-x64
cmake --build native/build/win-x64 --config Release
cmake --install native/build/win-x64 --config Release

# VST3 test fixtures (EQ-06): add -DAT_VST3_TEST_PLUGINS=ON when configuring; installs the SDK's ADelay example and a
# module that crashes when scanned to native/out/<rid>/vst3-test/ (used by the tests, never shipped)

# Plugin libraries and assets (P4)
python native/fetch_libopenmpt.py --rid win-x64 --rid win-arm64
native/build_libopenmpt_unix.sh linux-x64            # on Linux/macOS
python native/fetch_optimfrog.py --rid win-x64 --rid linux-x64 --rid osx-x64
python native/fetch_soundfont.py
```

After fetching, rebuild the solution so the plugins pick the files up.

CI (`.github/workflows/ci.yml`) runs the same steps:
- the `ffmpeg` job runs `build_ffmpeg.sh` per RID (cached until the script changes) and publishes `ffmpeg-<rid>`; the release job attaches the source archives and the script (`AudioTastic-<version>-ffmpeg-sources.tar`, for the LGPL);
- the `native` job publishes `native-<rid>` and `libopenmpt-<rid>` artifacts;
- the `plugin-assets` job publishes the SoundFont and the OptimFROG libraries.

## FFmpeg build

`build_ffmpeg.sh` (P14) replaced the BtbN downloads (Windows, Linux: about 50 libraries inside, mostly video, avcodec 87 MB) and the macOS-only script. What it builds:

- **Libraries:** avformat, avcodec, avutil, swresample; no avfilter, avdevice, swscale or programs.
- **Decoders:** every native audio decoder (read from `allcodecs.c` between `/* audio codecs */` and `/* subtitles */`), plus mjpeg and png for cover-art streams.
- **Encoders:** flac, alac, aac, pcm_s16le/s24le, libmp3lame, libopus, libvorbis, and the platform's H.264 encoder (h264_mf on Windows, h264_videotoolbox on macOS, libopenh264 on Linux). The script fails if one is missing.
- **Demuxers, muxers, parsers:** all. **Protocols:** file, pipe, http(s), httpproxy, tcp, tls, crypto, data.
- **TLS:** Schannel (Windows), Secure Transport (macOS), the system's OpenSSL 3 (Linux).
- **Threads:** w32threads / pthreads. The earlier macOS build had none, and no TLS, zlib or VideoToolbox, because `--disable-autodetect` also turns those off.
- **macOS:** deployment target 12.0.
- **Windows:** the DLLs may import only Windows' own DLLs (checked).

Each output folder gets `FFMPEG-BUILDINFO.txt` (sources, SHA-256, configure line) and `FFMPEG-LICENSES.txt` (FFmpeg's LGPL plus the license of every library inside). To upgrade, change the version and SHA-256 in `SOURCES`, check the new licence, and keep `FfmpegLibrary.AvCodecMajor` and the FFmpeg.AutoGen package in step.

The FFmpeg command-line tool used to generate test media (`scripts/generate_test_media.py`) is a separate thing: any FFmpeg with libmp3lame, libvorbis and libopus works (a distribution package, or a downloaded build kept in `downloads/`).

## miniaudio shim

`miniaudio/at_miniaudio.h` is a small, flat C API (opaque handles, fixed-layout structs) compiled into the `miniaudio` library. The .NET side (`AudioTastic.Core/Audio/Output/MiniaudioNative.cs`) P/Invokes only this API, never miniaudio's own structs, whose layout differs per platform. When the shim changes, bump `AT_MA_API_VERSION` and `MiniaudioNative.ApiVersionExpected` together.

API version 2 (P6) added sample formats (`AT_MA_FORMAT_F32/S16/S24/S32`), exclusive mode, `at_ma_device_formats` (the device's native formats, used for exclusive-mode negotiation) and `at_ma_device_internal_format`. The data callback receives a raw `void*` buffer in the opened format; integer conversion and dither are done in C# (`SampleConverter`), and `noClip` is set so miniaudio never alters the samples.

## VST3 host shim

`vst3host/at_vst3host.h` is a flat C API (opaque handle, API version 2) over the SDK's hosting classes: scan a module, open a class, set up stereo processing, process interleaved float in place, latency, output parameters (meters: queued lock-free on the audio thread, handed to the edit controller by `at_vst3_flush_output` on the UI thread; API v2), restart flags (`at_vst3_take_restart_flags`, e.g. latency changed; API v2), state (component + controller, length-prefixed), and the editor in a native window (HWND, NSView, X11). `at_vst3scan <module>` prints a module's effect classes as JSON; the player runs it as a separate process per module so a plug-in that crashes or hangs while loading is blacklisted instead of taking the player down. Bump `AT_VST3_API_VERSION` and `Vst3Native.ApiVersionExpected` together.

The SDK's `HostProcessData` owns the bus buffers (it frees them): audio is copied into and out of them, never replaced by our own pointers (that corrupted the heap in the first version).

## Rules

- FFmpeg must stay an LGPL-2.1 build: never `--enable-gpl`, `--enable-nonfree` or `--enable-version3`, and no external library under the GPL.
- Every library and its licence must be listed in `THIRD-PARTY-NOTICES.md`.
- Downloads are verified against a pinned SHA-256 (or, for OptimFROG, published SHA-1) checksum. When upstream changes, check the new version and its licence before updating the checksum.
- OptimFROG: ship only the unmodified Libraries under their original names, with `license.html`. Never ship the command-line encoders (`ofr`, `ofs`, `off`); `scripts/generate_test_media.py` uses them locally for test files only.
- Each FFmpeg folder contains `FFMPEG-BUILDINFO.txt` (source URLs, SHA-256, configure line) so a build can be traced, and `FFMPEG-LICENSES.txt`.
