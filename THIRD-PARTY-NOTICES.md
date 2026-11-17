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

## Fonts and assets

| Component | Licence | Used for |
|---|---|---|
| Inter (`docs/pdf-src/fonts`) | SIL Open Font License 1.1 | Project PDF documents; default skin font from P2 |
| AudioTastic logo (`assets/branding`) | Owned by the AudioTastic project | App icon, branding |
