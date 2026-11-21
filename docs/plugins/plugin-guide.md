# Writing AudioTastic plugins

This guide is for developers who want to extend AudioTastic Desktop: new file formats, effects, visualisations, background features, windows or cast targets. It covers SDK 0.4 (pre-release; see *Versioning*).

**Licence:** the SDK package, the samples and the template are **MIT licensed**, so your plugin can be free or commercial under any licence you choose.

## Quick start

1. Build or get the SDK package. In this repository: `dotnet pack src/AudioTastic.Sdk -c Release -o artifacts/packages`.
2. Install the template and create a plugin:
   ```
   dotnet new install templates/AudioTastic.Plugin
   dotnet new audiotastic-plugin -n MyPlugin
   ```
3. Build it: `dotnet build MyPlugin -c Release`. If the SDK comes from a local folder, add that folder as a NuGet source, e.g. in a `nuget.config`.
4. The build writes `bin/Release/MyPlugin.atplugin`. Install it in AudioTastic: **Settings › Plugins › Install plugin…**

The template is a *general* plugin that counts the tracks played. `scripts/check_plugin_template.py` runs these steps end to end and loads the result in the plugin host.

Working examples are in `samples/`:

| Sample | Shows |
|---|---|
| `SineInput` | an input plugin: a decoder for a new file type (`.sine`) |
| `GainDsp` | a DSP effect, with no allocation on the audio thread |
| `PeakMeter` | a visualisation with its own Avalonia control |
| `HelloPanel` | a UI panel window, a general plugin and a settings page |

Each sample builds to `artifacts/atplugins/<Configuration>/<name>.atplugin`.

## What a plugin is

A plugin is a folder (or a `.atplugin` zip of that folder) containing:
- `plugin.json`: the manifest;
- the entry assembly and the plugin's own dependencies;
- optionally, native libraries in `runtimes/<rid>/native/` (e.g. `runtimes/win-x64/native/foo.dll`).

AudioTastic looks for plugins in two places:
- `plugins/` next to the program (built-in plugins);
- the `plugins/` folder in the data folder (installed plugins).

An installed plugin with the same id replaces a built-in one.

### plugin.json

```json
{
  "id": "com.example.myplugin",
  "name": "My plugin",
  "version": "1.0.0",
  "author": "Me",
  "description": "What it does, in one or two sentences.",
  "website": "https://example.com",
  "license": "MIT",
  "sdkVersion": "0.4.2",
  "entryAssembly": "MyPlugin.dll",
  "types": ["general"],
  "priority": 0
}
```

| Field | Meaning |
|---|---|
| `id` | Unique, lower case, reverse-domain style. Also the key for the plugin's settings and data folder. |
| `name`, `version`, `author`, `description`, `website`, `license` | Shown in the plugin manager. |
| `sdkVersion` | The SDK version the plugin was built for (see *Versioning*). |
| `entryAssembly` | The DLL with the plugin classes, relative to the plugin folder. |
| `types` | One or more of `input`, `output`, `dsp`, `visualisation`, `general`, `uiPanel`, `castTarget`. |
| `priority` | Input plugins only: when several decoders accept the same extension, the higher priority wins. The default is 0; the built-in FFmpeg decoder uses -100. |

## Plugin classes and lifecycle

The host creates **every public, non-abstract class that implements `IPlugin` and has a parameterless constructor** in the entry assembly. One assembly may hold several plugin classes, of different types.

