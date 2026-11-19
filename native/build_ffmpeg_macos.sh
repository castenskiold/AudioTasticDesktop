#!/usr/bin/env bash
# Build LGPL FFmpeg shared libraries for macOS (BtbN does not provide macOS builds).
# Decoders only need FFmpeg itself (native Opus/Vorbis decoders); external encoder libraries
# (libmp3lame, libopus, libvorbis) are added in P11/P13 when encoding is needed.
#
# Usage: native/build_ffmpeg_macos.sh [arm64|x86_64] [ffmpeg-version]
set -euo pipefail
ARCH="${1:-$(uname -m)}"
VERSION="${2:-8.1}"
HERE="$(cd "$(dirname "$0")" && pwd)"
case "$ARCH" in
  arm64) RID=osx-arm64 ;;
  x86_64) RID=osx-x64 ;;
  *) echo "Unknown arch $ARCH" >&2; exit 1 ;;
esac
SRC="$HERE/build/ffmpeg-$VERSION"
OUT="$HERE/out/$RID/native"
mkdir -p "$HERE/downloads" "$HERE/build" "$OUT"
TARBALL="$HERE/downloads/ffmpeg-$VERSION.tar.xz"
[ -f "$TARBALL" ] || curl -fsSL "https://ffmpeg.org/releases/ffmpeg-$VERSION.tar.xz" -o "$TARBALL"
[ -d "$SRC" ] || tar -xJf "$TARBALL" -C "$HERE/build"
cd "$SRC"
# Building x86_64 on an arm64 Mac (GitHub's macOS runners) is a cross build; the x86 assembly needs nasm.
CROSS=()
if [ "$ARCH" != "$(uname -m)" ]; then
  CROSS=(--enable-cross-compile --target-os=darwin)
fi
./configure \
  --prefix="$HERE/build/install-$RID" \
  --arch="$ARCH" --cc="clang -arch $ARCH" ${CROSS[@]+"${CROSS[@]}"} \
  --enable-shared --disable-static \
  --disable-programs --disable-doc --disable-debug \
  --disable-gpl --disable-nonfree \
  --disable-autodetect --enable-audiotoolbox \
  --disable-avdevice --disable-avfilter --disable-swscale \
  --install-name-dir='@rpath'
make -j"$(sysctl -n hw.ncpu)"
make install
cp -a "$HERE/build/install-$RID/lib/"*.dylib "$OUT/"
cp COPYING.LGPLv2.1 "$OUT/FFMPEG-LICENSE.txt"
printf 'source: https://ffmpeg.org/releases/ffmpeg-%s.tar.xz\nvariant: lgpl-shared (built locally)\n' "$VERSION" > "$OUT/FFMPEG-BUILDINFO.txt"
echo "$RID: FFmpeg $VERSION -> $OUT"
