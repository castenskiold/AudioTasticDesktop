# Builds the small native libraries AudioTastic needs on every platform (phase P0 pipeline):
#   - miniaudio (public domain / MIT-0): audio output (PB-04), used from P1
#   - SoundTouch (LGPL-2.1, shared library + C wrapper): time-stretch (PB-06, XF-02), used from P6
# libopenmpt (FM-07) is added in P4; VST3/ASIO shims in P6/P12.
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
