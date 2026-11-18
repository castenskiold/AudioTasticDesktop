/* AudioTastic miniaudio shim (PB-04). See at_miniaudio.h. */
#include "at_miniaudio.h"
#include "miniaudio.h"

#include <stdlib.h>
#include <string.h>

_Static_assert(sizeof(ma_device_id) <= AT_MA_ID_BYTES, "AT_MA_ID_BYTES too small for ma_device_id");
_Static_assert(MA_MAX_DEVICE_NAME_LENGTH + 1 <= AT_MA_NAME_BYTES, "AT_MA_NAME_BYTES too small");

typedef struct at_device {
    ma_device device;
    at_ma_data_proc data_proc;
    at_ma_notify_proc notify_proc;
    void* user;
} at_device;

static void at_data_callback(ma_device* device, void* output, const void* input, ma_uint32 frame_count)
{
    at_device* d = (at_device*)device->pUserData;
    (void)input;
    d->data_proc(d->user, output, frame_count);
}

static ma_format at_to_ma_format(int32_t format)
{
    switch (format) {
    case AT_MA_FORMAT_S16: return ma_format_s16;
    case AT_MA_FORMAT_S24: return ma_format_s24;
    case AT_MA_FORMAT_S32: return ma_format_s32;
    default: return ma_format_f32;
    }
}

static int32_t at_from_ma_format(ma_format format)
{
    switch (format) {
    case ma_format_f32: return AT_MA_FORMAT_F32;
    case ma_format_s16: return AT_MA_FORMAT_S16;
    case ma_format_s24: return AT_MA_FORMAT_S24;
    case ma_format_s32: return AT_MA_FORMAT_S32;
    default: return -1;
    }
}

static void at_notification_callback(const ma_device_notification* notification)
{
    at_device* d = (at_device*)notification->pDevice->pUserData;
    if (d != NULL && d->notify_proc != NULL) {
        d->notify_proc(d->user, (int32_t)notification->type);
    }
}

AT_MA_API int32_t at_ma_api_version(void)
{
    return AT_MA_API_VERSION;
}

AT_MA_API int32_t at_ma_context_create(int32_t use_null_backend, void** out_context)
{
    ma_context* context;
    ma_result result;
    ma_backend null_backend[1] = { ma_backend_null };

    if (out_context == NULL) {
        return MA_INVALID_ARGS;
    }
    *out_context = NULL;
    context = (ma_context*)calloc(1, sizeof(ma_context));
    if (context == NULL) {
        return MA_OUT_OF_MEMORY;
    }
    result = use_null_backend ? ma_context_init(null_backend, 1, NULL, context) : ma_context_init(NULL, 0, NULL, context);
    if (result != MA_SUCCESS) {
        free(context);
        return result;
    }
    *out_context = context;
    return MA_SUCCESS;
}

AT_MA_API void at_ma_context_destroy(void* context)
{
    if (context != NULL) {
        ma_context_uninit((ma_context*)context);
        free(context);
    }
}

AT_MA_API const char* at_ma_context_backend_name(void* context)
{
    return context == NULL ? "" : ma_get_backend_name(((ma_context*)context)->backend);
}

AT_MA_API int32_t at_ma_enumerate_playback(void* context, at_ma_device_info* infos, uint32_t capacity, uint32_t* total)
{
    ma_device_info* playback;
    ma_uint32 count;
    ma_uint32 i;
    ma_result result;

    if (context == NULL || total == NULL) {
        return MA_INVALID_ARGS;
    }
    result = ma_context_get_devices((ma_context*)context, &playback, &count, NULL, NULL);
    if (result != MA_SUCCESS) {
        *total = 0;
        return result;
    }
    *total = count;
    for (i = 0; i < count && i < capacity && infos != NULL; i++) {
        memset(&infos[i], 0, sizeof(at_ma_device_info));
        memcpy(infos[i].id, &playback[i].id, sizeof(ma_device_id));
        strncpy(infos[i].name, playback[i].name, AT_MA_NAME_BYTES - 1);
        infos[i].is_default = playback[i].isDefault ? 1 : 0;
    }
    return MA_SUCCESS;
}

