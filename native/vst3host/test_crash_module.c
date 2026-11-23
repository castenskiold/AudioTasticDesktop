/* Test fixture (EQ-06): a "VST3 module" that crashes as soon as a host asks for its factory, so the tests can check
 * that a plug-in crashing during the scan is blacklisted without taking the player down. Never shipped. */
#include <stdlib.h>

#ifdef _WIN32
#define EXPORT __declspec(dllexport)
#else
#define EXPORT __attribute__((visibility("default")))
#endif

EXPORT int InitDll(void) { return 1; }
EXPORT int ExitDll(void) { return 1; }
EXPORT int ModuleEntry(void* handle) { (void)handle; return 1; }
EXPORT int ModuleExit(void) { return 1; }
EXPORT int bundleEntry(void* ref) { (void)ref; return 1; }
EXPORT int bundleExit(void) { return 1; }

EXPORT void* GetPluginFactory(void)
{
    abort();
    return 0;
}
