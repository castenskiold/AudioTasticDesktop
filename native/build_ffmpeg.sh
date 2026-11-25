#!/usr/bin/env bash
# AudioTastic's own audio-focused LGPL FFmpeg (P14), built from source for every runtime identifier.
#
# What it contains (and why it is small): avformat, avcodec, avutil and swresample only (no avfilter, avdevice or
# swscale; no programs); every native audio decoder plus the two picture decoders cover art needs (mjpeg, png); only the
# encoders the player uses (FLAC, ALAC, AAC, PCM, LAME MP3, Opus, Vorbis for conversion and casting, plus the platform's
# H.264 encoder for casting visuals); all demuxers and muxers; the network protocols internet radio needs.
# External libraries, all linked statically into avcodec: LAME (LGPL), Opus, Ogg and Vorbis (BSD), zlib (Windows; the
# system's elsewhere) and OpenH264 (BSD, Linux only). TLS comes from the OS (Schannel, Secure Transport) or the
# system's OpenSSL 3 on Linux. Never --enable-gpl, --enable-nonfree or --enable-version3: the result is LGPL-2.1-or-later.
#
# Hosts: Linux builds natively; Windows is cross-built on Linux with llvm-mingw (downloaded, pinned); macOS builds on a
# Mac (x86_64 as a cross build on Apple silicon). CI runs this per RID (.github/workflows/ci.yml, job "ffmpeg").
#
# Usage:  native/build_ffmpeg.sh <rid>       win-x64 | win-arm64 | linux-x64 | linux-arm64 | osx-arm64 | osx-x64
#         native/build_ffmpeg.sh sources     download (and verify) every source archive, e.g. for the release's source bundle
# Needs:  curl, make, pkg-config, nasm (x86_64 targets); Linux also libssl-dev. Result: native/out/<rid>/native.
set -euo pipefail

HERE="$(cd "$(dirname "$0")" && pwd)"
SRC_DIR="$HERE/downloads/ffmpeg-src"
FFMPEG_VERSION=8.1.3

# name | url | sha256 (checked on every use; when upgrading, check the new version's licence too)
SOURCES="
ffmpeg-$FFMPEG_VERSION.tar.xz|https://ffmpeg.org/releases/ffmpeg-$FFMPEG_VERSION.tar.xz|7138d28c96d9d3e3af4ee3d8cad72741f8ffb40da90c1112235dea3ecd3178a3
lame-3.100.tar.gz|https://downloads.sourceforge.net/project/lame/lame/3.100/lame-3.100.tar.gz|ddfe36cab873794038ae2c1210557ad34857a4b6bdc515785d1da9e175b1da1e
libogg-1.3.6.tar.xz|https://downloads.xiph.org/releases/ogg/libogg-1.3.6.tar.xz|5c8253428e181840cd20d41f3ca16557a9cc04bad4a3d04cce84808677fa1061
libvorbis-1.3.7.tar.xz|https://downloads.xiph.org/releases/vorbis/libvorbis-1.3.7.tar.xz|b33cc4934322bcbf6efcbacf49e3ca01aadbea4114ec9589d1b1e9d20f72954b
opus-1.6.1.tar.gz|https://downloads.xiph.org/releases/opus/opus-1.6.1.tar.gz|6ffcb593207be92584df15b32466ed64bbec99109f007c82205f0194572411a1
openh264-2.6.0.tar.gz|https://github.com/cisco/openh264/archive/refs/tags/v2.6.0.tar.gz|558544ad358283a7ab2930d69a9ceddf913f4a51ee9bf1bfb9e377322af81a69
zlib-1.3.2.tar.xz|https://github.com/madler/zlib/releases/download/v1.3.2/zlib-1.3.2.tar.xz|d7a0654783a4da529d1bb793b7ad9c3318020af77667bcae35f95d0e42a792f3
"
# The Windows cross toolchain (llvm-mingw, Apache-2.0 with LLVM exceptions; only used to build, never shipped).
LLVM_MINGW_RELEASE=20261104
LLVM_MINGW_X86_64=919380403de87dca3c581c2053230cd298af5e891286628ba0613b7fed370926
LLVM_MINGW_AARCH64=595e95bdffabfc62559fe26e30bdfbccf277cbaed4902793555015ee47306b94

