# AudioTastic.Sdk

Public plugin contracts (PG-02). No reference to any other AudioTastic project; only interfaces, data types and small helpers.

- `Audio/`: `IAudioSource` (decoded stream: read float frames, sample-accurate seek), `IAudioSourceFactory` (input plugin), `AudioFormat`, `AudioStreamInfo`, `AudioSourceException`. Added in P1.
- `Interop/NativeLibraries`: finds native libraries next to the app or in `runtimes/<rid>/native`; plugins with native code use it too.

The SDK is formalised in P4 (versioning, NuGet package, `plugin.json`, templates, samples).
