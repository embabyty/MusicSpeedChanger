#!/bin/zsh
# Build MusicSpeedChanger.app (icon, bundle id, version) into dist/.
set -euo pipefail
cd "$(dirname "$0")/.."

CONFIG="${1:-release}"
APP="dist/MusicSpeedChanger.app"
MACOS_TRIPLE="apple-macosx26.0"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

if [[ "$CONFIG" == "release" ]]; then
  # Universal: Apple silicon + Intel, then one binary in the bundle.
  for ARCH in arm64 x86_64; do
    swift build -c "$CONFIG" --triple "${ARCH}-${MACOS_TRIPLE}"
  done
  ARM_BIN="$(swift build -c "$CONFIG" --triple "arm64-${MACOS_TRIPLE}" --show-bin-path)/MusicSpeedChanger"
  X64_BIN="$(swift build -c "$CONFIG" --triple "x86_64-${MACOS_TRIPLE}" --show-bin-path)/MusicSpeedChanger"
  lipo -create -output "$APP/Contents/MacOS/MusicSpeedChanger" "$ARM_BIN" "$X64_BIN"
else
  swift build -c "$CONFIG"
  BINDIR="$(swift build -c "$CONFIG" --show-bin-path)"
  cp "$BINDIR/MusicSpeedChanger" "$APP/Contents/MacOS/MusicSpeedChanger"
fi

cp Resources/Info.plist "$APP/Contents/Info.plist"
# Stamp the release version (e.g. 1.0.1-devbeta.5) when VERSION is set.
if [ -n "${VERSION:-}" ]; then
  plutil -replace CFBundleShortVersionString -string "$VERSION" "$APP/Contents/Info.plist"
fi
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"

codesign --force --sign - --timestamp=none "$APP" 2>/dev/null || true
echo "Built $APP"