sha256() { if command -v sha256sum >/dev/null; then sha256sum "$1" | cut -d' ' -f1; else shasum -a 256 "$1" | cut -d' ' -f1; fi; }

# fetch <file> <url> <sha256> [dir]: download once (three tries), always verify.
fetch() {
  local file="$1" url="$2" want="$3" dir="${4:-$SRC_DIR}"
  mkdir -p "$dir"
  if [ ! -f "$dir/$file" ]; then
    for attempt in 1 2 3; do
      curl -fsSL --retry 3 -o "$dir/$file.part" "$url" && break
      [ "$attempt" = 3 ] && { echo "Download failed: $url" >&2; exit 1; }
      sleep 10
    done
    mv "$dir/$file.part" "$dir/$file"
  fi
  local got; got="$(sha256 "$dir/$file")"
  if [ "$got" != "$want" ]; then
    echo "SHA-256 mismatch for $file: got $got, expected $want" >&2
    rm -f "$dir/$file"
    exit 1
  fi
}

source_line() { printf '%s\n' "$SOURCES" | grep "^$1|"; }
fetch_source() { local line; line="$(source_line "$1")"; IFS='|' read -r f u s <<<"$line"; fetch "$f" "$u" "$s"; }

if [ "${1:-}" = "sources" ]; then
  printf '%s\n' "$SOURCES" | while IFS='|' read -r f u s; do [ -n "$f" ] && fetch "$f" "$u" "$s"; done
  echo "Sources verified in $SRC_DIR"
  exit 0
fi

RID="${1:?usage: build_ffmpeg.sh <rid> | sources}"
WORK="$HERE/build/ffmpeg-$RID"
PREFIX="$WORK/prefix"
OUT="$HERE/out/$RID/native"
JOBS="$(getconf _NPROCESSORS_ONLN 2>/dev/null || sysctl -n hw.ncpu)"

case "$RID" in
  win-x64)     OS=windows; HOST=x86_64-w64-mingw32;  FFARCH=x86_64 ;;
  win-arm64)   OS=windows; HOST=aarch64-w64-mingw32; FFARCH=aarch64 ;;
  linux-x64)   OS=linux;   HOST="";                  FFARCH=x86_64 ;;
  linux-arm64) OS=linux;   HOST="";                  FFARCH=aarch64 ;;
  osx-arm64)   OS=macos;   HOST="";                  FFARCH=arm64 ;;
  osx-x64)     OS=macos;   HOST="";                  FFARCH=x86_64 ;;
  *) echo "Unknown RID $RID" >&2; exit 1 ;;
esac

rm -rf "$WORK"
mkdir -p "$WORK" "$PREFIX/include" "$PREFIX/lib/pkgconfig" "$OUT"
TOOLCHAIN="system compiler"

