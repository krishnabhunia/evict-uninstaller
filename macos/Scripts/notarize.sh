#!/bin/bash
# Signs, notarizes and staples a finished Evict DMG so a downloaded copy opens with a normal
# double-click (no "Apple could not verify…" block). Needs a Developer ID Application identity in
# the keychain (SIGN_IDENTITY) and an Apple ID app-specific password for the notary service.
#
#   SIGN_IDENTITY="Developer ID Application: Name (TEAMID)" \
#   APPLE_ID=… APPLE_TEAM_ID=… APPLE_APP_PASSWORD=… \
#   Scripts/notarize.sh build/Evict_<version>.dmg
#
# The app inside the DMG must already be signed with the same identity, hardened runtime and a
# secure timestamp (Scripts/make-app.sh does that when SIGN_IDENTITY is set). Stapling changes the
# DMG's bytes, so the .sha256 sidecar is written again at the end.
set -euo pipefail

DMG="${1:-}"
if [[ -z "$DMG" || ! -f "$DMG" ]]; then
  echo "Usage: Scripts/notarize.sh <path/to/Evict_<version>.dmg>" >&2
  exit 2
fi
: "${SIGN_IDENTITY:?SIGN_IDENTITY must name a Developer ID Application identity}"
: "${APPLE_ID:?}" "${APPLE_TEAM_ID:?}" "${APPLE_APP_PASSWORD:?}"
if [[ "$SIGN_IDENTITY" == "-" ]]; then
  echo "Notarization needs a Developer ID identity, not an ad-hoc signature." >&2
  exit 2
fi

echo "==> Signing the disk image"
codesign --force --timestamp --sign "$SIGN_IDENTITY" "$DMG"
codesign --verify --strict --verbose=2 "$DMG"

echo "==> Sending it to Apple's notary service (this usually takes a few minutes)"
RESULT="$(mktemp "${TMPDIR:-/tmp}/evict-notary.XXXXXX")"
trap 'rm -f "$RESULT"' EXIT
xcrun notarytool submit "$DMG" \
  --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" \
  --wait --timeout 30m --output-format json > "$RESULT"
STATUS="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("status",""))' "$RESULT")"
SUBMISSION="$(python3 -c 'import json,sys; print(json.load(open(sys.argv[1])).get("id",""))' "$RESULT")"
if [[ "$STATUS" != "Accepted" ]]; then
  echo "::error::Notarization ended with status '$STATUS'"
  [[ -n "$SUBMISSION" ]] && xcrun notarytool log "$SUBMISSION" \
    --apple-id "$APPLE_ID" --team-id "$APPLE_TEAM_ID" --password "$APPLE_APP_PASSWORD" || true
  exit 1
fi

echo "==> Stapling the ticket"
xcrun stapler staple "$DMG"
xcrun stapler validate "$DMG"
spctl --assess --type open --context context:primary-signature -vv "$DMG"

(cd "$(dirname "$DMG")" && shasum -a 256 "$(basename "$DMG")" > "$(basename "$DMG").sha256")
echo "==> Notarized: $DMG"
