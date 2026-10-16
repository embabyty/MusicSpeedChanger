#!/bin/zsh
# Build MusicSpeedChanger.app (icon, bundle id, version) into dist/.
set -euo pipefail
cd "$(dirname "$0")/.."

CONFIG="${1:-release}"
APP="dist/MusicSpeedChanger.app"

swift build -c "$CONFIG"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
BINDIR="$(swift build -c "$CONFIG" --show-bin-path)"
cp "$BINDIR/MusicSpeedChanger" "$APP/Contents/MacOS/MusicSpeedChanger"
cp Resources/Info.plist "$APP/Contents/Info.plist"
# Stamp the release version (e.g. 1.0.1-devbeta.3) so the app can tell
# gated Beta builds apart from stable ones at runtime.
if [ -n "${VERSION:-}" ]; then
  plutil -replace CFBundleShortVersionString -string "$VERSION" "$APP/Contents/Info.plist"
fi
cp Resources/AppIcon.icns "$APP/Contents/Resources/AppIcon.icns"

codesign --force --sign - --timestamp=none "$APP" 2>/dev/null || true
echo "Built $APP"