# --- toolchain --------------------------------------------------------------------------------------------------
CONFIGURE_HOST=()
export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig"
case "$OS" in
  windows)
    case "$(uname -m)" in
      x86_64) tc_arch=x86_64; tc_sha="$LLVM_MINGW_X86_64" ;;
      aarch64|arm64) tc_arch=aarch64; tc_sha="$LLVM_MINGW_AARCH64" ;;
      *) echo "No llvm-mingw for $(uname -m)" >&2; exit 1 ;;
    esac
    tc_name="llvm-mingw-$LLVM_MINGW_RELEASE-ucrt-ubuntu-22.04-$tc_arch"
    fetch "$tc_name.tar.xz" "https://github.com/mstorsjo/llvm-mingw/releases/download/$LLVM_MINGW_RELEASE/$tc_name.tar.xz" "$tc_sha" "$HERE/downloads"
    [ -d "$HERE/build/$tc_name" ] || tar -xJf "$HERE/downloads/$tc_name.tar.xz" -C "$HERE/build"
    export PATH="$HERE/build/$tc_name/bin:$PATH"
    export CC="$HOST-clang" AR="$HOST-ar" RANLIB="$HOST-ranlib" STRIP="$HOST-strip"
    export CFLAGS="-O2"
    # Only our own libraries: never pick up the build machine's .pc files for a Windows target.
    export PKG_CONFIG_LIBDIR="$PREFIX/lib/pkgconfig"
    CONFIGURE_HOST=(--host="$HOST")
    TOOLCHAIN="llvm-mingw $LLVM_MINGW_RELEASE (UCRT)"
    ;;
  linux)
    export CC="${CC:-cc}" CFLAGS="-O2"
    ;;
  macos)
    # .NET 10 runs on macOS 12 and later; without this the libraries would need the build machine's macOS version.
    export MACOSX_DEPLOYMENT_TARGET=12.0
    export CC="clang -arch $FFARCH" CFLAGS="-O2"
    if [ "$FFARCH" != "$(uname -m)" ]; then CONFIGURE_HOST=(--host="$FFARCH-apple-darwin"); fi
    ;;
esac

unpack() { fetch_source "$1"; tar -xf "$SRC_DIR/$1" -C "$WORK"; }

# Old config.sub/config.guess (LAME 2017, Vorbis 2020) may not know aarch64-w64-mingw32: use the system's newer copies.
refresh_config_scripts() {
  for f in config.sub config.guess; do
    for sys in /usr/share/misc/$f /usr/share/automake-*/$f; do
      if [ -f "$sys" ] && [ -f "$1/$f" ]; then cp "$sys" "$1/$f"; break; fi
    done
  done
}

autotools_build() { # <dir> <configure args...>
  local dir="$1"; shift
  refresh_config_scripts "$dir"
  (cd "$dir" && ./configure --prefix="$PREFIX" --enable-static --disable-shared --with-pic ${CONFIGURE_HOST[@]+"${CONFIGURE_HOST[@]}"} "$@" \
     && make -j"$JOBS" && make install)
}

