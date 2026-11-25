# AudioTastic.Sdk

Contracts for AudioTastic Desktop plugins (PG-02). The package holds only interfaces, data types and small helpers. It references no AudioTastic internals, and its only dependency is Microsoft.Extensions.Logging.Abstractions.

**Status:** 1.0 (stable). A plugin built for SDK 1.y runs on every player with SDK 1.z where z ≥ y; new features arrive as additive minor versions (default interface methods, new optional types), never by changing what is there. Plugins built for the 0.4 previews also run on 1.x (`SdkInfo.IsCompatible`): 1.0.0 has exactly the contracts of 0.4.3.

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

## Licence

MIT. Plugins built against the SDK may use any licence, free or commercial.