AT_MA_API int32_t at_ma_device_formats(void* context, const uint8_t* device_id, at_ma_native_format* formats,
    uint32_t capacity, uint32_t* total)
{
    ma_device_info info;
    ma_device_id id;
    ma_result result;
    ma_uint32 i;

    if (context == NULL || total == NULL) {
        return MA_INVALID_ARGS;
    }
    *total = 0;
    if (device_id != NULL) {
        memcpy(&id, device_id, sizeof(ma_device_id));
    }
    result = ma_context_get_device_info((ma_context*)context, ma_device_type_playback, device_id != NULL ? &id : NULL, &info);
    if (result != MA_SUCCESS) {
        return result;
    }
    *total = info.nativeDataFormatCount;
    for (i = 0; i < info.nativeDataFormatCount && i < capacity && formats != NULL; i++) {
        formats[i].format = at_from_ma_format(info.nativeDataFormats[i].format);
        formats[i].channels = info.nativeDataFormats[i].channels;
        formats[i].sample_rate = info.nativeDataFormats[i].sampleRate;
        formats[i].exclusive_only = (info.nativeDataFormats[i].flags & MA_DATA_FORMAT_FLAG_EXCLUSIVE_MODE) != 0 ? 1 : 0;
    }
    return MA_SUCCESS;
}

AT_MA_API int32_t at_ma_device_open(void* context, const uint8_t* device_id,
    uint32_t sample_rate, uint32_t channels, int32_t format, int32_t exclusive, uint32_t buffer_ms,
    at_ma_data_proc data_proc, at_ma_notify_proc notify_proc, void* user, void** out_device)
{
    at_device* d;
    ma_device_config config;
    ma_device_id id;
    ma_result result;

    if (context == NULL || data_proc == NULL || out_device == NULL || channels == 0) {
        return MA_INVALID_ARGS;
    }
    *out_device = NULL;
    d = (at_device*)calloc(1, sizeof(at_device));
    if (d == NULL) {
        return MA_OUT_OF_MEMORY;
    }
    d->data_proc = data_proc;
    d->notify_proc = notify_proc;
    d->user = user;

    config = ma_device_config_init(ma_device_type_playback);
    if (device_id != NULL) {
        memcpy(&id, device_id, sizeof(ma_device_id));
        config.playback.pDeviceID = &id;
    }
    config.playback.format = at_to_ma_format(format);
    config.playback.channels = channels;
    config.playback.shareMode = exclusive ? ma_share_mode_exclusive : ma_share_mode_shared;
    config.noClip = MA_TRUE;   /* the managed side clips and converts integer formats itself */
    config.sampleRate = sample_rate;
    /* buffer_ms is the total buffer; miniaudio wants the period size. Two periods per buffer. */
    config.periodSizeInMilliseconds = buffer_ms >= 4 ? buffer_ms / 2 : 2;
    config.periods = 2;
    config.performanceProfile = ma_performance_profile_low_latency;
    config.dataCallback = at_data_callback;
    config.notificationCallback = at_notification_callback;
    config.pUserData = d;

    result = ma_device_init((ma_context*)context, &config, &d->device);
    if (result != MA_SUCCESS) {
        free(d);
        return result;
    }
    *out_device = d;
    return MA_SUCCESS;
}

AT_MA_API int32_t at_ma_device_start(void* device)
{
    return device == NULL ? MA_INVALID_ARGS : ma_device_start(&((at_device*)device)->device);
}

AT_MA_API int32_t at_ma_device_stop(void* device)
{
    return device == NULL ? MA_INVALID_ARGS : ma_device_stop(&((at_device*)device)->device);
}

AT_MA_API void at_ma_device_close(void* device)
{
    if (device != NULL) {
        ma_device_uninit(&((at_device*)device)->device);
        free(device);
    }
}

AT_MA_API uint32_t at_ma_device_internal_sample_rate(void* device)
{
    return device == NULL ? 0 : ((at_device*)device)->device.playback.internalSampleRate;
}

AT_MA_API int32_t at_ma_device_internal_format(void* device)
{
    return device == NULL ? -1 : at_from_ma_format(((at_device*)device)->device.playback.internalFormat);
}

AT_MA_API uint32_t at_ma_device_period_frames(void* device)
{
    return device == NULL ? 0 : ((at_device*)device)->device.playback.internalPeriodSizeInFrames;
}

AT_MA_API int32_t at_ma_device_name(void* device, char* buffer, uint32_t buffer_bytes)
{
    ma_device_info info;
    ma_result result;

    if (device == NULL || buffer == NULL || buffer_bytes == 0) {
        return MA_INVALID_ARGS;
    }
    result = ma_device_get_info(&((at_device*)device)->device, ma_device_type_playback, &info);
    if (result != MA_SUCCESS) {
        buffer[0] = '\0';
        return result;
    }
    strncpy(buffer, info.name, buffer_bytes - 1);
    buffer[buffer_bytes - 1] = '\0';
    return MA_SUCCESS;
}
