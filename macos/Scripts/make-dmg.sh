#!/bin/bash
# Package a previously built universal Evict.app as a native drag-and-drop installer.
# The filename follows the combined release version. The app keeps its own Mac version.
#
#   Scripts/make-app.sh --universal
#   Scripts/make-dmg.sh <combined-release-version>
#
# Output: build/Evict_<combined-release-version>.dmg and its SHA-256 sidecar.
set -euo pipefail

cd "$(dirname "$0")/.."
RELEASE_VERSION="${1:-}"
if [[ $# -ne 1 ]]; then
  echo "Usage: Scripts/make-dmg.sh <combined-release-version>" >&2
  exit 2
fi
python3 - "$RELEASE_VERSION" <<'PY'
import re
import sys

version = sys.argv[1]
pattern = (
    r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
    r"(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?"
    r"(?:\+([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?"
)
match = re.fullmatch(pattern, version)
if not match or len(version) > 128:
    raise SystemExit("The combined release version must be a safe full semantic version.")
prerelease = match.group(4)
if prerelease and any(
    item.isdigit() and len(item) > 1 and item.startswith("0")
    for item in prerelease.split(".")
):
    raise SystemExit("Numeric prerelease identifiers must not contain leading zeroes.")
PY

SOURCE_METADATA="$(python3 - <<'PY'
import pathlib
import re

source = pathlib.Path("Sources/EvictKit/Version.swift").read_text(encoding="utf-8")
versions = re.findall(r'public static let current\s*=\s*"([^"]+)"', source)
builds = re.findall(r'public static let build\s*=\s*"([^"]+)"', source)
if len(versions) != 1 or len(builds) != 1:
    raise SystemExit("Cannot determine the Mac component version and build from source.")
if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", versions[0]):
    raise SystemExit("The Mac component version must use major.minor.patch.")
if not re.fullmatch(r"[0-9]+", builds[0]):
    raise SystemExit("The Mac component build must be numeric.")
print(versions[0] + "\t" + builds[0])
PY
)"
IFS=$'\t' read -r MAC_VERSION MAC_BUILD <<< "$SOURCE_METADATA"
APP="$(pwd)/build/Evict.app"
test -d "$APP"
test ! -L "$APP"

verify_bundle() {
  local bundle_path="$1"
  python3 - "$bundle_path" "$MAC_VERSION" "$MAC_BUILD" <<'PY'
import os
import pathlib
import plistlib
import stat
import sys

bundle = pathlib.Path(sys.argv[1])
with (bundle / "Contents/Info.plist").open("rb") as file:
    info = plistlib.load(file)
expected = {
    "CFBundleName": "Evict",
    "CFBundleIdentifier": "app.evict.mac",
    "CFBundleExecutable": "Evict",
    "CFBundlePackageType": "APPL",
    "CFBundleShortVersionString": sys.argv[2],
    "CFBundleVersion": sys.argv[3],
}
for key, value in expected.items():
    if info.get(key) != value:
        raise SystemExit(f"Bundle {key} mismatch: {info.get(key)!r}; expected {value!r}")
executable = bundle / "Contents/MacOS/Evict"
mode = executable.lstat().st_mode
if not stat.S_ISREG(mode) or not mode & 0o111 or not os.access(executable, os.X_OK):
    raise SystemExit("The bundled Evict executable must be a regular executable file.")
PY
  lipo "$bundle_path/Contents/MacOS/Evict" -verify_arch arm64 x86_64
  codesign --verify --deep --strict --verbose=2 "$bundle_path"
}

verify_bundle "$APP"
TASK_TEMP="$(mktemp -d "${TMPDIR:-/tmp}/evict-dmg.XXXXXX")"
STAGING="$TASK_TEMP/staging"
MOUNT_POINT="$TASK_TEMP/mounted"
MOUNT_DEVICE=""
DMG_TEMP="$TASK_TEMP/Evict_$RELEASE_VERSION.dmg"
DMG_OUTPUT="$(pwd)/build/Evict_$RELEASE_VERSION.dmg"

cleanup() {
  local status=$?
  local detach_target="$MOUNT_DEVICE"
  trap - EXIT
  if [[ -z "$detach_target" ]] && python3 - "$MOUNT_POINT" <<'PY'
import os
import sys
raise SystemExit(0 if os.path.ismount(sys.argv[1]) else 1)
PY
  then
    detach_target="$MOUNT_POINT"
  fi
  if [[ -n "$detach_target" ]]; then
    if ! hdiutil detach "$detach_target" >/dev/null 2>&1; then
      if ! hdiutil detach -force "$detach_target" >/dev/null 2>&1; then
        echo "Cannot detach installer verification volume: $detach_target" >&2
        # Never recursively remove a directory that still contains a mounted volume.
        [[ "$status" -ne 0 ]] || status=1
        exit "$status"
      fi
    fi
  fi
  rm -rf "$TASK_TEMP"
  exit "$status"
}
trap cleanup EXIT
trap 'exit 130' INT
trap 'exit 143' TERM

mkdir -p "$STAGING" "$MOUNT_POINT"
ditto "$APP" "$STAGING/Evict.app"
ln -s /Applications "$STAGING/Applications"
echo "==> Creating Evict $RELEASE_VERSION installer (Mac component $MAC_VERSION, build $MAC_BUILD)"
hdiutil create -ov -format UDZO -fs HFS+ -volname "Evict_$RELEASE_VERSION" \
  -srcfolder "$STAGING" "$DMG_TEMP"
hdiutil verify "$DMG_TEMP"
hdiutil imageinfo -plist "$DMG_TEMP" > "$TASK_TEMP/image-info.plist"
python3 - "$TASK_TEMP/image-info.plist" <<'PY'
import pathlib
import plistlib
import sys

info = plistlib.loads(pathlib.Path(sys.argv[1]).read_bytes())
if not isinstance(info, dict) or not info:
    raise SystemExit("Cannot read native disk image metadata.")
image_format = info.get("Format")
if image_format is not None and image_format != "UDZO":
    raise SystemExit(f"Unexpected installer disk image format: {image_format!r}")
PY

hdiutil attach -readonly -nobrowse -noautoopen -owners on -mountpoint "$MOUNT_POINT" \
  -plist "$DMG_TEMP" > "$TASK_TEMP/attach.plist"
MOUNT_DEVICE="$(python3 - "$TASK_TEMP/attach.plist" "$MOUNT_POINT" <<'PY'
import pathlib
import plistlib
import re
import sys

info = plistlib.loads(pathlib.Path(sys.argv[1]).read_bytes())
entities = info.get("system-entities", [])
mounted = [item for item in entities if item.get("mount-point") == sys.argv[2]]
if len(mounted) != 1:
    raise SystemExit("The installer must mount exactly one filesystem at the requested path.")
device = mounted[0].get("dev-entry", "")
if not re.fullmatch(r"/dev/disk[0-9]+(?:s[0-9]+)*", device):
    raise SystemExit("Unexpected installer mount device.")
print(device)
PY
)"
verify_bundle "$MOUNT_POINT/Evict.app"
python3 - "$APP" "$MOUNT_POINT" <<'PY'
import hashlib
import os
import pathlib
import stat
import sys

