#!/usr/bin/env bash
# FM-07: builds libopenmpt (BSD-3-Clause) as a shared library for Linux or macOS, without its optional
# mpg123/ogg/vorbis dependencies (modules with MP3/Vorbis-compressed samples are rare).
# Built by CI on Linux and macOS. On macOS the target architecture comes from the RID (osx-x64 is cross-built on the
# arm64 runners). Alternative: the system package (libopenmpt0, brew libopenmpt).
#
# Usage: native/build_libopenmpt_unix.sh <rid>      e.g. linux-x64, linux-arm64, osx-arm64, osx-x64
# Result: native/out/libopenmpt/runtimes/<rid>/native/libopenmpt.{so.0,dylib} plus the licence.
set -euo pipefail
RID="${1:?runtime identifier, e.g. linux-x64}"
VERSION="0.8.10"
HERE="$(cd "$(dirname "$0")" && pwd)"
WORK="$HERE/build/libopenmpt-$RID"
OUT="$HERE/out/libopenmpt/runtimes/$RID/native"
TARBALL="libopenmpt-$VERSION+release.makefile.tar.gz"

mkdir -p "$WORK" "$OUT" "$HERE/downloads"
[ -f "$HERE/downloads/$TARBALL" ] || curl -fL -o "$HERE/downloads/$TARBALL" "https://lib.openmpt.org/files/libopenmpt/src/$TARBALL"
tar -xzf "$HERE/downloads/$TARBALL" -C "$WORK" --strip-components=1
cd "$WORK"
ARCH_FLAGS=()
case "$RID" in
  osx-arm64) ARCH_FLAGS=(CC="clang -arch arm64" CXX="clang++ -arch arm64") ;;
  osx-x64)   ARCH_FLAGS=(CC="clang -arch x86_64" CXX="clang++ -arch x86_64") ;;
esac
make -j"$(getconf _NPROCESSORS_ONLN)" ${ARCH_FLAGS[@]+"${ARCH_FLAGS[@]}"} SHARED_LIB=1 STATIC_LIB=0 EXAMPLES=0 OPENMPT123=0 TEST=0 \
    NO_MPG123=1 NO_OGG=1 NO_VORBIS=1 NO_VORBISFILE=1 NO_PORTAUDIO=1 NO_PORTAUDIOCPP=1 NO_PULSEAUDIO=1 NO_SDL2=1 NO_SNDFILE=1 NO_FLAC=1
ls -l bin
case "$RID" in
  # On macOS the makefile writes the Mach-O library as bin/libopenmpt.so; .NET loads libopenmpt.dylib.
  osx-*) cp -L "$(ls bin/libopenmpt*.dylib bin/libopenmpt.so 2>/dev/null | head -n 1)" "$OUT/libopenmpt.dylib" ;;
  *)     cp -L bin/libopenmpt.so* "$OUT/" ;;
esac
cp LICENSE "$OUT/libopenmpt-LICENSE.txt"
echo "libopenmpt for $RID -> $OUT"
