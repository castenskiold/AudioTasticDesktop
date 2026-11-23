# Builds the small native libraries AudioTastic needs on every platform (phase P0 pipeline):
#   - miniaudio (public domain / MIT-0): audio output (PB-04), used from P1
#   - SoundTouch (LGPL-2.1, shared library + C wrapper): time-stretch (PB-06, XF-02), used from P6
#   - the VST3 host shim (EQ-06, P12): at_vst3host (C API over the Steinberg VST3 SDK, MIT) and the at_vst3scan scanner
# libopenmpt (FM-07) is fetched/built by its own scripts (P4). ASIO is optional and deferred.
#
#   cmake -S native -B native/build/<rid> -DCMAKE_BUILD_TYPE=Release -DAT_RID=<rid>
#   cmake --build native/build/<rid> --config Release
#   cmake --install native/build/<rid> --config Release
cmake_minimum_required(VERSION 3.24)
# VERSION is required: SoundTouch's CMakeLists reads CMAKE_PROJECT_VERSION (the top-level project's version).
project(AudioTasticNative VERSION 0.1.0 LANGUAGES C CXX)

set(AT_RID "" CACHE STRING "Runtime identifier, e.g. win-x64 (output goes to native/out/<rid>/native)")
if(AT_RID STREQUAL "")
  message(FATAL_ERROR "Set -DAT_RID=<rid>")
endif()
set(CMAKE_INSTALL_PREFIX "${CMAKE_CURRENT_SOURCE_DIR}/out/${AT_RID}" CACHE PATH "" FORCE)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
# The installed libraries find each other in their own folder (libSoundTouchDLL needs libSoundTouch): without this,
# Linux and macOS refuse to load the wrapper even though its dependency sits next to it (seen in CI, 19 Nov 2026).
if(APPLE)
  set(CMAKE_INSTALL_NAME_DIR "@rpath")
  set(CMAKE_INSTALL_RPATH "@loader_path")
elseif(UNIX)
  set(CMAKE_INSTALL_RPATH "$ORIGIN")
endif()

include(FetchContent)

# --- miniaudio --------------------------------------------------------------
set(AT_MINIAUDIO_TAG "0.11.26" CACHE STRING "miniaudio git tag")
FetchContent_Declare(miniaudio_src
  GIT_REPOSITORY https://github.com/mackron/miniaudio.git
  GIT_TAG ${AT_MINIAUDIO_TAG}
  GIT_SHALLOW TRUE
  SOURCE_SUBDIR no-cmake-here)  # download sources only; our own target is defined below
FetchContent_MakeAvailable(miniaudio_src)
# miniaudio plus our flat C shim (miniaudio/at_miniaudio.c) that the .NET side P/Invokes (PB-04).
add_library(miniaudio SHARED "${miniaudio_src_SOURCE_DIR}/miniaudio.c" "${CMAKE_CURRENT_SOURCE_DIR}/miniaudio/at_miniaudio.c")
target_include_directories(miniaudio PUBLIC "${miniaudio_src_SOURCE_DIR}" "${CMAKE_CURRENT_SOURCE_DIR}/miniaudio")
target_compile_definitions(miniaudio PRIVATE MA_DLL MA_NO_ENCODING MA_NO_GENERATION MA_NO_DECODING MA_NO_RESOURCE_MANAGER MA_NO_NODE_GRAPH MA_NO_ENGINE)
set_target_properties(miniaudio PROPERTIES C_STANDARD 11 C_VISIBILITY_PRESET hidden)
if(UNIX AND NOT APPLE)
  target_link_libraries(miniaudio PRIVATE pthread m dl)
endif()
install(TARGETS miniaudio RUNTIME DESTINATION native LIBRARY DESTINATION native)
install(FILES "${miniaudio_src_SOURCE_DIR}/LICENSE" DESTINATION native RENAME MINIAUDIO-LICENSE.txt OPTIONAL)

# --- SoundTouch -------------------------------------------------------------
option(AT_WITH_SOUNDTOUCH "Build SoundTouch (needed from P6)" ON)
if(AT_WITH_SOUNDTOUCH)
set(AT_SOUNDTOUCH_TAG "2.4.1" CACHE STRING "SoundTouch git tag")
# SoundTouch declares an old cmake_minimum_required that CMake 4 rejects without this.
set(CMAKE_POLICY_VERSION_MINIMUM 3.5)
set(SOUNDTOUCH_DLL ON CACHE BOOL "" FORCE)
set(BUILD_SHARED_LIBS ON CACHE BOOL "" FORCE)
FetchContent_Declare(soundtouch
  GIT_REPOSITORY https://codeberg.org/soundtouch/soundtouch.git
  GIT_TAG ${AT_SOUNDTOUCH_TAG}
  GIT_SHALLOW TRUE)
FetchContent_MakeAvailable(soundtouch)
# Also install the shared libraries next to the other native libraries.
install(TARGETS SoundTouch SoundTouchDLL RUNTIME DESTINATION native LIBRARY DESTINATION native)
install(FILES "${soundtouch_SOURCE_DIR}/COPYING.TXT" DESTINATION native RENAME SOUNDTOUCH-LICENSE.txt OPTIONAL)
endif()

