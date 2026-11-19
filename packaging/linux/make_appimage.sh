#!/usr/bin/env bash
# P10: packs a published Linux build into an AppImage (one file; chmod +x and run).
#   packaging/linux/make_appimage.sh <published folder> <version> <rid> <output folder>
# Uses appimagetool (MIT), downloaded to native/downloads/appimagetool. It runs extracted, so no FUSE is needed.
set -euo pipefail

src="$1"; version="$2"; rid="$3"; out="$4"
root="$(cd "$(dirname "$0")/../.." && pwd)"
case "$rid" in
  linux-x64) arch=x86_64 ;;
  linux-arm64) arch=aarch64 ;;
  *) echo "Unsupported RID $rid"; exit 1 ;;
esac

tools="$root/native/downloads/appimagetool"
tool="$tools/appimagetool-x86_64.AppImage" # the build machine is x86_64; ARCH picks the target runtime
mkdir -p "$tools" "$out"
if [ ! -x "$tool" ]; then
  curl -fsSL --retry 3 -o "$tool" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
  chmod +x "$tool"
fi

work="$(mktemp -d)"
appdir="$work/AudioTastic.AppDir"
mkdir -p "$appdir/usr/bin" "$appdir/usr/share/applications" "$appdir/usr/share/icons/hicolor/256x256/apps"
cp -R "$src/." "$appdir/usr/bin/"
chmod +x "$appdir/usr/bin/AudioTastic"
cp "$root/packaging/linux/audiotastic.desktop" "$appdir/audiotastic.desktop"
cp "$root/packaging/linux/audiotastic.desktop" "$appdir/usr/share/applications/audiotastic.desktop"
cp "$root/assets/branding/png/audiotastic-256.png" "$appdir/audiotastic.png"
cp "$root/assets/branding/png/audiotastic-256.png" "$appdir/usr/share/icons/hicolor/256x256/apps/audiotastic.png"
cat > "$appdir/AppRun" <<'RUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/AudioTastic" "$@"
RUN
chmod +x "$appdir/AppRun"

image="$out/AudioTastic-${version}-${rid}.AppImage"
ARCH="$arch" APPIMAGE_EXTRACT_AND_RUN=1 "$tool" --no-appstream "$appdir" "$image"
rm -rf "$work"
echo "Wrote $image"
