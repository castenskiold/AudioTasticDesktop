# Third-party notices

AudioTastic uses the components below. Every entry must meet the licence policy in `CLAUDE.md` (section 5): free and open source; LGPL only when dynamically linked; closed-source freeware only when no open-source option exists, with no licence key and no cost to the end user.

When you add a package or native library, add it here in the same pull request.

## .NET packages (shipped)

| Component | Licence | Used for | Since |
|---|---|---|---|
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent | MIT | UI framework | P0 |
| Microsoft.Extensions.DependencyInjection, .Logging, .Logging.Abstractions | MIT | DI container, logging abstractions | P0 |
| Microsoft.Data.Sqlite | MIT | Cache database (AN-01) | P0 |
| SQLitePCLRaw (dependency of Microsoft.Data.Sqlite) | Apache-2.0 | SQLite native bindings | P0 |
| SQLite (bundled by SQLitePCLRaw) | Public domain | Cache database engine | P0 |
| Serilog, Serilog.Sinks.File, Serilog.Extensions.Logging | Apache-2.0 | Log files (TL-03) | P0 |
| FFmpeg.AutoGen | MIT | .NET bindings for the FFmpeg libraries (FM-01); loads them dynamically | P1 |
| Svg.Controls.Skia.Avalonia (with Svg.Skia, Svg.Model, Svg.Custom) | MIT | SVG skin images (UI-03) | P2 |
| NJsonSchema (with NJsonSchema.Annotations, Namotion.Reflection, Newtonsoft.Json) | MIT | skin.json schema validation (UI-03) | P2 |
| TagLibSharp | LGPL-2.1 (separate, replaceable DLL) | Tag reading (MD-01) | P3 |
| MeltySynth | MIT (includes code from C# Synth and TinySoundFont/SFZero, also MIT) | MIDI synthesizer in the MIDI plugin (FM-08) | P4 |

## .NET packages (development only, not shipped)

| Component | Licence | Used for |
|---|---|---|
| xunit.v3, xunit.v3.assert (with Microsoft.Testing.Platform) | Apache-2.0 / MIT | Unit tests |
| Microsoft.Extensions.Logging.Console | MIT | Console logging in tools/AudioTastic.SoakTest |

## Native libraries (fetched or built by `native/`)

| Component | Licence | Used for | Since |
|---|---|---|---|
| FFmpeg (libavcodec, libavformat, libavutil, libswresample), LGPL shared build | LGPL-2.1-or-later | Decoding (from P1), encoding (P11, P13) | P0 pipeline / P1 use |
| miniaudio (plus our own shim, native/miniaudio) | Public domain or MIT-0 | Audio output (PB-04) | P0 pipeline / P1 use |
| SoundTouch, SoundTouchDLL | LGPL-2.1 | Time-stretch | P0 pipeline / P6 use |
| libopenmpt 0.8.10 | BSD-3-Clause (parts BSL-1.0) | Tracker modules in the libopenmpt plugin (FM-07) | P4 |
| openmpt-mpg123 (mpg123, shipped with libopenmpt on Windows) | LGPL-2.1 (separate, replaceable DLL) | MP3-compressed samples inside modules | P4 |
| openmpt-ogg, openmpt-vorbis (libogg, libvorbis, shipped with libopenmpt on Windows) | BSD-3-Clause | Vorbis-compressed samples inside modules | P4 |
| openmpt-zlib (zlib, shipped with libopenmpt on Windows) | zlib | Compressed module formats | P4 |
| OptimFROG Libraries 5.100 / SDK 1.600 (OptimFROG.dll, libOptimFROG.so, libOptimFROG.dylib), by Florin Ghido | **Closed-source freeware** (licence-policy exception: no open-source decoder exists, no licence key, free for the end user). The licence allows redistributing the unmodified Libraries under their original names with software that uses them via the SDK, free and commercial; shipped with its `license.html` (as `OptimFROG-license.html`). The command-line encoders are not shipped. | OptimFROG plugin (FM-06), x64 only | P4 |

The licence texts of libopenmpt and its helper libraries are shipped next to them in the plugin folder (`plugins/AudioTastic.Input.OpenMpt/runtimes/<rid>/native/libopenmpt-*.txt`).

## Fonts and assets

| Component | Licence | Used for |
|---|---|---|
| Inter (`docs/pdf-src/fonts`) | SIL Open Font License 1.1 | Project PDF documents; default skin font from P2 |
| AudioTastic logo (`assets/branding`) | Owned by the AudioTastic project | App icon, branding |
| GeneralUser GS v2.0.3 SoundFont by S. Christian Collins (`GeneralUser-GS.sf2`) | GeneralUser GS License v2.0 (custom, permissive: free use in software projects, private or commercial, no attribution required; approved by Anders, 17 Nov 2026). Shipped with its `LICENSE.txt` in `plugins/AudioTastic.Input.Midi/soundfont/`. | Bundled General MIDI SoundFont for the MIDI plugin (FM-08) |
