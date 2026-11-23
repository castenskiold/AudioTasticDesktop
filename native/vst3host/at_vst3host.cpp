// AudioTastic VST3 host shim (EQ-06). See at_vst3host.h for the API and its threading rules.
// Built on the VST3 SDK's hosting classes (Module, PlugProvider, HostProcessData, ParameterChanges; MIT).

#include "at_vst3host.h"

#include "pluginterfaces/gui/iplugview.h"
#include "pluginterfaces/vst/ivstaudioprocessor.h"
#include "pluginterfaces/vst/ivstcomponent.h"
#include "pluginterfaces/vst/ivsteditcontroller.h"
#include "pluginterfaces/vst/vstspeaker.h"
#include "public.sdk/source/common/memorystream.h"
#include "public.sdk/source/vst/hosting/hostclasses.h"
#include "public.sdk/source/vst/hosting/module.h"
#include "public.sdk/source/vst/hosting/parameterchanges.h"
#include "public.sdk/source/vst/hosting/plugprovider.h"
#include "public.sdk/source/vst/hosting/processdata.h"
#include "public.sdk/source/vst/utility/stringconvert.h"

#include <algorithm>
#include <cstring>
#include <memory>
#include <mutex>
#include <string>
#include <utility>
#include <vector>

using namespace Steinberg;
using namespace Steinberg::Vst;

namespace {

#if defined(_WIN32)
const FIDString PlatformType = kPlatformTypeHWND;
#elif defined(__APPLE__)
const FIDString PlatformType = kPlatformTypeNSView;
#else
const FIDString PlatformType = kPlatformTypeX11EmbedWindowID;
#endif

// One host context for every plugin (IHostApplication), set once.
HostApplication& Host()
{
    static HostApplication* host = [] {
        auto* h = new HostApplication();
        PluginContextFactory::instance().setPluginContext(h);
        return h;
    }();
    return *host;
}

void CopyText(const std::string& text, char* buffer, int32_t capacity)
{
    if (buffer == nullptr || capacity <= 0)
        return;
    auto n = std::min<size_t>(text.size(), static_cast<size_t>(capacity - 1));
    std::memcpy(buffer, text.data(), n);
    buffer[n] = 0;
}

void AppendJson(std::string& out, const std::string& text)
{
    out += '"';
    for (unsigned char c : text)
    {
        switch (c)
        {
            case '"': out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20)
                {
                    char hex[8];
                    snprintf(hex, sizeof hex, "\\u%04x", c);
                    out += hex;
                }
                else
                {
                    out += static_cast<char>(c);
                }
        }
    }
    out += '"';
}

// Collects the editor's parameter edits (UI thread) for the next process call (audio thread).
class ComponentHandler : public IComponentHandler
{
public:
    tresult PLUGIN_API beginEdit(ParamID) override { return kResultOk; }
    tresult PLUGIN_API endEdit(ParamID) override { return kResultOk; }
    tresult PLUGIN_API restartComponent(int32) override { return kResultOk; }

    tresult PLUGIN_API performEdit(ParamID id, ParamValue value) override
    {
        std::lock_guard<std::mutex> lock(mutex);
        for (auto& change : pending)
        {
            if (change.first == id)
            {
                change.second = value;
                return kResultOk;
            }
        }
        if (pending.size() < pending.capacity())
            pending.emplace_back(id, value);
        return kResultOk;
    }

    // Audio thread: moves pending edits into changes without waiting (a busy lock waits for the next block).
    void Drain(ParameterChanges& changes)
    {
        std::unique_lock<std::mutex> lock(mutex, std::try_to_lock);
        if (!lock.owns_lock())
            return;
        for (auto& change : pending)
        {
            int32 index = 0;
            if (auto* queue = changes.addParameterData(change.first, index))
            {
                int32 point = 0;
                queue->addPoint(0, change.second, point);
            }
        }
        pending.clear();
    }

    std::mutex mutex;
    std::vector<std::pair<ParamID, ParamValue>> pending;

    // Not reference counted: owned by the plugin record, which outlives the controller's use of it.
    tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override
    {
        if (FUnknownPrivate::iidEqual(iid, IComponentHandler::iid) || FUnknownPrivate::iidEqual(iid, FUnknown::iid))
        {
            *obj = static_cast<IComponentHandler*>(this);
            return kResultOk;
        }
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef() override { return 1000; }
    uint32 PLUGIN_API release() override { return 1000; }
};

// The editor's frame: passes size requests on to the .NET window.
class PlugFrame : public IPlugFrame
{
public:
    tresult PLUGIN_API resizeView(IPlugView* view, ViewRect* size) override
    {
        if (view == nullptr || size == nullptr)
            return kInvalidArgument;
        if (callback != nullptr)
            callback(user, size->getWidth(), size->getHeight());
        view->onSize(size);
        return kResultTrue;
    }

