#!/usr/bin/env python3
"""Validate a native universal ZIP's identity and exact embedded release notes."""
import argparse
import hashlib
import json
from pathlib import Path, PurePosixPath
import plistlib
import re
import stat
import zipfile


def validate_archive(path, version, core, notes):
    if not re.fullmatch(re.escape(core) + r"(?:-beta(?!000)\d{3})?", version):
        raise ValueError("Invalid native release identity")
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)) or len(names) > 20000:
            raise ValueError("Duplicate or excessive ZIP entries")
        for info in archive.infolist():
            p = PurePosixPath(info.filename)
            if p.is_absolute() or ".." in p.parts or "\\" in info.filename or not p.parts or p.parts[0] not in ("Evict.app", "__MACOSX"):
                raise ValueError("Unsafe app ZIP entry")
            mode = info.external_attr >> 16
            if stat.S_ISLNK(mode):
                raise ValueError("This standalone Evict bundle must not contain symbolic links")
        info = plistlib.loads(archive.read("Evict.app/Contents/Info.plist"))
        for key, value in {"CFBundleIdentifier": "app.evict.mac", "CFBundleExecutable": "Evict",
                           "CFBundleShortVersionString": core, "EvictReleaseVersion": version}.items():
            if info.get(key) != value:
                raise ValueError(f"Bundle {key} disagrees with the release")
        binary = archive.getinfo("Evict.app/Contents/MacOS/Evict")
        if not binary.external_attr >> 16 & 0o111 or binary.file_size < 4096:
            raise ValueError("Evict binary is not a native executable")
        if archive.read("Evict.app/Contents/Resources/release-notes.md").decode("utf-8") != notes:
            raise ValueError("Packaged implementation notes do not match the tested release")
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("manifest")
    parser.add_argument("archive")
    args = parser.parse_args()
    manifest = json.loads(Path(args.manifest).read_text())
    plan = manifest["plan"]
    print(validate_archive(args.archive, plan["version"], plan["core_version"], manifest["notes"]))


if __name__ == "__main__":
    main()