# --- external libraries (static, position-independent) ----------------------------------------------------------
if [ "$OS" = windows ]; then
  # zlib (cover art in PNG, compressed ID3 frames and MP4 headers); Linux and macOS use the system's libz.
  unpack zlib-1.3.2.tar.xz
  (cd "$WORK/zlib-1.3.2"
   for c in adler32 compress crc32 deflate gzclose gzlib gzread gzwrite infback inffast inflate inftrees trees uncompr zutil; do
     $CC $CFLAGS -c "$c.c" -o "$c.o"
   done
   $AR rcs "$PREFIX/lib/libz.a" ./*.o
   cp zlib.h zconf.h "$PREFIX/include/")
fi

unpack lame-3.100.tar.gz
autotools_build "$WORK/lame-3.100" --disable-frontend --disable-decoder --disable-gtktest

unpack libogg-1.3.6.tar.xz
autotools_build "$WORK/libogg-1.3.6"

unpack libvorbis-1.3.7.tar.xz
# libvorbis 1.3.7's configure adds -force_cpusubtype_ALL on macOS, which current Apple linkers reject.
sed -i.bak 's/-force_cpusubtype_ALL//g' "$WORK/libvorbis-1.3.7/configure"
autotools_build "$WORK/libvorbis-1.3.7" --with-ogg="$PREFIX" --disable-oggtest --disable-docs --disable-examples

unpack opus-1.6.1.tar.gz
autotools_build "$WORK/opus-1.6.1" --disable-doc --disable-extra-programs

if [ "$OS" = linux ]; then
  # OpenH264 (BSD) for casting visuals (CN-09); Windows and macOS use the OS's H.264 encoder instead.
  unpack openh264-2.6.0.tar.gz
  make -C "$WORK/openh264-2.6.0" -j"$JOBS" OS=linux ARCH="$FFARCH" PREFIX="$PREFIX" install-static
  # It is C++: a static link needs the C++ runtime, which its .pc file leaves out.
  sed -i 's/^Libs: \(.*\)$/Libs: \1 -lstdc++/' "$PREFIX/lib/pkgconfig/openh264.pc"
fi

# --- FFmpeg -----------------------------------------------------------------------------------------------------
unpack "ffmpeg-$FFMPEG_VERSION.tar.xz"
FF="$WORK/ffmpeg-$FFMPEG_VERSION"

# Every native audio decoder: the entries of allcodecs.c from "/* audio codecs */" up to "/* subtitles */" (audio, PCM,
# DPCM and ADPCM). Plus mjpeg and png, so cover-art streams are recognised as quickly as before.
AUDIO_DECODERS="$(sed -n '/\/\* audio codecs \*\//,/\/\* subtitles \*\//p' "$FF/libavcodec/allcodecs.c" \
  | sed -n 's/.*ff_\([a-z0-9_]*\)_decoder;.*/\1/p' | paste -sd, -)"
[ -n "$AUDIO_DECODERS" ] || { echo "Could not read the audio decoder list from allcodecs.c" >&2; exit 1; }

ENCODERS="flac,alac,aac,pcm_s16le,pcm_s24le,libmp3lame,libopus,libvorbis"
PLATFORM=()
case "$OS" in
  windows)
    ENCODERS="$ENCODERS,h264_mf"
    PLATFORM=(--target-os=mingw32 --arch="$FFARCH" --enable-cross-compile --cross-prefix="$HOST-" --cc="$HOST-clang"
              --pkg-config=pkg-config --enable-w32threads --enable-mediafoundation --enable-schannel)
    ;;
  linux)
    ENCODERS="$ENCODERS,libopenh264"
    PLATFORM=(--enable-pthreads --enable-openssl --enable-libopenh264)
    ;;
  macos)
    ENCODERS="$ENCODERS,h264_videotoolbox"
    PLATFORM=(--arch="$FFARCH" --cc="clang -arch $FFARCH" --enable-pthreads --enable-videotoolbox
              --enable-securetransport --install-name-dir=@rpath)
    if [ "$FFARCH" != "$(uname -m)" ]; then PLATFORM+=(--enable-cross-compile --target-os=darwin); fi
    ;;
esac

CONFIGURE=(
  --prefix="$PREFIX"
  --enable-shared --disable-static
  --disable-programs --disable-doc --disable-debug
  --disable-autodetect
  --disable-avdevice --disable-avfilter --disable-swscale
  --disable-hwaccels --disable-indevs --disable-outdevs
  --disable-decoders --enable-decoder="$AUDIO_DECODERS,mjpeg,png"
  --disable-encoders --enable-encoder="$ENCODERS"
  --disable-protocols --enable-protocol=file,pipe,http,https,httpproxy,tcp,tls,crypto,data
  --enable-zlib --enable-libmp3lame --enable-libopus --enable-libvorbis
  --pkg-config-flags=--static
  --extra-cflags="-I$PREFIX/include" --extra-ldflags="-L$PREFIX/lib"
  "${PLATFORM[@]}"
)
# FFmpeg picks its own optimisation flags (CFLAGS above were for the libraries).
unset CFLAGS
(cd "$FF" && ./configure "${CONFIGURE[@]}" && make -j"$JOBS" && make install)