source = pathlib.Path(sys.argv[1])
volume = pathlib.Path(sys.argv[2])
if not os.statvfs(volume).f_flag & os.ST_RDONLY:
    raise SystemExit("Installer verification must use a read-only mount.")
applications = volume / "Applications"
if not applications.is_symlink() or os.readlink(applications) != "/Applications":
    raise SystemExit("The disk image must contain the Applications drag-and-drop shortcut.")


def manifest(bundle):
    root = bundle.resolve(strict=True)
    result = {}
    for current, directories, files in os.walk(root, followlinks=False):
        for name in sorted(directories + files):
            entry = pathlib.Path(current) / name
            relative = entry.relative_to(root).as_posix()
            mode = entry.lstat().st_mode
            permissions = stat.S_IMODE(mode)
            if stat.S_ISLNK(mode):
                target = os.readlink(entry)
                if pathlib.Path(target).is_absolute() or not entry.resolve(strict=True).is_relative_to(root):
                    raise SystemExit(f"Unsafe app bundle symlink: {relative}")
                result[relative] = ("symlink", permissions, target)
            elif stat.S_ISDIR(mode):
                result[relative] = ("directory", permissions)
            elif stat.S_ISREG(mode):
                result[relative] = ("file", permissions, hashlib.sha256(entry.read_bytes()).hexdigest())
            else:
                raise SystemExit(f"Unsupported app bundle entry: {relative}")
    return result


if manifest(source) != manifest(volume / "Evict.app"):
    raise SystemExit("The installer changed app bytes, symlinks, or executable permissions.")
print("Native installer verified: identical universal signed app, original component version, read-only mount.")
PY
hdiutil detach "$MOUNT_DEVICE"
MOUNT_DEVICE=""
mv -f "$DMG_TEMP" "$DMG_OUTPUT"
(cd build && shasum -a 256 "Evict_$RELEASE_VERSION.dmg" > "Evict_$RELEASE_VERSION.dmg.sha256")
echo "==> Done: $DMG_OUTPUT"
