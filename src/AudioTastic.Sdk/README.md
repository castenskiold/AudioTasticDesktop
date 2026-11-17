# AudioTastic.Sdk

Contracts for AudioTastic Desktop plugins (PG-02). The package holds only interfaces, data types and small helpers. It references no AudioTastic internals, and its only dependency is Microsoft.Extensions.Logging.Abstractions.

**Status:** pre-release (0.x). Contracts may still change until the DSP chain and crossfading are built. A plugin must be built against the same 0.minor version as the player (`SdkInfo.IsCompatible`).

## What is in it

| Namespace | Contents |
|---|---|
| `AudioTastic.Sdk.Plugins` | The seven plugin types: `IInputPlugin`, `IOutputPlugin`, `IDspPlugin` (+ `IDspEffect`), `IVisualisationPlugin` (+ `IVisualisation`), `IGeneralPlugin`, `IUiPanelPlugin`, `ICastTargetPlugin`. Also `IPlugin`, `IPluginContext`, `IPluginSettings`, `IPluginSettingsPage`, `IPlayerApi` and `PluginManifest` (plugin.json), plus `SdkInfo`. |
| `AudioTastic.Sdk.Audio` | `IAudioSource` (decoded float audio, sample-accurate seek), `IAudioSourceFactory`, `IStreamMetadata`, `IAudioOutput`, `AudioFormat`, `AudioStreamInfo`. |
| `AudioTastic.Sdk.Playlists` | `PlaylistItem`, `IPlaylistFormat`. |
| `AudioTastic.Sdk.Metadata` | `TrackTags`. |
| `AudioTastic.Sdk.Interop` | `NativeLibraries`, for plugins that ship native libraries. |

## Writing a plugin

The plugin author guide in the AudioTastic repository (`docs/plugins/plugin-guide.md`) covers:
- the manifest;
- packaging as `.atplugin`;
- threading rules;
- the `dotnet new audiotastic-plugin` template.

Samples are in `samples/`.
