# Builds the small native libraries AudioTastic needs on every platform (phase P0 pipeline):
#   - miniaudio (public domain / MIT-0): audio output (PB-04), used from P1
#   - SoundTouch (LGPL-2.1, shared library + C wrapper): time-stretch (PB-06, XF-02), used from P6
# libopenmpt (FM-07) is added in P4; VST3/ASIO shims in P6/P12.
#
#   cmake -S native -B native/build/<rid> -DCMAKE_BUILD_TYPE=Release -DAT_RID=<rid>
#   cmake --build native/build/<rid> --config Release
#   cmake --install native/build/<rid> --config Release
cmake_minimum_required(VERSION 3.24)
project(AudioTasticNative C CXX)

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
add_library(miniaudio SHARED "${miniaudio_src_SOURCE_DIR}/miniaudio.c")
target_include_directories(miniaudio PUBLIC "${miniaudio_src_SOURCE_DIR}")
target_compile_definitions(miniaudio PRIVATE MA_DLL MA_NO_ENCODING MA_NO_GENERATION)
if(UNIX AND NOT APPLE)
  target_link_libraries(miniaudio PRIVATE pthread m dl)
endif()
install(TARGETS miniaudio RUNTIME DESTINATION native LIBRARY DESTINATION native)
install(FILES "${miniaudio_src_SOURCE_DIR}/LICENSE" DESTINATION native RENAME MINIAUDIO-LICENSE.txt OPTIONAL)

# --- SoundTouch -------------------------------------------------------------
option(AT_WITH_SOUNDTOUCH "Build SoundTouch (needed from P6)" ON)
if(AT_WITH_SOUNDTOUCH)
set(AT_SOUNDTOUCH_TAG "2.3.3" CACHE STRING "SoundTouch git tag")
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
