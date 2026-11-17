# Built-in plugins

Built-in decoders, visualisations, DSP effects and cast targets are written as plugins against `AudioTastic.Sdk`, so the SDK is tested through real use.

| Plugin | Type | Since | Notes |
|---|---|---|---|
| `AudioTastic.Input.Ffmpeg` | Input/decoder | P1 (FM-01) | FFmpeg via FFmpeg.AutoGen 8.1. Referenced directly by the app until the plugin loader (PG-01) arrives in P4. |
