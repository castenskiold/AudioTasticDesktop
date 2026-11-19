#!/usr/bin/env bash
# P10: packs a published macOS build into AudioTastic.app inside a .dmg (drag to Applications).
#   packaging/macos/make_app.sh <published folder> <version> <rid> <output folder>
# The app is ad-hoc signed (free; Apple silicon needs a signature to run) but not notarised: the Apple Developer ID is
# deferred, so the first start needs right-click > Open. Needs macOS (hdiutil, codesign, PlistBuddy).
set -euo pipefail

src="$1"; version="$2"; rid="$3"; out="$4"
root="$(cd "$(dirname "$0")/../.." && pwd)"
work="$(mktemp -d)"
app="$work/stage/AudioTastic.app"

mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources" "$out"
cp -R "$src/." "$app/Contents/MacOS/"
chmod +x "$app/Contents/MacOS/AudioTastic"
cp "$root/assets/branding/audiotastic.icns" "$app/Contents/Resources/audiotastic.icns"

# Audio and playlist types the app opens (Finder "Open With"; the user picks the default).
types=(wav flac mp3 aiff aif ape aac m4a wv ogg oga opus wma mpc tta shn dsf dff mid midi mod xm it s3m m3u m3u8 pls xspf cue)
exts=""
for t in "${types[@]}"; do exts+="<string>${t}</string>"; done

cat > "$app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>AudioTastic</string>
  <key>CFBundleDisplayName</key><string>AudioTastic</string>
  <key>CFBundleIdentifier</key><string>dk.bigapps.audiotastic</string>
  <key>CFBundleExecutable</key><string>AudioTastic</string>
  <key>CFBundleIconFile</key><string>audiotastic</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${version}</string>
  <key>CFBundleVersion</key><string>${version}</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>Copyright (c) Big Apps ApS. MIT license.</string>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key><string>Audio file or playlist</string>
      <key>CFBundleTypeRole</key><string>Viewer</string>
      <key>LSHandlerRank</key><string>Alternate</string>
      <key>CFBundleTypeExtensions</key><array>${exts}</array>
    </dict>
  </array>
</dict>
</plist>
PLIST

# Ad-hoc signatures: every Mach-O file on its own (Apple silicon refuses to run unsigned code), then the bundle
# without --deep, which would take the plugin folders (names with dots) for nested bundles and fail.
while IFS= read -r -d '' file; do
  if file -b "$file" | grep -q 'Mach-O'; then
    codesign --force --sign - "$file"
  fi
done < <(find "$app/Contents/MacOS" -type f -print0)
codesign --force --sign - "$app" || echo "warning: the bundle itself could not be signed (its binaries are)"
ln -s /Applications "$work/stage/Applications"
dmg="$out/AudioTastic-${version}-${rid}.dmg"
hdiutil create -volname "AudioTastic ${version}" -srcfolder "$work/stage" -ov -format UDZO "$dmg"
rm -rf "$work"
echo "Wrote $dmg"
