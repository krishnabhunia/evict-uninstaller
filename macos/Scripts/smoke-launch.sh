#!/bin/bash
# Proves that a built Evict.app really opens on this Mac: started through LaunchServices (the same
# path as a double-click in the Finder), it must show a window and still be running a few seconds
# later. A crash at launch, a missing window or an app that quits by itself fails the check.
#
#   Scripts/smoke-launch.sh <path/to/Evict.app> [seconds-to-stay-up]
#
# CI runs it on the freshly built app and again on the copy taken from the published DMG.
# It never touches the user's data: the app only lists applications while it is open, and it is
# closed with SIGTERM at the end.
set -euo pipefail

APP="${1:-}"
STAY_UP="${2:-8}"
if [[ -z "$APP" || ! -d "$APP" ]]; then
  echo "Usage: Scripts/smoke-launch.sh <path/to/Evict.app> [seconds-to-stay-up]" >&2
  exit 2
fi
APP="$(cd "$APP" && pwd -P)"
EXECUTABLE="$APP/Contents/MacOS/Evict"
test -x "$EXECUTABLE"

WORK="$(mktemp -d "${TMPDIR:-/tmp}/evict-launch.XXXXXX")"
REPORTS="$HOME/Library/Logs/DiagnosticReports"
PID=""

cleanup() {
  local status=$?
  trap - EXIT
  if [[ -n "$PID" ]] && kill -0 "$PID" 2>/dev/null; then
    kill -TERM "$PID" 2>/dev/null || true
    for _ in $(seq 1 20); do kill -0 "$PID" 2>/dev/null || break; sleep 0.25; done
    kill -KILL "$PID" 2>/dev/null || true
  fi
  rm -rf "$WORK"
  exit "$status"
}
trap cleanup EXIT

fail() {
  echo "::error::Evict did not open: $1"
  echo "---- app output ----"
  cat "$WORK/stdout.txt" "$WORK/stderr.txt" 2>/dev/null | tail -n 60 || true
  echo "---- system log (last 2 minutes) ----"
  log show --last 2m --style compact --predicate 'process == "Evict" OR eventMessage CONTAINS[c] "app.evict.mac"' 2>/dev/null | tail -n 80 || true
  echo "---- crash reports ----"
  find "$REPORTS" -maxdepth 1 -name 'Evict*' -newer "$WORK/started" -print -exec head -n 60 {} \; 2>/dev/null || true
  exit 1
}

# Counts the app's normal (layer 0) windows. No screen-recording permission is needed for this.
cat > "$WORK/windows.swift" <<'SWIFT'
import CoreGraphics
import Foundation

let pid = Int32(CommandLine.arguments[1]) ?? -1
let windows = CGWindowListCopyWindowInfo([.optionAll], kCGNullWindowID) as? [[String: Any]] ?? []
let count = windows.filter {
    ($0[kCGWindowOwnerPID as String] as? NSNumber)?.int32Value == pid
        && ($0[kCGWindowLayer as String] as? NSNumber)?.intValue == 0
}.count
print(count)
SWIFT
xcrun swiftc -O -o "$WORK/windows" "$WORK/windows.swift"

pkill -x Evict 2>/dev/null || true
sleep 1
touch "$WORK/started"
echo "==> Opening $APP"
open -n --stdout "$WORK/stdout.txt" --stderr "$WORK/stderr.txt" "$APP" || fail "LaunchServices refused to open the bundle"

for _ in $(seq 1 60); do
  PID="$(pgrep -nx Evict || true)"
  [[ -n "$PID" ]] && break
  sleep 0.5
done
[[ -n "$PID" ]] || fail "no Evict process started within 30 seconds"
echo "    process $PID started"

windows=0
for _ in $(seq 1 60); do
  kill -0 "$PID" 2>/dev/null || fail "the process quit before showing a window"
  windows="$("$WORK/windows" "$PID" 2>/dev/null || echo 0)"
  [[ "$windows" =~ ^[0-9]+$ && "$windows" -gt 0 ]] && break
  sleep 0.5
done
[[ "$windows" =~ ^[0-9]+$ && "$windows" -gt 0 ]] || fail "no window appeared within 30 seconds"
echo "    window shown"

sleep "$STAY_UP"
kill -0 "$PID" 2>/dev/null || fail "the app quit within $STAY_UP seconds of opening"
if find "$REPORTS" -maxdepth 1 -name 'Evict*' -newer "$WORK/started" 2>/dev/null | grep -q .; then
  fail "a crash report was written"
fi
echo "==> Evict opened, showed its window and stayed up for $STAY_UP seconds"
