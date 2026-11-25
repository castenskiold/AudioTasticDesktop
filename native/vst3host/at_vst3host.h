/* AudioTastic VST3 host shim (EQ-06): a flat C API over the Steinberg VST3 SDK's hosting classes (MIT).
 * The .NET side (AudioTastic.Core/Audio/Dsp/Vst3Native.cs) P/Invokes only this API. Bump AT_VST3_API_VERSION and
 * Vst3Native.ApiVersionExpected together when it changes.
 *
 * Threads: at_vst3_process runs on the audio thread (no allocation, no blocking: editor parameter changes are
 * handed over with a try-lock). Everything else runs on a control or UI thread, never at the same time as process
 * for the same plugin except at_vst3_get_state (the plugin's own locking applies, as in every host).
 * Strings are UTF-8. Return codes: 0 = ok, negative = error (AT_VST3_E_*).
 */
#ifndef AT_VST3HOST_H
#define AT_VST3HOST_H

#include <stdint.h>

#ifdef _WIN32
#define AT_VST3_API __declspec(dllexport)
#else
#define AT_VST3_API __attribute__((visibility("default")))
#endif

#define AT_VST3_API_VERSION 2

#define AT_VST3_E_FAILED -1
#define AT_VST3_E_ARGUMENT -2
#define AT_VST3_E_NOT_SUPPORTED -3
#define AT_VST3_E_BUFFER_TOO_SMALL -4

#ifdef __cplusplus
extern "C" {
#endif

typedef struct at_vst3_plugin at_vst3_plugin;

/* Called on the UI thread when the plugin's editor asks for a new size (pixels). */
typedef void (*at_vst3_resize_callback)(void* user, int32_t width, int32_t height);

AT_VST3_API int32_t at_vst3_api_version(void);

/* Lists the audio effect classes of the module at path as JSON:
 * {"classes":[{"id":"<32 hex>","name":"…","vendor":"…","version":"…","subCategories":"Fx|EQ"}]}
 * Returns the JSON length in bytes (without the terminating zero); if that is >= capacity nothing useful was
 * written: call again with a bigger buffer. Negative on error (the message goes to json when it fits). */
AT_VST3_API int32_t at_vst3_scan(const char* path, char* json, int32_t capacity);

/* Loads class_id (32 hex characters) from the module at path. Returns null on failure, with a message in error. */
AT_VST3_API at_vst3_plugin* at_vst3_open(const char* path, const char* class_id, char* error, int32_t error_capacity);

/* Sets up stereo processing at sample_rate with blocks of at most max_frames, and activates the plugin. Call again
 * to change the rate (the plugin is deactivated first). */
AT_VST3_API int32_t at_vst3_setup(at_vst3_plugin* plugin, double sample_rate, int32_t max_frames);

/* Processes frames of interleaved stereo float in place (frames <= max_frames). */
AT_VST3_API int32_t at_vst3_process(at_vst3_plugin* plugin, float* interleaved, int32_t frames);

/* The plugin's processing latency in frames. */
AT_VST3_API int32_t at_vst3_latency(at_vst3_plugin* plugin);

/* API v2. UI thread: hands the output parameter values the processor reported (meters, gain reduction) to the edit
 * controller, so the editor shows them. The audio thread queues them without locks; values beyond the queue's room
 * are dropped until the next flush. Returns the number of values handed over. */
AT_VST3_API int32_t at_vst3_flush_output(at_vst3_plugin* plugin);

/* API v2. The IComponentHandler::restartComponent flags the plugin raised since the last call (e.g. 8 =
 * kLatencyChanged), cleared by the call. */
AT_VST3_API int32_t at_vst3_take_restart_flags(at_vst3_plugin* plugin);

/* The plugin's state (component + controller). Returns the size in bytes; if > capacity nothing was written. */
AT_VST3_API int32_t at_vst3_get_state(at_vst3_plugin* plugin, uint8_t* buffer, int32_t capacity);
AT_VST3_API int32_t at_vst3_set_state(at_vst3_plugin* plugin, const uint8_t* buffer, int32_t length);

/* 1 when the plugin has its own editor that can live in this platform's windows. */
AT_VST3_API int32_t at_vst3_has_editor(at_vst3_plugin* plugin);

/* Opens the editor inside parent (HWND on Windows, NSView* on macOS, X11 window id on Linux); width/height get its
 * size. resize is called when the editor wants another size. */
AT_VST3_API int32_t at_vst3_editor_open(at_vst3_plugin* plugin, void* parent, int32_t* width, int32_t* height,
                                        at_vst3_resize_callback resize, void* user);
AT_VST3_API void at_vst3_editor_close(at_vst3_plugin* plugin);

/* Deactivates and releases the plugin (closes the editor first). */
AT_VST3_API void at_vst3_close(at_vst3_plugin* plugin);

#ifdef __cplusplus
}
#endif

#endif