    tresult PLUGIN_API queryInterface(const TUID iid, void** obj) override
    {
        if (FUnknownPrivate::iidEqual(iid, IPlugFrame::iid) || FUnknownPrivate::iidEqual(iid, FUnknown::iid))
        {
            *obj = static_cast<IPlugFrame*>(this);
            return kResultOk;
        }
        *obj = nullptr;
        return kNoInterface;
    }
    uint32 PLUGIN_API addRef() override { return 1000; }
    uint32 PLUGIN_API release() override { return 1000; }

    at_vst3_resize_callback callback = nullptr;
    void* user = nullptr;
};

} // namespace

struct at_vst3_plugin
{
    VST3::Hosting::Module::Ptr module;
    IPtr<PlugProvider> provider;
    IPtr<IComponent> component;
    IPtr<IAudioProcessor> processor;
    IPtr<IEditController> controller;
    IPtr<IPlugView> view;
    ComponentHandler handler;
    PlugFrame frame;
    HostProcessData data;
    ParameterChanges inputChanges;
    int32_t maxFrames = 0;
    int32_t inChannels = 0;
    int32_t outChannels = 0;
    bool active = false;

    void Deactivate()
    {
        if (!active)
            return;
        processor->setProcessing(false);
        component->setActive(false);
        data.unprepare();
        active = false;
    }
};

