/*
 * AudioTastic miniaudio shim (PB-04, PB-05).
 *
 * A small, flat C API over miniaudio so the .NET side never has to mirror miniaudio's large,
 * platform-dependent structs (ma_context, ma_device, ma_device_config). Handles are opaque pointers;
 * all structs below have a fixed layout on every platform.
 *
 * Version 2 (P6): shared or exclusive mode, and the output sample format (f32, s16, s24 packed, s32). For integer
 * formats the managed side converts the samples itself (exactly inverse to its decoding, with optional dither), so
 * the device receives them unchanged: miniaudio does no conversion when the requested format is the device's.
 * Bump AT_MA_API_VERSION when anything here changes; the managed side checks it at start-up.
 */
#ifndef AT_MINIAUDIO_H
#define AT_MINIAUDIO_H

#include <stdint.h>

#ifdef _WIN32
#define AT_MA_API __declspec(dllexport)
#else
#define AT_MA_API __attribute__((visibility("default")))
#endif

#define AT_MA_API_VERSION 2
#define AT_MA_NAME_BYTES 256
#define AT_MA_ID_BYTES 512
#define AT_MA_MAX_FORMATS 64

#ifdef __cplusplus
extern "C" {
#endif

/* Output sample formats. */
enum {
    AT_MA_FORMAT_F32 = 0,
    AT_MA_FORMAT_S16 = 1,
    AT_MA_FORMAT_S24 = 2, /* packed, 3 bytes per sample, little-endian */
    AT_MA_FORMAT_S32 = 3
};

typedef struct at_ma_device_info {
    uint8_t id[AT_MA_ID_BYTES];     /* opaque ma_device_id, pass back to at_ma_device_open */
    char name[AT_MA_NAME_BYTES];    /* UTF-8, zero-terminated */
    int32_t is_default;
} at_ma_device_info;

/* One native data format of a device. sample_rate 0 / channels 0 mean "any". */
typedef struct at_ma_native_format {
    int32_t format;      /* AT_MA_FORMAT_*, or -1 for formats the shim does not use */
    uint32_t channels;
    uint32_t sample_rate;
    int32_t exclusive_only;
} at_ma_native_format;

/* Notification types, same values as ma_device_notification_type. */
enum {
    AT_MA_NOTIFY_STARTED = 0,
    AT_MA_NOTIFY_STOPPED = 1,
    AT_MA_NOTIFY_REROUTED = 2,
    AT_MA_NOTIFY_INTERRUPTION_BEGAN = 3,
    AT_MA_NOTIFY_INTERRUPTION_ENDED = 4,
    AT_MA_NOTIFY_UNLOCKED = 5
};

/* Called on the audio thread: fill frame_count frames in the device's sample format (interleaved). */
typedef void (*at_ma_data_proc)(void* user, void* output, uint32_t frame_count);
/* Called on a miniaudio thread when the device state changes. */
typedef void (*at_ma_notify_proc)(void* user, int32_t notification);

AT_MA_API int32_t at_ma_api_version(void);

/* Returns a ma_result (0 = success). use_null_backend != 0 selects miniaudio's null backend (tests, CI). */
AT_MA_API int32_t at_ma_context_create(int32_t use_null_backend, void** out_context);
AT_MA_API void at_ma_context_destroy(void* context);
/* Name of the backend in use, e.g. "WASAPI". */
AT_MA_API const char* at_ma_context_backend_name(void* context);

/* Copies up to capacity playback devices into infos; *total receives the number available. */
AT_MA_API int32_t at_ma_enumerate_playback(void* context, at_ma_device_info* infos, uint32_t capacity, uint32_t* total);

/* PB-05: the device's native formats (device_id NULL = default device). Copies up to capacity; *total = available. */
AT_MA_API int32_t at_ma_device_formats(void* context, const uint8_t* device_id, at_ma_native_format* formats,
    uint32_t capacity, uint32_t* total);

/*
 * device_id: NULL for the system default device (follows default-device changes where the backend allows).
 * format: AT_MA_FORMAT_*. exclusive != 0 requests exclusive mode (fails with MA_SHARE_MODE_NOT_SUPPORTED or a
 * format error when the device cannot do it).
 */
AT_MA_API int32_t at_ma_device_open(void* context, const uint8_t* device_id,
    uint32_t sample_rate, uint32_t channels, int32_t format, int32_t exclusive, uint32_t buffer_ms,
    at_ma_data_proc data_proc, at_ma_notify_proc notify_proc, void* user, void** out_device);
AT_MA_API int32_t at_ma_device_start(void* device);
AT_MA_API int32_t at_ma_device_stop(void* device);
AT_MA_API void at_ma_device_close(void* device);
/* Sample rate of the hardware side (may differ from the requested rate in shared mode). */
AT_MA_API uint32_t at_ma_device_internal_sample_rate(void* device);
/* Sample format of the hardware side (AT_MA_FORMAT_*, -1 for others). */
AT_MA_API int32_t at_ma_device_internal_format(void* device);
/* Period size in frames actually used by the device. */
AT_MA_API uint32_t at_ma_device_period_frames(void* device);
AT_MA_API int32_t at_ma_device_name(void* device, char* buffer, uint32_t buffer_bytes);

#ifdef __cplusplus
}
#endif

#endif
