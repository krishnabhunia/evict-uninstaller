#!/bin/bash
# Builds Evict.app from the Swift package. Runs on macOS only (needs swift + iconutil).
#
#   Scripts/make-app.sh [--universal]
#
# Output: build/Evict.app  and  build/Evict-<version>.zip
set -euo pipefail

cd "$(dirname "$0")/.."
VERSION="$(grep -m1 'public static let current' Sources/EvictKit/Version.swift | sed 's/.*"\(.*\)".*/\1/')"
BUILD_NUMBER="$(grep -m1 'public static let build' Sources/EvictKit/Version.swift | sed 's/.*"\(.*\)".*/\1/')"
APP="build/Evict.app"
ARCH_FLAGS=""
if [[ "${1:-}" == "--universal" ]]; then
  ARCH_FLAGS="--arch arm64 --arch x86_64"
fi

echo "==> Building Evict $VERSION ($BUILD_NUMBER)"
swift build -c release --product Evict $ARCH_FLAGS

BIN="$(swift build -c release --product Evict $ARCH_FLAGS --show-bin-path)/Evict"
test -x "$BIN" || { echo "Binary not found at $BIN"; exit 1; }

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp "$BIN" "$APP/Contents/MacOS/Evict"

if command -v iconutil >/dev/null 2>&1; then
  iconutil -c icns Resources/AppIcon.iconset -o "$APP/Contents/Resources/AppIcon.icns"
else
  echo "    (iconutil not available – the app will use the default icon)"
fi

sed -e "s/@VERSION@/$VERSION/g" -e "s/@BUILD@/$BUILD_NUMBER/g" Resources/Info.plist > "$APP/Contents/Info.plist"

# Ad-hoc signature. With a Developer ID in the keychain, set SIGN_IDENTITY to use it instead.
IDENTITY="${SIGN_IDENTITY:--}"
echo "==> Signing with identity: $IDENTITY"
codesign --force --deep --options runtime --sign "$IDENTITY" "$APP" 2>/dev/null || codesign --force --deep --sign "$IDENTITY" "$APP"
codesign --verify --verbose=2 "$APP" || true

(cd build && rm -f "Evict-$VERSION.zip" && ditto -c -k --sequesterRsrc --keepParent Evict.app "Evict-$VERSION.zip")
shasum -a 256 "build/Evict-$VERSION.zip" | tee "build/Evict-$VERSION.zip.sha256"
echo "==> Done: $APP"
