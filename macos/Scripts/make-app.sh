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
RELEASE_VERSION="${EVICT_RELEASE_VERSION:-$VERSION}"
python3 - "$VERSION" "$RELEASE_VERSION" <<'PY'
import re, sys
core, release = sys.argv[1:]
if not re.fullmatch(r'(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)', core):
    raise SystemExit('Invalid native core version')
if not re.fullmatch(re.escape(core) + r'(?:-beta(?!000)\d{3})?', release):
    raise SystemExit('Release identity must use the native core and an optional three-digit beta')
PY
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

sed -e "s/@VERSION@/$VERSION/g" -e "s/@BUILD@/$BUILD_NUMBER/g" -e "s/@RELEASE_VERSION@/$RELEASE_VERSION/g" Resources/Info.plist > "$APP/Contents/Info.plist"
cp Resources/release-notes.md "$APP/Contents/Resources/release-notes.md"

# Ad-hoc signature. With a Developer ID in the keychain, set SIGN_IDENTITY to use it instead.
IDENTITY="${SIGN_IDENTITY:--}"
echo "==> Signing with identity: $IDENTITY"
if [[ "$IDENTITY" == "-" ]]; then
  codesign --force --deep --options runtime --sign - "$APP"
else
  # Notarization needs the hardened runtime and a secure timestamp from Apple.
  codesign --force --deep --options runtime --timestamp --sign "$IDENTITY" "$APP"
fi
# A bundle whose signature doesn't verify is refused by macOS ("damaged"), so this must not be skipped.
codesign --verify --deep --strict --verbose=2 "$APP"

(cd build && rm -f "Evict-$RELEASE_VERSION.zip" && ditto -c -k --sequesterRsrc --keepParent Evict.app "Evict-$RELEASE_VERSION.zip")
(cd build && shasum -a 256 "Evict-$RELEASE_VERSION.zip" > "Evict-$RELEASE_VERSION.zip.sha256")
echo "==> Done: $APP"
