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
```

In Visual Studio, open `AudioTastic.slnx` and set **AudioTastic.App** as the startup project.

Useful switches:

- `--portable`: keep all data in a `Data` folder next to the executable (TL-05). Creating an empty file named `portable` next to the executable does the same.
- `--crash-test`: throw an exception after start-up, to check crash reporting (TL-03). The next start offers the report.

## Publish (self-contained)

```
dotnet publish src/AudioTastic.App -c Release -r win-x64 --self-contained -o publish/win-x64
```

Runtime identifiers: `win-x64`, `win-arm64`, `osx-arm64`, `osx-x64`, `linux-x64`, `linux-arm64`.

## Native libraries and test media

- **Native libraries:** see `native/README.md` (FFmpeg LGPL builds, miniaudio, SoundTouch).
- **Test media:** run `python scripts/generate_test_media.py`. It writes to `tests/media/generated/`, which is ignored by git.

## Repository layout

| Folder | Contents |
|---|---|
| `src/` | Application and library projects (see CLAUDE.md, section 8) |
| `tests/` | xUnit test projects |
| `plugins/` | Built-in plugins (from P4) |
| `native/` | Scripts that fetch or build the native libraries |
| `scripts/` | Developer scripts (test media) |
| `assets/branding/` | Logo and app icons |
| `docs/pdf-src/` | Sources of the PDF documents |
| `.vscode/` | VS Code tasks, launch configurations and recommended extensions |
| `.github/workflows/` | CI workflow (build/test on 3 OSes, publish 6 RIDs, native libraries) |
