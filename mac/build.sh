#!/bin/bash
# Builds AutoCopy.app, signs it and installs it into /Applications (replacing the old copy).
# Usage on the Mac:  ./build.sh          build + install + launch
#                    ./build.sh --no-install
set -euo pipefail
cd "$(dirname "$0")"

# Signing with the same certificate every time keeps the Accessibility permission across rebuilds
# (with ad-hoc signing macOS forgets it after each build). Set AUTOCOPY_SIGN_ID to a code-signing
# identity (see `security find-identity -v -p codesigning`), otherwise the app is signed ad-hoc.
IDENTITY="${AUTOCOPY_SIGN_ID:--}"

APP=build/AutoCopy.app
rm -rf build
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

swiftc -O -swift-version 5 -target arm64-apple-macos13 -o "$APP/Contents/MacOS/AutoCopy" AutoCopy.swift
cp Info.plist "$APP/Contents/Info.plist"

swift make_icon.swift build/AppIcon.iconset
iconutil -c icns build/AppIcon.iconset -o "$APP/Contents/Resources/AppIcon.icns"

if [ "$IDENTITY" = "-" ]; then
    codesign --force --sign - "$APP"
elif ! codesign --force --timestamp=none --sign "$IDENTITY" "$APP" 2>build/codesign.log; then
    echo "WARNING: certificate signing failed ($(cat build/codesign.log)); using ad-hoc signature." >&2
    echo "         Accessibility permission will have to be granted again after each rebuild." >&2
    codesign --force --sign - "$APP"
fi
codesign --verify --strict "$APP"
echo "Built $APP"

# Disk image for sharing: open it, drag AutoCopy onto the Applications shortcut.
VER=$(/usr/libexec/PlistBuddy -c "Print CFBundleShortVersionString" Info.plist)
DMG="build/AutoCopy_mac_${VER}_ODIZH.dmg"
mkdir -p build/dmg && cp -R "$APP" build/dmg/ && ln -s /Applications build/dmg/Applications
hdiutil create -quiet -volname "AutoCopy $VER" -srcfolder build/dmg -ov -format UDZO "$DMG"
rm -rf build/dmg
echo "Built $DMG"

[ "${1:-}" = "--no-install" ] && exit 0

pkill -x AutoCopy 2>/dev/null && sleep 1 || true
rm -rf /Applications/AutoCopy.app
cp -R "$APP" /Applications/
open /Applications/AutoCopy.app
echo "Installed and launched /Applications/AutoCopy.app"
