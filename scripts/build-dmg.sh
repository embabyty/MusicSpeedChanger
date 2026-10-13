#!/bin/zsh
# Build a "drag to Applications" DMG installer into dist/.
# Usage: ./scripts/build-dmg.sh [release|debug]
set -euo pipefail
cd "$(dirname "$0")/.."

CONFIG="${1:-release}"
VERSION="${VERSION:-1.0}"
VOLUME="MusicSpeedChanger"
DMG="dist/${VOLUME}-${VERSION}.dmg"
STAGE="/tmp/${VOLUME}-dmg"

# 1. Fresh app bundle.
./scripts/build-app.sh "$CONFIG"

# 2. Stage: app + Applications symlink.
rm -rf "$STAGE" "$DMG"
mkdir -p "$STAGE"
cp -R "dist/${VOLUME}.app" "$STAGE/"
ln -s /Applications "$STAGE/Applications"

# 3. Writable disk image, then lay it out in Finder.
# Drop any stale mounts from a previous failed run so the new image
# mounts at exactly /Volumes/${VOLUME} (not "VOLUME 1").
for M in "/Volumes/${VOLUME}" "/Volumes/${VOLUME} 1"; do
  hdiutil detach "$M" -quiet 2>/dev/null || true
done
rm -f /tmp/${VOLUME}-rw.dmg
hdiutil create -srcfolder "$STAGE" -volname "$VOLUME" -fs HFS+ \
    -format UDRW -size 64m -o /tmp/${VOLUME}-rw.dmg >/dev/null
ATTACH_OUT=$(hdiutil attach -readwrite -noverify -noautoopen /tmp/${VOLUME}-rw.dmg)
echo "$ATTACH_OUT"
VOL_LINE=$(echo "$ATTACH_OUT" | grep -E '/Volumes/' | head -1)
DEV=$(echo "$VOL_LINE" | awk '{print $1}')
MNT=$(echo "$VOL_LINE" | sed -n 's|.*\(/Volumes/.*\)|\1|p')
echo "DEV=$DEV MNT=$MNT"
sleep 2

# Volume icon (app logo in Finder's sidebar/titlebar). Cosmetic: don't fail the build.
cp Resources/AppIcon.icns "$MNT/.VolumeIcon.icns" 2>/dev/null || echo "warning: volume icon copy failed"
SetFile -a C "$MNT" 2>/dev/null || true

osascript <<EOF || echo "warning: Finder DMG layout skipped (headless runner?)"
tell application "Finder"
    tell disk "${VOLUME}"
        open
        set current view of container window to icon view
        set toolbar visible of container window to false
        set statusbar visible of container window to false
        set the bounds of container window to {200, 120, 860, 560}
        set viewOptions to the icon view options of container window
        set arrangement of viewOptions to not arranged
        set icon size of viewOptions to 128
        delay 1
        set position of item "${VOLUME}.app" of container window to {170, 210}
        set position of item "Applications" of container window to {490, 210}
        close
        open
        update without registering applications
        delay 2
    end tell
end tell
EOF

# 4. Compress to a read-only distributable image.
hdiutil detach "$DEV" -quiet
hdiutil convert /tmp/${VOLUME}-rw.dmg -format UDZO -imagekey zlib-level=9 -o "$DMG" >/dev/null
rm -f /tmp/${VOLUME}-rw.dmg
rm -rf "$STAGE"
echo "Built $DMG"
ls -la "$DMG"
