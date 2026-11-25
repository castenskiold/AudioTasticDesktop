# Third-party notices

AudioTastic uses the components below. Every entry must meet the project's license policy: free and open source; LGPL only when dynamically linked; closed-source freeware only when no open-source option exists, with no license key and no cost to the end user.

When you add a package or native library, add it here in the same pull request.

**Audit (P14, 25 Nov 2026):** every package the app and its built-in plugins ship (`dotnet list package --include-transitive`) was checked against its own `.nuspec` license or license file, and the FFmpeg build's configuration (`ffmpeg -buildconf`) was read. Corrections from the audit: the transitive packages below were added, Svg.Custom is MS-PL (not MIT), and the BtbN FFmpeg build was LGPL-3.0-or-later (configured with `--enable-version3`) with about 50 libraries inside whose license texts did not ship. That build was replaced by our own audio-focused LGPL-2.1-or-later build with five libraries, whose license texts ship with it (see *Libraries inside the FFmpeg build*).

## .NET packages (shipped)

| Component | License | Used for | Since |
|---|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent (with Avalonia.Skia, .HarfBuzz, .Win32, .X11, .Native, .FreeDesktop, .FreeDesktop.AtSpi, .Remote.Protocol) | MIT | UI framework | P0 |
| Avalonia.Angle.Windows.Natives (ANGLE, Windows builds) | BSD-3-Clause (The ANGLE Project Authors) | OpenGL ES on Direct3D for Avalonia's renderer | P0 |
| SkiaSharp with its native assets (Skia) | MIT (SkiaSharp); BSD-3-Clause (Skia; the package's THIRD-PARTY-NOTICES lists its parts) | 2-D drawing under Avalonia, SVG and the cast visualisations | P0 |
| HarfBuzzSharp with its native assets (HarfBuzz) | MIT (HarfBuzzSharp); MIT-style "Old MIT" (HarfBuzz) | Text shaping under Avalonia | P0 |
| MicroCom.Runtime | MIT | COM interop used by Avalonia | P0 |
| Tmds.DBus.Protocol | MIT | D-Bus for Avalonia on Linux (accessibility, tray) | P0 |
| Microsoft.Extensions.DependencyInjection, .Logging, .Logging.Abstractions (with .Options, .Primitives) | MIT | DI container, logging abstractions | P0 |
| Microsoft.Data.Sqlite | MIT | Cache database (AN-01) | P0 |
| SQLitePCLRaw (dependency of Microsoft.Data.Sqlite) | Apache-2.0 | SQLite native bindings | P0 |
| SQLite (bundled by SQLitePCLRaw) | Public domain | Cache database engine | P0 |
| Serilog, Serilog.Sinks.File, Serilog.Extensions.Logging | Apache-2.0 | Log files (TL-03) | P0 |
| FFmpeg.AutoGen | MIT | .NET bindings for the FFmpeg libraries (FM-01); loads them dynamically | P1 |
| Svg.Controls.Skia.Avalonia (with Svg.Skia, Svg.Model, Svg.Animation, Svg.SceneGraph, ShimSkiaSharp, ExCSS) | MIT | SVG skin images (UI-03) | P2 |
| Svg.Custom (dependency of Svg.Skia; the SVG document model) | MS-PL | SVG skin images (UI-03) | P2 |
| NJsonSchema (with NJsonSchema.Annotations, Namotion.Reflection, Newtonsoft.Json) | MIT | skin.json schema validation (UI-03) | P2 |
| TagLibSharp | LGPL-2.1 (separate, replaceable DLL) | Tag reading (MD-01) | P3 |
| MeltySynth | MIT (includes code from C# Synth and TinySoundFont/SFZero, also MIT) | MIDI synthesizer in the MIDI plugin (FM-08) | P4 |
| System.CommandLine | MIT (.NET Foundation) | Command-line parsing (SI-08) | P10 |
| ASP.NET Core / Kestrel (shared framework `Microsoft.AspNetCore.App`) | MIT (.NET Foundation) | The streaming server cast devices pull audio from (CN-04) | P11 |
| Microsoft.Windows.SDK.NET (Windows builds only; part of the .NET SDK's Windows targeting) | Microsoft Windows SDK license: free, redistributable, not open source (accepted by Big Apps ApS, 19 Nov 2026) | Windows Runtime projection for the media controls (SI-02) | P10 |
| WinRT.Runtime (C#/WinRT, Windows builds only) | MIT | Windows Runtime interop for the projection above | P10 |

## Packaging tools (P10)

| Component | License | Used for |
|---|---|---|
| Inno Setup (Jordan Russell, Martijn Laan) | Inno Setup License (permissive; free for any use, including commercial) | The Windows installer; its setup runtime is part of `AudioTastic-<version>-win-<arch>-setup.exe` |
| appimagetool and the AppImage runtime (AppImage project) | MIT | The Linux AppImage; the runtime is part of the `.AppImage` file |

## .NET packages (development only, not shipped)

| Component | License | Used for |
|---|---|---|
| xunit.v3, xunit.v3.assert (with Microsoft.Testing.Platform) | Apache-2.0 / MIT | Unit tests |
| Microsoft.Extensions.Logging.Console | MIT | Console logging in tools/AudioTastic.SoakTest |
| Avalonia.BuildServices (pulled in by Avalonia; a build-time task, not shipped) | MIT | Avalonia's build tooling (it sends anonymous build telemetry; set `AVALONIA_TELEMETRY_OPTOUT=1` to switch that off) |
| Avalonia.Headless | MIT | Accessibility tests of the skin controls without a window system (TL-04) |

## Native libraries (fetched or built by `native/`)

| Component | License | Used for | Since |
|---|---|---|---|
| FFmpeg 8.1.3 (libavcodec, libavformat, libavutil, libswresample) as shared libraries: **our own audio-focused build** on every platform (P14, `native/build_ffmpeg.sh`; all native audio decoders, only the encoders the player uses, no avfilter/avdevice/swscale) | **LGPL-2.1-or-later** (configured without `--enable-gpl`, `--enable-nonfree` or `--enable-version3`). The license texts of FFmpeg and of every library inside it ship as `FFMPEG-LICENSES.txt`, the exact sources, checksums and configure line as `FFMPEG-BUILDINFO.txt`; each release attaches the source archives and the build script (`AudioTastic-<version>-ffmpeg-sources.tar`) | Decoding (from P1); encoding for casting (P11) and conversion (P13); H.264 for casting visuals (P11) | P0 pipeline / P1 use; own build P14 |
| miniaudio (plus our own shim, native/miniaudio) | Public domain or MIT-0 | Audio output (PB-04) | P0 pipeline / P1 use |
| SoundTouch, SoundTouchDLL | LGPL-2.1 | Time-stretch | P0 pipeline / P6 use |
| Steinberg VST 3 SDK 3.8.1 (base, pluginterfaces, public.sdk; compiled into our `at_vst3host` library and `at_vst3scan` tool; no VSTGUI) | MIT (since SDK 3.8; LICENSE.txt checked 23 Nov 2026, shipped as `VST3SDK-LICENSE.txt`) | Hosting VST3 effects in the DSP chain (EQ-06) | P12 |
| Freeverb design by Jezar at Dreampoint (algorithm only; our own code) | Public domain | Reverb in the DSP chain (EQ-05) | P7 |
| libopenmpt 0.8.10 | BSD-3-Clause (parts BSL-1.0) | Tracker modules in the libopenmpt plugin (FM-07) | P4 |
| openmpt-mpg123 (mpg123, shipped with libopenmpt on Windows) | LGPL-2.1 (separate, replaceable DLL) | MP3-compressed samples inside modules | P4 |
| openmpt-ogg, openmpt-vorbis (libogg, libvorbis, shipped with libopenmpt on Windows) | BSD-3-Clause | Vorbis-compressed samples inside modules | P4 |
| openmpt-zlib (zlib, shipped with libopenmpt on Windows) | zlib | Compressed module formats | P4 |
| OptimFROG Libraries 5.100 / SDK 1.600 (OptimFROG.dll, libOptimFROG.so, libOptimFROG.dylib), by Florin Ghido | **Closed-source freeware** (license-policy exception: no open-source decoder exists, no license key, free for the end user). The license allows redistributing the unmodified Libraries under their original names with software that uses them via the SDK, free and commercial; shipped with its `license.html` (as `OptimFROG-license.html`). The command-line encoders are not shipped. **Not part of the AudioTastic package** (since 19 Nov 2026): the OptimFROG plugin is a separate `.atplugin` download that contains the libraries. | OptimFROG plugin (FM-06), x64 only | P4 |

### Libraries inside the FFmpeg build

Linked statically into the FFmpeg libraries (P14; before, the Windows and Linux builds came from BtbN and carried about 50 libraries, mostly for video, whose license texts did not ship). Their license texts are in `FFMPEG-LICENSES.txt` next to the libraries.

| Library | License | Used for | Platforms |
|---|---|---|---|
| LAME 3.100 (libmp3lame) | LGPL-2.0-or-later | MP3 encoding (conversion, casting) | all |
| Opus 1.6.1 (libopus) | BSD-3-Clause | Opus encoding | all |
| libogg 1.3.6, libvorbis 1.3.7 | BSD-3-Clause | Vorbis encoding | all |
| OpenH264 2.6.0 (libopenh264) | BSD-2-Clause | H.264 for casting visuals | Linux |
| zlib 1.3.2 | zlib | PNG cover art, compressed tags and MP4 headers | Windows (the system's libz on Linux and macOS) |

Operating-system libraries used, not shipped: Media Foundation and Schannel (Windows; H.264 and HTTPS), VideoToolbox and Secure Transport (macOS), OpenSSL 3 (Linux, the system's `libssl.so.3`; Apache-2.0, compatible with the LGPL) and the C++ runtime `libstdc++` (Linux, for OpenH264). The Windows libraries are cross-built with llvm-mingw (a build tool, not shipped) and import only Windows' own DLLs.

**Patents (not a copyright license matter, noted for releases):** encoding H.264 (casting visuals) uses the operating system's encoder on Windows (Media Foundation) and macOS (VideoToolbox), which carry the vendors' licenses; on Linux it uses OpenH264 compiled from source, which is not covered by Cisco's patent license (that covers only Cisco's own binaries). AAC encoding uses FFmpeg's own encoder. MP3 patents have expired.

The license texts of libopenmpt and its helper libraries are shipped next to them in the plugin folder (`plugins/AudioTastic.Input.OpenMpt/runtimes/<rid>/native/libopenmpt-*.txt`).

## Fonts and assets

| Component | License | Used for |
|---|---|---|
| Inter (`docs/pdf-src/fonts`) | SIL Open Font License 1.1 | Project PDF documents; default skin font from P2 |
| AudioTastic logo (`assets/branding`) | Owned by Big Apps ApS | App icon, branding |
| GeneralUser GS v2.0.3 SoundFont by S. Christian Collins (`GeneralUser-GS.sf2`) | GeneralUser GS License v2.0 (custom, permissive: free use in software projects, private or commercial, no attribution required; approved by Anders, 17 Nov 2026). **Not bundled** (since 19 Nov 2026): the MIDI plugin downloads it, unmodified, only when the user clicks Download on its settings page, from the `soundfont-generaluser-gs-2.0.3` release of this repository (SHA-256 checked), and saves its `LICENSE.txt` next to it. | General MIDI SoundFont for the MIDI plugin (FM-08) |