# Every encoder we asked for must be there (configure silently drops a component whose dependency is missing).
for e in ${ENCODERS//,/ }; do
  grep -q "^#define CONFIG_$(echo "$e" | tr '[:lower:]' '[:upper:]')_ENCODER 1" "$FF/config_components.h" \
    || { echo "Encoder $e was not enabled" >&2; exit 1; }
done
grep -q '^#define CONFIG_HTTPS_PROTOCOL 1' "$FF/config_components.h" || { echo "https protocol was not enabled" >&2; exit 1; }

# --- results ----------------------------------------------------------------------------------------------------
LIBS=(avcodec avformat avutil swresample)
rm -f "$OUT"/*avcodec* "$OUT"/*avformat* "$OUT"/*avutil* "$OUT"/*swresample* "$OUT"/FFMPEG-*
case "$OS" in
  windows)
    for l in "${LIBS[@]}"; do cp "$PREFIX"/bin/"$l"-*.dll "$OUT/"; done
    for f in "$OUT"/*.dll; do "$STRIP" --strip-unneeded "$f"; done
    # Only Windows' own DLLs may be imported (no MinGW runtime such as libwinpthread or libc++).
    imports="$(for l in "${LIBS[@]}"; do llvm-objdump -p "$OUT/$l"-*.dll | sed -n 's/.*DLL Name: //p'; done | sort -u)"
    echo "Imports: $(echo "$imports" | paste -sd' ' -)"
    if echo "$imports" | grep -Eiq 'winpthread|libc\+\+|libunwind|libgcc|libstdc'; then echo "Unexpected MinGW runtime import" >&2; exit 1; fi
    ;;
  linux)
    for l in "${LIBS[@]}"; do
      soname="$(objdump -p "$PREFIX/lib/lib$l.so" | awk '/SONAME/{print $2}')"
      cp -L "$PREFIX/lib/lib$l.so" "$OUT/$soname"
      strip --strip-unneeded "$OUT/$soname"
    done
    echo "Needs: $(for f in "$OUT"/libav*.so.* "$OUT"/libsw*.so.*; do objdump -p "$f" | awk '/NEEDED/{print $2}'; done | sort -u | paste -sd' ' -)"
    ;;
  macos)
    for l in "${LIBS[@]}"; do
      for f in "$PREFIX"/lib/lib"$l".*.dylib; do
        n="$(basename "$f")"
        # Only the major-version names (libavcodec.62.dylib), which are the install names the libraries refer to.
        if [[ "$n" =~ ^lib$l\.[0-9]+\.dylib$ ]]; then cp -L "$f" "$OUT/$n"; fi
      done
    done
    ;;
esac

# Licences of everything inside the libraries (shipped next to them).
{
  echo "FFmpeg $FFMPEG_VERSION as built for AudioTastic ($RID): LGPL-2.1-or-later. Source and build script: see FFMPEG-BUILDINFO.txt."
  echo "The libraries below are linked into avcodec. Their licences follow."
  section() { printf '\n\n==================== %s ====================\n\n' "$1"; cat "$2"; }
  section "FFmpeg (LGPL-2.1-or-later)" "$FF/COPYING.LGPLv2.1"
  section "LAME 3.100 (LGPL-2.0-or-later)" "$WORK/lame-3.100/COPYING"
  section "Opus 1.6.1 (BSD-3-Clause)" "$WORK/opus-1.6.1/COPYING"
  section "libogg 1.3.6 (BSD-3-Clause)" "$WORK/libogg-1.3.6/COPYING"
  section "libvorbis 1.3.7 (BSD-3-Clause)" "$WORK/libvorbis-1.3.7/COPYING"
  if [ "$OS" = windows ]; then section "zlib 1.3.2 (zlib)" "$WORK/zlib-1.3.2/LICENSE"; fi
  if [ "$OS" = linux ]; then section "OpenH264 2.6.0 (BSD-2-Clause)" "$WORK/openh264-2.6.0/LICENSE"; fi
} > "$OUT/FFMPEG-LICENSES.txt"

{
  echo "FFmpeg $FFMPEG_VERSION, built by native/build_ffmpeg.sh for $RID ($TOOLCHAIN)"
  echo "variant: LGPL-2.1-or-later, shared; no --enable-gpl, --enable-nonfree or --enable-version3"
  echo "sources (SHA-256):"
  printf '%s\n' "$SOURCES" | while IFS='|' read -r f u s; do [ -n "$f" ] && echo "  $u  $s"; done
  echo "encoders: $ENCODERS"
  echo "decoders: $(echo "$AUDIO_DECODERS" | tr ',' '\n' | wc -l | tr -d ' ') native audio decoders, mjpeg, png"
  echo "configure: ${CONFIGURE[*]}"
} > "$OUT/FFMPEG-BUILDINFO.txt"

ls -l "$OUT"
echo "$RID: FFmpeg $FFMPEG_VERSION -> $OUT"