# --- VST3 host shim (EQ-06, P12) --------------------------------------------
# The Steinberg VST3 SDK (MIT since 3.8; LICENSE.txt checked 23 Nov 2026) at a pinned tag, with only the submodules a
# host needs (no VSTGUI, docs or tutorials). EXCLUDE_FROM_ALL: only what our targets use gets built.
option(AT_WITH_VST3 "Build the VST3 host shim (needed from P12)" ON)
option(AT_VST3_TEST_PLUGINS "Also build VST3 test fixtures (the SDK's ADelay example and a module that crashes on scan)" OFF)
if(AT_WITH_VST3)
set(AT_VST3SDK_TAG "v3.8.1_build_84" CACHE STRING "VST3 SDK git tag")
set(SMTG_ENABLE_VSTGUI_SUPPORT OFF CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VST3_PLUGIN_EXAMPLES ${AT_VST3_TEST_PLUGINS} CACHE BOOL "" FORCE)
set(SMTG_ENABLE_VST3_HOSTING_EXAMPLES OFF CACHE BOOL "" FORCE)
set(SMTG_RUN_VST_VALIDATOR OFF CACHE BOOL "" FORCE)
set(SMTG_CREATE_PLUGIN_LINK OFF CACHE BOOL "" FORCE)   # never link into the system VST3 folder
set(SMTG_ENABLE_AUV2_BUILDS OFF CACHE BOOL "" FORCE)
set(SMTG_CREATE_MODULE_INFO OFF CACHE BOOL "" FORCE)
FetchContent_Declare(vst3sdk
  GIT_REPOSITORY https://github.com/steinbergmedia/vst3sdk.git
  GIT_TAG ${AT_VST3SDK_TAG}
  GIT_SHALLOW TRUE
  GIT_SUBMODULES base cmake pluginterfaces public.sdk
  SOURCE_SUBDIR no-cmake-here)  # added below with EXCLUDE_FROM_ALL
FetchContent_MakeAvailable(vst3sdk)
add_subdirectory("${vst3sdk_SOURCE_DIR}" "${CMAKE_BINARY_DIR}/vst3sdk" EXCLUDE_FROM_ALL)

# Like the SDK's editorhost sample: sdk_hosting leaves out the plug-in provider, the memory stream and the
# platform's module loader, which every host compiles itself.
set(AT_VST3_SOURCES
  "${CMAKE_CURRENT_SOURCE_DIR}/vst3host/at_vst3host.cpp"
  "${vst3sdk_SOURCE_DIR}/public.sdk/source/vst/hosting/plugprovider.cpp"
  "${vst3sdk_SOURCE_DIR}/public.sdk/source/common/memorystream.cpp")
if(APPLE)
  enable_language(OBJCXX)
  list(APPEND AT_VST3_SOURCES "${vst3sdk_SOURCE_DIR}/public.sdk/source/vst/hosting/module_mac.mm")
  set_source_files_properties("${vst3sdk_SOURCE_DIR}/public.sdk/source/vst/hosting/module_mac.mm" PROPERTIES COMPILE_FLAGS "-fobjc-arc")
elseif(WIN32)
  list(APPEND AT_VST3_SOURCES "${vst3sdk_SOURCE_DIR}/public.sdk/source/vst/hosting/module_win32.cpp")
else()
  list(APPEND AT_VST3_SOURCES "${vst3sdk_SOURCE_DIR}/public.sdk/source/vst/hosting/module_linux.cpp")
endif()
add_library(at_vst3host SHARED ${AT_VST3_SOURCES})
target_link_libraries(at_vst3host PRIVATE sdk_hosting)
if(APPLE)
  target_link_libraries(at_vst3host PRIVATE "-framework Cocoa")
elseif(UNIX)
  target_link_libraries(at_vst3host PRIVATE dl pthread)
endif()
target_include_directories(at_vst3host PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/vst3host")
set_target_properties(at_vst3host PROPERTIES CXX_STANDARD 17 CXX_VISIBILITY_PRESET hidden)
add_executable(at_vst3scan "${CMAKE_CURRENT_SOURCE_DIR}/vst3host/at_vst3scan.cpp")
target_link_libraries(at_vst3scan PRIVATE at_vst3host)
set_target_properties(at_vst3scan PROPERTIES CXX_STANDARD 17)
install(TARGETS at_vst3host at_vst3scan RUNTIME DESTINATION native LIBRARY DESTINATION native)
install(FILES "${vst3sdk_SOURCE_DIR}/LICENSE.txt" DESTINATION native RENAME VST3SDK-LICENSE.txt)

if(AT_VST3_TEST_PLUGINS)
  add_library(at_vst3_test_crash MODULE "${CMAKE_CURRENT_SOURCE_DIR}/vst3host/test_crash_module.c")
  set_target_properties(at_vst3_test_crash PROPERTIES PREFIX "" SUFFIX ".vst3" OUTPUT_NAME "crash-on-scan" C_VISIBILITY_PRESET hidden)
  add_custom_target(at_vst3_test_plugins ALL)
  add_dependencies(at_vst3_test_plugins adelay at_vst3_test_crash)
  install(TARGETS at_vst3_test_crash LIBRARY DESTINATION vst3-test RUNTIME DESTINATION vst3-test)
  install(DIRECTORY "${CMAKE_BINARY_DIR}/VST3/$<CONFIG>/adelay.vst3" DESTINATION vst3-test)
endif()
endif()