extern "C" {

AT_VST3_API int32_t at_vst3_api_version(void) { return AT_VST3_API_VERSION; }

AT_VST3_API int32_t at_vst3_scan(const char* path, char* json, int32_t capacity)
{
    if (path == nullptr)
        return AT_VST3_E_ARGUMENT;
    Host();
    std::string error;
    auto module = VST3::Hosting::Module::create(path, error);
    if (!module)
    {
        CopyText(error.empty() ? "Not a VST3 module" : error, json, capacity);
        return AT_VST3_E_FAILED;
    }
    std::string out = "{\"classes\":[";
    bool first = true;
    for (auto& info : module->getFactory().classInfos())
    {
        if (info.category() != kVstAudioEffectClass)
            continue;
        if (!first)
            out += ',';
        first = false;
        out += "{\"id\":";
        AppendJson(out, info.ID().toString(false));
        out += ",\"name\":";
        AppendJson(out, info.name());
        out += ",\"vendor\":";
        AppendJson(out, info.vendor());
        out += ",\"version\":";
        AppendJson(out, info.version());
        out += ",\"subCategories\":";
        AppendJson(out, info.subCategoriesString());
        out += '}';
    }
    out += "]}";
    if (json != nullptr && static_cast<int32_t>(out.size()) < capacity)
        CopyText(out, json, capacity);
    return static_cast<int32_t>(out.size());
}

AT_VST3_API at_vst3_plugin* at_vst3_open(const char* path, const char* class_id, char* error, int32_t error_capacity)
{
    if (path == nullptr || class_id == nullptr)
    {
        CopyText("Missing path or class id", error, error_capacity);
        return nullptr;
    }
    Host();
    std::string message;
    auto module = VST3::Hosting::Module::create(path, message);
    if (!module)
    {
        CopyText(message.empty() ? "Not a VST3 module" : message, error, error_capacity);
        return nullptr;
    }
    auto uid = VST3::UID::fromString(std::string(class_id), false);
    if (!uid)
    {
        CopyText("Bad class id", error, error_capacity);
        return nullptr;
    }
    auto& factory = module->getFactory();
    for (auto& info : factory.classInfos())
    {
        if (info.ID() != *uid)
            continue;
        auto plugin = std::make_unique<at_vst3_plugin>();
        plugin->module = module;
        plugin->provider = owned(new PlugProvider(factory, info, true));
        if (!plugin->provider->initialize())
        {
            CopyText("The plug-in could not be initialised", error, error_capacity);
            return nullptr;
        }
        plugin->component = plugin->provider->getComponentPtr();
        plugin->controller = plugin->provider->getControllerPtr();
        plugin->processor = FUnknownPtr<IAudioProcessor>(plugin->component);
        if (!plugin->processor)
        {
            CopyText("The plug-in has no audio processor", error, error_capacity);
            return nullptr;
        }
        if (plugin->processor->canProcessSampleSize(kSample32) != kResultTrue)
        {
            CopyText("The plug-in cannot process 32-bit float", error, error_capacity);
            return nullptr;
        }
        if (plugin->controller)
            plugin->controller->setComponentHandler(&plugin->handler);
        plugin->handler.pending.reserve(512);
        plugin->inputChanges.setMaxParameters(512);
        return plugin.release();
    }
    CopyText("The class was not found in the module", error, error_capacity);
    return nullptr;
}

AT_VST3_API int32_t at_vst3_setup(at_vst3_plugin* plugin, double sample_rate, int32_t max_frames)
{
    if (plugin == nullptr || sample_rate <= 0 || max_frames <= 0)
        return AT_VST3_E_ARGUMENT;
    plugin->Deactivate();

    // Stereo main buses; a plug-in that refuses keeps its own arrangement (mono is mapped below).
    SpeakerArrangement stereo = SpeakerArr::kStereo;
    SpeakerArrangement input = stereo, output = stereo;
    auto inputs = plugin->component->getBusCount(kAudio, kInput);
    auto outputs = plugin->component->getBusCount(kAudio, kOutput);
    if (outputs < 1)
        return AT_VST3_E_NOT_SUPPORTED;
    std::vector<SpeakerArrangement> ins(static_cast<size_t>(inputs), stereo), outs(static_cast<size_t>(outputs), stereo);
    if (plugin->processor->setBusArrangements(ins.data(), inputs, outs.data(), outputs) != kResultTrue)
    {
        if (inputs > 0)
            plugin->processor->getBusArrangement(kInput, 0, input);
        plugin->processor->getBusArrangement(kOutput, 0, output);
    }
    plugin->inChannels = inputs > 0 ? SpeakerArr::getChannelCount(input) : 0;
    plugin->outChannels = SpeakerArr::getChannelCount(output);
    if (plugin->outChannels < 1 || plugin->outChannels > 2 || plugin->inChannels > 2)
        return AT_VST3_E_NOT_SUPPORTED;

    for (int32 i = 0; i < inputs; ++i)
        plugin->component->activateBus(kAudio, kInput, i, i == 0);
    for (int32 i = 0; i < outputs; ++i)
        plugin->component->activateBus(kAudio, kOutput, i, i == 0);

    ProcessSetup setup {kRealtime, kSample32, max_frames, sample_rate};
    if (plugin->processor->setupProcessing(setup) != kResultTrue)
        return AT_VST3_E_FAILED;
    // HostProcessData owns the buffers of every bus (it frees them in unprepare): the audio is copied into and out of
    // the main buses' buffers, never replaced by ours.
    if (!plugin->data.prepare(*plugin->component, max_frames, kSample32))
        return AT_VST3_E_FAILED;
    plugin->maxFrames = max_frames;
    if (plugin->component->setActive(true) != kResultTrue)
        return AT_VST3_E_FAILED;
    plugin->processor->setProcessing(true);
    plugin->active = true;
    return 0;
}

AT_VST3_API int32_t at_vst3_process(at_vst3_plugin* plugin, float* interleaved, int32_t frames)
{
    if (plugin == nullptr || interleaved == nullptr || !plugin->active || frames < 0 || frames > plugin->maxFrames)
        return AT_VST3_E_ARGUMENT;
    if (frames == 0)
        return 0;
    auto& data = plugin->data;
    if (plugin->inChannels > 0 && data.numInputs > 0)
    {
        float* inL = data.inputs[0].channelBuffers32[0];
        if (plugin->inChannels == 1)
        {
            for (int32_t i = 0; i < frames; ++i)
                inL[i] = 0.5f * (interleaved[2 * i] + interleaved[2 * i + 1]);
        }
        else
        {
            float* inR = data.inputs[0].channelBuffers32[1];
            for (int32_t i = 0; i < frames; ++i)
            {
                inL[i] = interleaved[2 * i];
                inR[i] = interleaved[2 * i + 1];
            }
        }
    }

    plugin->inputChanges.clearQueue();
    plugin->handler.Drain(plugin->inputChanges);
    plugin->data.inputParameterChanges = &plugin->inputChanges;
    plugin->data.numSamples = frames;
    if (plugin->processor->process(plugin->data) != kResultOk)
        return AT_VST3_E_FAILED;

    float* outL = data.outputs[0].channelBuffers32[0];
    float* outR = plugin->outChannels > 1 ? data.outputs[0].channelBuffers32[1] : outL;
    for (int32_t i = 0; i < frames; ++i)
    {
        interleaved[2 * i] = outL[i];
        interleaved[2 * i + 1] = outR[i];
    }
    return 0;
}

AT_VST3_API int32_t at_vst3_latency(at_vst3_plugin* plugin)
{
    return plugin == nullptr ? 0 : static_cast<int32_t>(plugin->processor->getLatencySamples());
}

// State layout: [u32 component length][component state][u32 controller length][controller state].
AT_VST3_API int32_t at_vst3_get_state(at_vst3_plugin* plugin, uint8_t* buffer, int32_t capacity)
{
    if (plugin == nullptr)
        return AT_VST3_E_ARGUMENT;
    MemoryStream component, controller;
    if (plugin->component->getState(&component) != kResultOk)
        return AT_VST3_E_FAILED;
    if (plugin->controller)
        plugin->controller->getState(&controller); // optional
    int64 componentSize = 0, controllerSize = 0;
    component.tell(&componentSize);
    controller.tell(&controllerSize);
    auto total = 8 + componentSize + controllerSize;
    if (total > INT32_MAX)
        return AT_VST3_E_FAILED;
    if (buffer == nullptr || total > capacity)
        return static_cast<int32_t>(total);
    auto put = [&](int64 size, const char* bytes, uint8_t*& at) {
        auto n = static_cast<uint32_t>(size);
        std::memcpy(at, &n, 4);
        at += 4;
        if (size > 0)
            std::memcpy(at, bytes, static_cast<size_t>(size));
        at += size;
    };
    auto* at = buffer;
    put(componentSize, component.getData(), at);
    put(controllerSize, controller.getData(), at);
    return static_cast<int32_t>(total);
}

AT_VST3_API int32_t at_vst3_set_state(at_vst3_plugin* plugin, const uint8_t* buffer, int32_t length)
{
    if (plugin == nullptr || buffer == nullptr || length < 8)
        return AT_VST3_E_ARGUMENT;
    uint32_t componentSize = 0, controllerSize = 0;
    std::memcpy(&componentSize, buffer, 4);
    if (8ull + componentSize > static_cast<uint64_t>(length))
        return AT_VST3_E_ARGUMENT;
    std::memcpy(&controllerSize, buffer + 4 + componentSize, 4);
    if (8ull + componentSize + controllerSize != static_cast<uint64_t>(length))
        return AT_VST3_E_ARGUMENT;
    auto* componentBytes = const_cast<uint8_t*>(buffer + 4);
    auto* controllerBytes = const_cast<uint8_t*>(buffer + 8 + componentSize);
    MemoryStream component(componentBytes, componentSize);
    if (plugin->component->setState(&component) != kResultOk)
        return AT_VST3_E_FAILED;
    if (plugin->controller)
    {
        MemoryStream again(componentBytes, componentSize);
        plugin->controller->setComponentState(&again);
        if (controllerSize > 0)
        {
            MemoryStream controller(controllerBytes, controllerSize);
            plugin->controller->setState(&controller);
        }
    }
    return 0;
}

AT_VST3_API int32_t at_vst3_has_editor(at_vst3_plugin* plugin)
{
    if (plugin == nullptr || !plugin->controller)
        return 0;
    if (plugin->view)
        return 1;
    auto view = owned(plugin->controller->createView(ViewType::kEditor));
    return view && view->isPlatformTypeSupported(PlatformType) == kResultTrue ? 1 : 0;
}

AT_VST3_API int32_t at_vst3_editor_open(at_vst3_plugin* plugin, void* parent, int32_t* width, int32_t* height,
                                        at_vst3_resize_callback resize, void* user)
{
    if (plugin == nullptr || parent == nullptr || !plugin->controller)
        return AT_VST3_E_ARGUMENT;
    at_vst3_editor_close(plugin);
    auto view = owned(plugin->controller->createView(ViewType::kEditor));
    if (!view || view->isPlatformTypeSupported(PlatformType) != kResultTrue)
        return AT_VST3_E_NOT_SUPPORTED;
    plugin->frame.callback = resize;
    plugin->frame.user = user;
    view->setFrame(&plugin->frame);
    if (view->attached(parent, PlatformType) != kResultTrue)
    {
        view->setFrame(nullptr);
        return AT_VST3_E_FAILED;
    }
    ViewRect rect {};
    view->getSize(&rect);
    if (width != nullptr)
        *width = rect.getWidth();
    if (height != nullptr)
        *height = rect.getHeight();
    plugin->view = view;
    return 0;
}

AT_VST3_API void at_vst3_editor_close(at_vst3_plugin* plugin)
{
    if (plugin == nullptr || !plugin->view)
        return;
    plugin->view->removed();
    plugin->view->setFrame(nullptr);
    plugin->view = nullptr;
    plugin->frame.callback = nullptr;
}

AT_VST3_API void at_vst3_close(at_vst3_plugin* plugin)
{
    if (plugin == nullptr)
        return;
    at_vst3_editor_close(plugin);
    plugin->Deactivate();
    if (plugin->controller)
        plugin->controller->setComponentHandler(nullptr);
    plugin->processor = nullptr;
    plugin->controller = nullptr;
    plugin->component = nullptr;
    plugin->provider = nullptr; // terminates and disconnects component and controller
    plugin->module = nullptr;
    delete plugin;
}

} // extern "C"
