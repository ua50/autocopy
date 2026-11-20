#!/bin/bash
# Builds AutoCopy.app, signs it and installs it into /Applications (replacing the old copy).
# Usage on the Mac:  ./build.sh          build + install + launch
#                    ./build.sh --no-install
set -euo pipefail
cd "$(dirname "$0")"

# Signing with the same certificate every time keeps the Accessibility permission across rebuilds
# (with ad-hoc signing macOS forgets it after each build). The identity comes from AUTOCOPY_SIGN_ID
# or from a local .signing_identity file next to this script (not in git); see
# `security find-identity -v -p codesigning`. Without one the app is signed ad-hoc.
IDENTITY="${AUTOCOPY_SIGN_ID:-}"
[ -z "$IDENTITY" ] && [ -f .signing_identity ] && IDENTITY="$(tr -d '[:space:]' < .signing_identity)"
IDENTITY="${IDENTITY:--}"

APP=build/AutoCopy.app
rm -rf build
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"

swiftc -O -swift-version 5 -target arm64-apple-macos13 -o "$APP/Contents/MacOS/AutoCopy" AutoCopy.swift
cp Info.plist "$APP/Contents/Info.plist"

swift make_icon.swift build/AppIcon.iconset
iconutil -c icns build/AppIcon.iconset -o "$APP/Contents/Resources/AppIcon.icns"

# Over SSH the login keychain is out of reach (errSecInternalComponent), so codesign runs as a one-shot
# launchd job inside the logged-in user's GUI session. The first time macOS asks for the login password
# on the Mac screen; «Always Allow» makes later builds silent.
sign_in_gui() {
    local label=ua.uran.autocopy.sign
    local plist="$PWD/build/sign.plist" log="$PWD/build/sign.log" app="$PWD/$APP"
    rm -f "$log"
    cat > "$plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
<key>Label</key><string>$label</string>
<key>ProgramArguments</key><array><string>/bin/bash</string><string>-c</string>
<string>/usr/bin/codesign --force --timestamp=none --sign '$IDENTITY' '$app' &gt; '$log.tmp' 2&gt;&amp;1; echo "rc=\$?" &gt;&gt; '$log.tmp'; mv '$log.tmp' '$log'</string></array>
<key>RunAtLoad</key><true/>
</dict></plist>
EOF
    launchctl bootout "gui/$(id -u)/$label" 2>/dev/null || true
    launchctl bootstrap "gui/$(id -u)" "$plist"
    echo "Signing in the GUI session (approve the keychain prompt on the Mac screen if it appears)..."
    for _ in $(seq 1 300); do [ -f "$log" ] && break; sleep 1; done
    launchctl bootout "gui/$(id -u)/$label" 2>/dev/null || true
    rm -f "$plist"
    [ -f "$log" ] && grep -q '^rc=0$' "$log"
}

signed=0
if [ "$IDENTITY" = "-" ]; then
    :
elif [ -n "${SSH_CONNECTION:-}" ]; then
    sign_in_gui && signed=1
else
    codesign --force --timestamp=none --sign "$IDENTITY" "$APP" && signed=1
fi
if [ "$signed" = 0 ]; then
    [ "$IDENTITY" != "-" ] && echo "WARNING: certificate signing failed ($(cat build/sign.log 2>/dev/null)); using ad-hoc signature." >&2
    rm -rf "$APP/Contents/_CodeSignature" # leftovers of an interrupted signing break the ad-hoc seal
    codesign --force --sign - "$APP"
fi
codesign -dvv "$APP" 2>&1 | grep -m1 -E '^Authority=|^Signature=' || true
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