1. **Create and initialise:** the class is created, then `Initialize(IPluginContext)` is called once. The context gives:
   - `PluginId`;
   - `PluginFolder` (read-only; it may be inside the program folder);
   - `DataFolder` (writable, created on first use);
   - `Settings`;
   - `Player` (the `IPlayerApi`);
   - `Logger` (writes to the player's log files, tagged with the plugin id).
2. **Use:** the player uses the plugin according to its type (below).
3. **Dispose:** `Dispose()` is called when the plugin is disabled, uninstalled or replaced, or when the player exits. Release native resources and stop background work here.

### Plugin types

| Interface | Used for | Where it appears | Hosted since |
|---|---|---|---|
| `IInputPlugin` | decoders (`IAudioSourceFactory`) and playlist formats (`IPlaylistFormat`) | Open dialogs, playlists, playback | P4 |
| `IGeneralPlugin` | background features using `IPlayerApi` (scrobblers, loggers, ...) | runs while enabled | P4 |
| `IUiPanelPlugin` | a window with your own Avalonia control | Main menu › Windows | P4 |
| `IOutputPlugin` | new output devices (`IAudioOutput`) | Settings › Output | P6 |
| `IDspPlugin` | effects in the DSP chain (`IDspEffect`) | DSP chain window (EQ › DSP, Settings › Equalizer & DSP) | P7 |
| `IVisualisationPlugin` | spectrum, scope or full-screen visuals | Main menu › Visualisation (Ctrl+Shift+K) | P7 |
| `ICastTargetPlugin` | new kinds of cast devices | device picker | P11 (contract still growing) |

Plugins of types not hosted yet already load, initialise and show in the plugin manager. The player starts using them in the phase listed.

### Writing a decoder (input plugin)

- `IAudioSourceFactory.CanOpen(path)` must be quick and look only at the path (usually the extension). `Open(path)` returns an `IAudioSource`.
- Decoded audio is **interleaved 32-bit float** at the source's own sample rate and channel count.
- `Read(buffer)` returns the number of **frames** written (one sample per channel); 0 means the end.
- `Seek(frame)` should be sample-exact. If it can't be, report the requested position anyway.
- `LengthFrames` is null for live streams.
- "Cannot play this file" is an `AudioSourceException`: the player shows it and skips to the next track. Any **other** exception counts as a plugin fault (see *Faults*).
- All calls on a source come from one decoder thread, never the audio thread. They may block briefly (file I/O). For network input, see `IStreamMetadata` (now-playing titles).

### Offering an encoder (input plugin, SDK 0.4.1)

An input plugin can also offer encoders through `IInputPlugin.EncoderFactories` (default: none). The player uses them to transcode for cast devices (CN-04) and, later, for format conversion (TL-01). The built-in FFmpeg plugin offers FLAC and, when its FFmpeg build has libmp3lame, MP3.

- `IAudioEncoderFactory.Formats` lists what this machine can write (`EncodeFormat.Flac`, `EncodeFormat.Mp3`).
- `Create(settings, format, output)` returns an `IAudioEncoder` writing to `output`, which may be **non-seekable** (a network response): never seek or read it.
- `Write(samples)` takes interleaved float in the format given at creation, in any amount; `Finish()` writes the rest and the end of the stream.
- "Cannot write this format here" is an `AudioSourceException`; an `IOException` from the output means the reader went away (not a fault). Other exceptions are plugin faults.

### Offering a video encoder (input plugin, SDK 0.4.2)

`IInputPlugin.VideoEncoderFactories` (default: none) offers live H.264 video for casting visualisations to a TV (CN-09). The built-in FFmpeg plugin offers it with the operating system's H.264 encoder where FFmpeg has one, else OpenH264.

- `IVideoEncoderFactory.Containers` lists what this machine can write: `VideoContainer.FragmentedMp4` (Chromecast) and `VideoContainer.MpegTs` (DLNA TVs). Empty when there is no H.264 encoder.
- `Create(settings, output)`: `VideoEncodeSettings` gives the container, size (even numbers), frame rate, video bitrate and, for audio and video, the audio format. `output` is a live network response: never seek it.
- `WriteFrame(bgra)` takes premultiplied BGRA frames, spaced 1 / frame rate apart; `WriteAudio(samples)` interleaved float. The player paces both in real time. `Finish()` ends the stream.
- Errors as for audio encoders: `AudioSourceException` = cannot encode here, `IOException` = the TV stopped reading.

### Writing an output (output plugin)

- `IOutputPlugin.Name` is shown in Settings › Output. Each device from your `IAudioOutput.GetDevices()` appears as **"Name: device"**; if you return no devices, a single entry **"Name"** opens your output with `device = null`.
- The player calls `CreateOutput()` once, on first use, and disposes that output before it disposes your plugin (when it is switched off, fails or the player exits). If your output is playing then, the player continues on the system default device.
- `Open(device, format, options, renderer)`: the renderer produces **interleaved 32-bit float** in `format`. Throw `InvalidOperationException` to **refuse** (an unsupported format or exclusive mode): the player falls back without counting a fault. Any other exception counts as a plugin fault (see *Faults*) and the player falls back to the default device.
- `StartOutput()` starts pulling audio: call `renderer.Render(buffer)` from **your own audio thread**, at the pace of your device. Do not allocate, lock or block there, and **catch your own exceptions** on that thread: the player cannot catch them, and an unhandled exception on a thread you own ends the process.
- `StopOutput()` stops pulling (the output stays open); `Close()` releases the device. Raise `OutputLost` if the device stops on its own (the player reopens on the default device) and `DevicesChanged` when your device list changes.
- Optional members: `IsExclusive`, `SampleFormat`, `DeviceSampleRate`, `GetDeviceFormats` (used to choose exclusive formats), `SetDither`.

### Writing an effect (DSP plugin)

`IDspEffect.Process` runs **on the audio thread**. It must not allocate, lock, block, do I/O or throw. Allocate buffers when the effect is created. To receive settings from other threads, use volatile fields (see `samples/GainDsp`).

- Each enabled DSP plugin instance adds one effect to the DSP chain (Settings › Equalizer & DSP › DSP chain), named by `IDspPlugin.Name`. New effects are added **switched off** at the end of the chain; the user switches them on and orders them.
- `CreateEffect(format)` is called for each new output format (interleaved float, usually stereo); the previous effect is disposed first. `Reset()` is called after seeks and new tracks.
- An exception from `Process` or `Reset` bypasses the effect until the next format change and counts as a fault (see *Faults*).
- **Time budget:** an effect may use at most 10 % of real time, measured over each second of audio. Every second over budget counts as a fault, so an effect that stays too slow is switched off after three seconds.
- While DSD is sent as DoP (bit-perfect), the whole chain is bypassed.

### Writing a visualisation

- `IVisualisationPlugin.Name` appears in Main menu › Visualisation; Ctrl+Shift+K opens the first visualisation plugin (or closes the open one).
- The player calls `Create()` and then `CreateView()` on the UI thread; the view must be an Avalonia `Control` and is shown in its own window.
- `OnAudio(samples, format)` is called about 60 times a second on the UI thread with the newest 2,048 stereo frames after the DSP chain (silence while paused). Copy what you need; the span is reused. Exceptions count as faults and close the window.

### Casting a visualisation to a TV (SDK 0.4.2)

The player can send a visualisation to a TV (cast modes *audio and video* and *video only*, CN-09). To take part:

- Return `true` from `IVisualisationPlugin.CanRenderOffscreen`, and implement `IOffscreenVisualisation` on the object `Create()` returns.
- The player creates a **separate instance** for casting (on the UI thread) and never calls `CreateView()` on it, so create your Avalonia control lazily in `CreateView()`.
- On one background thread it then calls `OnAudio(samples, format)` with the audio for the next frame, followed by `RenderFrame(pixels, width, height, time)`, never both at once. Draw the whole frame into `pixels`: premultiplied 32-bit **BGRA**, 4 bytes per pixel, rows top to bottom with no padding (typically 1920 × 1080 at 30 frames a second, so keep a frame well under 30 ms). `time` counts from the start of casting, for animation.
- `samples/PeakMeter` shows both ways: an Avalonia control on screen, plain pixel writes off-screen.

### UI: settings pages, panels, visualisations

- Methods that return UI (`IPluginSettingsPage.CreateSettingsPage`, `IUiPanelPlugin.CreatePanel`, `IVisualisation.CreateView`) return an **Avalonia `Control`**, typed as `object` so the SDK has no Avalonia dependency.
- They are called on the UI thread.
- Reference Avalonia for compiling only; the player provides it:
  ```xml
  <PackageReference Include="Avalonia" Version="12.1.6" ExcludeAssets="runtime;build;buildTransitive" PrivateAssets="all" />
  ```
- Player events arrive on background threads. Post UI updates with `Dispatcher.UIThread.Post(...)`.

## Settings

- `IPluginSettings` stores JSON-serialisable values per plugin, inside the player's settings file:
  - `GetValue(key, default)`;
  - `SetValue(key, value)`;
  - `Remove(key)`;
  - the `Changed` event.
- Values survive restarts. They are removed when the plugin is uninstalled.
- To offer a settings page, implement `IPluginSettingsPage` on any plugin class. The plugin manager then shows **Settings…** for the plugin.

## The player API

`IPlayerApi` gives controlled access to the player:
- state, now playing, position and volume;
- transport: play, pause, stop, next, previous and seek;
- the active playlist, and adding files or URLs to it;
- reading tags.

Each plugin gets its own view of the API:
- **Faults:** exceptions in your event handlers are counted as faults of your plugin (they never reach the player).
- **Unloading:** your handlers are dropped when the plugin is unloaded, even if you forget to unsubscribe. Unsubscribe in `Dispose` anyway.

## Isolation, sharing and dependencies

- Each plugin is loaded into its own **collectible `AssemblyLoadContext`**, so plugins can use different versions of the same library.
- Assemblies are loaded from memory, so the plugin files are not locked and can be updated or uninstalled while the player runs.
- **Shared with the player:**
  - `AudioTastic.Sdk`;
  - Avalonia, SkiaSharp and HarfBuzzSharp;
  - `Microsoft.Extensions.Logging.Abstractions`.

  Do **not** ship these in your plugin folder (the template excludes them). Everything else comes from your folder: set `<CopyLocalLockFileAssemblies>true</CopyLocalLockFileAssemblies>` so NuGet dependencies are copied into it.
- **Native libraries:**
  - Put them in `runtimes/<rid>/native/`. `DllImport`/`LibraryImport` calls are resolved there first.
  - Libraries that depend on each other must be in the same folder.
  - A plugin that needs native code on only some platforms should check availability and return no factories elsewhere (see the OptimFROG plugin).

## Faults

- **Counting:** exceptions thrown by plugin code are caught and logged. Three faults within a minute disable the plugin for the session.
- **What the user sees:** the main window says so, and **Settings › Plugins** shows the error. The user can enable the plugin again there.
- **Load failures:** a plugin that fails in `Initialize` is marked *Failed* and the player carries on.
- **Limits:** in-process loading cannot stop a **native** crash, an access violation or a stack overflow inside a plugin; those end the player. Keep native code small and well tested.

## Packaging and installing

- An `.atplugin` is a zip of the plugin folder, with `plugin.json` at the root.
- **Install:**
  1. The manifest is validated, and paths that would escape the folder are rejected.
  2. The files are extracted to `<data folder>/plugins/<id>/`.
  3. The plugin is loaded straight away.
- **Uninstall:** the folder is deleted. If a native library is still locked, the folder is deleted at the next start.
- **Built-in plugins** can be disabled, not uninstalled.

## Versioning

- The SDK follows semantic versioning, but stays **0.x (pre-release)** until phase P8: DSP, visualisation and crossfading work may still change the contracts.
- **While 0.x:** a plugin must be built for the same minor version (`0.4.x` runs on a 0.4 player).
- **0.4.1** added encoders (`IAudioEncoderFactory`, `IAudioEncoder`, `IInputPlugin.EncoderFactories`). It is additive: plugins built for 0.4.0 keep working unchanged.
- **0.4.2** added off-screen rendering for visualisations (`IOffscreenVisualisation`, `IVisualisationPlugin.CanRenderOffscreen`) and live video encoders (`IVideoEncoderFactory`, `IVideoEncoder`, `IInputPlugin.VideoEncoderFactories`), so visualisations can be cast to a TV (CN-09). Also additive.
- **From 1.0:** a plugin built for `1.y` runs on any player with SDK `1.z` where z ≥ y.
- Incompatible plugins are listed as *Incompatible* and are not loaded.
