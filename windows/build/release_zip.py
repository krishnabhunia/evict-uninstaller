#!/usr/bin/env python3
"""Build a release ZIP with portable/, windows-installer/ and macOS/ only.

python build/release_zip.py 1.11.0-beta.32.13.1 --setup Installed --exe Portable \
    --macos ../macos/build/Evict-0.2.0.zip --out Zip
python build/release_zip.py --check Zip/Evict-1.11.0-beta.32.13.1.zip
python build/release_zip.py --check-folder CiDownload

The macOS payload must be a built Evict.app ZIP, not an empty placeholder.
Its original ZIP bytes preserve executable permissions and bundle symlinks.
Python standard library only.
"""
import argparse
from collections import deque
import hashlib
import os
from pathlib import Path, PurePosixPath
import plistlib
import re
import stat
import sys
import tempfile
import zipfile
from xml.parsers.expat import ExpatError

FOLDERS = ("portable", "windows-installer", "macOS")
CORE_VERSION = r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"
VERSION = CORE_VERSION + r"(?:-beta\.[1-9][0-9]*\.[1-9][0-9]*\.[1-9][0-9]*)?"
MACHO_MAGICS = {
    b"\xfe\xed\xfa\xce", b"\xce\xfa\xed\xfe",
    b"\xfe\xed\xfa\xcf", b"\xcf\xfa\xed\xfe",
    b"\xca\xfe\xba\xbe", b"\xbe\xba\xfe\xca",
    b"\xca\xfe\xba\xbf", b"\xbf\xba\xfe\xca",
}


class ZipError(Exception):
    pass


def sha256_stream(stream):
    digest = hashlib.sha256()
    for block in iter(lambda: stream.read(1 << 20), b""):
        digest.update(block)
    return digest.hexdigest().upper()


def sha256_file(file):
    with open(file, "rb") as stream:
        return sha256_stream(stream)


def safe_name(name):
    """Reject paths that can escape the destination on either supported OS."""
    if not name or "\\" in name or name.startswith("/") or ":" in name:
        raise ZipError(f"unsafe archive path: {name!r}")
    parts = name.rstrip("/").split("/")
    if any(part in ("", ".", "..") or any(ord(c) < 32 for c in part) for part in parts):
        raise ZipError(f"unsafe archive path: {name!r}")
    return parts


def regular_file(path):
    path = Path(path)
    if not path.is_file() or path.is_symlink() or path.stat().st_size == 0:
        raise ZipError(f"expected a nonempty ordinary file: {path}")
    return path


def check_pe(stream, size, label):
    header = stream.read(64)
    if len(header) != 64 or header[:2] != b"MZ":
        raise ZipError(f"{label}: not a Windows executable")
    offset = int.from_bytes(header[60:64], "little")
    if offset < 64 or offset + 4 > size:
        raise ZipError(f"{label}: invalid Windows executable header")
    stream.seek(offset)
    if stream.read(4) != b"PE\0\0":
        raise ZipError(f"{label}: missing Windows PE signature")


def _safe_bundle_link(info, target):
    if not target or target.startswith("/") or "\\" in target or ":" in target or any(ord(c) < 32 for c in target):
        raise ZipError(f"unsafe macOS bundle symlink: {info.filename}")
    parts = list(PurePosixPath(info.filename).parent.parts)
    for part in target.split("/"):
        if part == "..":
            if len(parts) <= 1:
                raise ZipError(f"macOS bundle symlink escapes Evict.app: {info.filename}")
            parts.pop()
        elif part not in ("", "."):
            parts.append(part)
    if not parts or parts[0] != "Evict.app":
        raise ZipError(f"macOS bundle symlink escapes Evict.app: {info.filename}")


def _validate_bundle_links(links, names):
    """Resolve every bundle path through symlink ancestors before accepting it."""
    def resolve(name):
        remaining = deque(name.rstrip("/").split("/"))
        resolved = []
        expansions = 0
        while remaining:
            part = remaining.popleft()
            if part in ("", "."):
                continue
            if part == "..":
                if len(resolved) <= 1:
                    raise ZipError(f"macOS bundle symlink graph escapes Evict.app: {name}")
                resolved.pop()
                continue
            resolved.append(part)
            current = "/".join(resolved)
            if current in links:
                expansions += 1
                if expansions > 64:
                    raise ZipError(f"cyclic or excessive macOS bundle symlinks: {name}")
                resolved.pop()
                remaining.extendleft(reversed(links[current].split("/")))
        if not resolved or resolved[0] != "Evict.app":
            raise ZipError(f"macOS bundle symlink graph escapes Evict.app: {name}")
        return "/".join(resolved)

    for name in names:
        if name.rstrip("/").split("/")[0] == "Evict.app":
            resolve(name)
    for required in ("Evict.app/Contents/Info.plist", "Evict.app/Contents/MacOS/Evict"):
        if resolve(required) != required:
            raise ZipError(f"required macOS bundle path is redirected by a symlink: {required}")


def check_macos_archive(file):
    """Check a native app archive without extracting or altering bundle files."""
    try:
        with zipfile.ZipFile(file) as archive:
            infos = archive.infolist()
            names = set()
            paths = set()
            links = {}
            directories = {}
            for info in infos:
                parts = safe_name(info.filename)
                path = info.filename.rstrip("/")
                if path in paths:
                    raise ZipError(f"duplicate macOS archive entry: {info.filename}")
                paths.add(path)
                names.add(info.filename)
                if parts[0] != "Evict.app":
                    # ditto --sequesterRsrc stores AppleDouble resource metadata here.
                    if parts[0] != "__MACOSX" or (
                        len(parts) > 1 and parts[1] not in ("Evict.app", "._Evict.app")
                    ):
                        raise ZipError(f"macOS archive contains an unrelated root: {info.filename}")
                mode = info.external_attr >> 16
                kind = stat.S_IFMT(mode)
                if kind not in (0, stat.S_IFREG, stat.S_IFDIR, stat.S_IFLNK):
                    raise ZipError(f"unsupported macOS archive entry type: {info.filename}")
                directories[path] = info.is_dir() or kind == stat.S_IFDIR
                if kind == stat.S_IFLNK:
                    if parts[0] != "Evict.app" or info.file_size > 4096:
                        raise ZipError(f"invalid macOS bundle symlink: {info.filename}")
                    try:
                        target = archive.read(info).decode("utf-8")
                    except UnicodeError as error:
                        raise ZipError(f"invalid macOS bundle symlink: {info.filename}") from error
                    _safe_bundle_link(info, target)
                    links[path] = target
            _validate_bundle_links(links, names)
            for parent in ("Evict.app", "Evict.app/Contents", "Evict.app/Contents/MacOS"):
                if parent in directories and not directories[parent]:
                    raise ZipError(f"macOS bundle parent must be an ordinary directory: {parent}")
            required = ("Evict.app/Contents/Info.plist", "Evict.app/Contents/MacOS/Evict")
            if any(name not in names for name in required):
                raise ZipError("macOS archive must contain a built Evict.app, including Info.plist and its executable")
            plist_info = archive.getinfo(required[0])
            executable = archive.getinfo(required[1])
            if plist_info.is_dir() or plist_info.file_size > 256 * 1024 or stat.S_IFMT(plist_info.external_attr >> 16) not in (0, stat.S_IFREG):
                raise ZipError("macOS Info.plist must be an ordinary small file")
            try:
                metadata = plistlib.loads(archive.read(plist_info))
            except (ValueError, plistlib.InvalidFileException, ExpatError) as error:
                raise ZipError("invalid macOS Info.plist") from error
            if not isinstance(metadata, dict):
                raise ZipError("macOS Info.plist must contain a dictionary")
            version = metadata.get("CFBundleShortVersionString")
            if not isinstance(version, str) or re.fullmatch(CORE_VERSION, version) is None:
                raise ZipError("macOS bundle version must be numeric x.y.z")
            if metadata.get("CFBundleExecutable") != "Evict":
                raise ZipError("macOS bundle executable must be Evict")
            if executable.is_dir() or executable.file_size < 32 or stat.S_IFMT(executable.external_attr >> 16) not in (0, stat.S_IFREG):
                raise ZipError("macOS Evict executable must be a nonempty ordinary binary")
            if not (executable.external_attr >> 16) & 0o111:
                raise ZipError("macOS Evict executable has lost its executable permissions")
            with archive.open(executable) as stream:
                if stream.read(4) not in MACHO_MAGICS:
                    raise ZipError("macOS Evict payload is not a Mach-O binary")
            corrupt = archive.testzip()
            if corrupt is not None:
                raise ZipError(f"corrupt macOS archive member: {corrupt}")
            return version
    except (OSError, zipfile.BadZipFile, KeyError, RuntimeError) as error:
        raise ZipError(f"invalid macOS app ZIP: {error}") from error


def _check_payloads(names, open_payload, size_payload):
    if len(names) != 6 or len(set(names)) != 6:
        raise ZipError("release ZIP must contain exactly three payload files and their SHA-256 sidecars")
    for name in names:
        parts = safe_name(name)
        if len(parts) != 2 or parts[0] not in FOLDERS:
            raise ZipError(f"release ZIP permits files only inside {', '.join(FOLDERS)}: {name}")
    setup_names = [name for name in names if re.fullmatch(
        "windows-installer/Evict-Setup-(" + VERSION + r")\.exe", name
    )]
    mac_names = [name for name in names if re.fullmatch(
        "macOS/Evict-macOS-(" + CORE_VERSION + r")\.zip", name
    )]
    if len(setup_names) != 1 or len(mac_names) != 1:
        raise ZipError("release ZIP must contain one versioned Windows installer and one versioned macOS app ZIP")
    payloads = ["portable/Evict.exe", setup_names[0], mac_names[0]]
    if set(names) != {name for payload in payloads for name in (payload, payload + ".sha256")}:
        raise ZipError("release ZIP contains missing, extra or incorrectly named files")
    for payload in payloads:
        if size_payload(payload) <= 0 or size_payload(payload + ".sha256") > 128:
            raise ZipError(f"empty payload or invalid checksum sidecar: {payload}")
        with open_payload(payload + ".sha256") as stream:
            try:
                expected = stream.read(129).decode("ascii").strip().upper()
            except UnicodeError as error:
                raise ZipError(f"invalid checksum sidecar: {payload}") from error
        if re.fullmatch(r"[0-9A-F]{64}", expected) is None:
            raise ZipError(f"invalid checksum sidecar: {payload}")
        with open_payload(payload) as stream:
            actual = sha256_stream(stream)
        if actual != expected:
            raise ZipError(f"checksum mismatch: {payload}")
    for payload in payloads[:2]:
        with open_payload(payload) as stream:
            check_pe(stream, size_payload(payload), payload)
    with open_payload(mac_names[0]) as stream:
        version = check_macos_archive(stream)
    if mac_names[0] != f"macOS/Evict-macOS-{version}.zip":
        raise ZipError("macOS archive name and bundle version differ")


def check_zip(file):
    try:
        with zipfile.ZipFile(file) as archive:
            infos = archive.infolist()
            for info in infos:
                if info.is_dir() or stat.S_IFMT(info.external_attr >> 16) not in (0, stat.S_IFREG):
                    raise ZipError(f"outer release ZIP must contain ordinary payload files only: {info.filename}")
            _check_payloads(
                [info.filename for info in infos],
                archive.open,
                lambda name: archive.getinfo(name).file_size,
            )
    except (OSError, zipfile.BadZipFile, RuntimeError) as error:
        raise ZipError(f"invalid release ZIP: {error}") from error


def check_folder(folder):
    folder = Path(folder)
    try:
        found = sorted(path.name for path in folder.iterdir())
        if found != sorted(FOLDERS) or any((folder / name).is_symlink() or not (folder / name).is_dir() for name in found):
            raise ZipError(f"{folder}: top level must be exactly {', '.join(FOLDERS)}; found {found}")
        names = []
        for name in FOLDERS:
            for path in (folder / name).iterdir():
                regular_file(path)
                names.append(f"{name}/{path.name}")
        _check_payloads(
            names,
            lambda name: (folder / name).open("rb"),
            lambda name: (folder / name).stat().st_size,
        )
    except OSError as error:
        raise ZipError(f"invalid unpacked release folder: {error}") from error


def make_zip(version, setup_dir, exe_dir, out_dir, macos_archive, archive_version=None):
    if re.fullmatch(VERSION, version) is None:
        raise ZipError("Windows version must be x.y.z or a complete x.y.z-beta.RUN.PR.ATTEMPT version")
    archive_version = version if archive_version is None else archive_version
    if re.fullmatch(VERSION, archive_version) is None:
        raise ZipError("Archive version must be x.y.z or a complete beta version")
    setups = sorted(Path(setup_dir).glob("*.exe"))
    expected_setup = f"Evict-Setup-{version}.exe"
    if len(setups) != 1 or setups[0].name != expected_setup:
        raise ZipError(f"expected exactly {expected_setup} in {setup_dir}")
    setup = regular_file(setups[0])
    exe = regular_file(Path(exe_dir) / "Evict.exe")
    macos = regular_file(macos_archive)
    for payload in (setup, exe):
        with payload.open("rb") as stream:
            check_pe(stream, payload.stat().st_size, str(payload))
    mac_version = check_macos_archive(macos)
    destinations = (
        ("portable/Evict.exe", exe),
        (f"windows-installer/{expected_setup}", setup),
        (f"macOS/Evict-macOS-{mac_version}.zip", macos),
    )
    output_dir = Path(out_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    output = output_dir / f"Evict-{archive_version}.zip"
    if output.is_symlink() or (output.exists() and not output.is_file()):
        raise ZipError(f"refusing an unsafe release ZIP output: {output}")
    sidecar = Path(str(output) + ".sha256")
    if sidecar.is_symlink() or (sidecar.exists() and not sidecar.is_file()):
        raise ZipError(f"refusing an unsafe checksum output: {sidecar}")
    # Validate a temporary file before replacing a previous local build.
    with tempfile.TemporaryDirectory(prefix=".evict-package-", dir=output_dir) as temporary:
        staged = Path(temporary) / output.name
        with zipfile.ZipFile(staged, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, payload in destinations:
                archive.write(payload, name)
                archive.writestr(name + ".sha256", sha256_file(payload) + "\n")
        check_zip(staged)
        digest = sha256_file(staged)
        staged_sidecar = Path(temporary) / sidecar.name
        staged_sidecar.write_text(digest + "\n", encoding="ascii")
        os.replace(staged, output)
        os.replace(staged_sidecar, sidecar)
    return str(output)


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("version", nargs="?")
    parser.add_argument("--setup", help="folder with exactly Evict-Setup-<version>.exe")
    parser.add_argument("--exe", help="folder with Evict.exe")
    parser.add_argument("--macos", help="required built macOS Evict.app ZIP (made on a Mac)")
    parser.add_argument("--out", help="folder for Evict-<version>.zip")
    parser.add_argument("--archive-version", help="optional outer ZIP version (Mac delivery); component versions remain independent")
    parser.add_argument("--check", metavar="ZIP", help="verify an existing cross-platform release ZIP")
    parser.add_argument("--check-folder", metavar="DIR", help="verify an unpacked cross-platform release ZIP")
    args = parser.parse_args(argv)
    try:
        if args.check_folder:
            check_folder(args.check_folder)
            print(f"{args.check_folder}: portable/, windows-installer/ and macOS/ verified")
        elif args.check:
            check_zip(args.check)
            print(f"{args.check}: portable/, windows-installer/ and macOS/ verified")
        elif args.version and args.setup and args.exe and args.out and args.macos:
            print(make_zip(args.version, args.setup, args.exe, args.out, args.macos, args.archive_version))
        else:
            parser.error("give <version> --setup --exe --macos <built Evict.app ZIP> --out, or --check ZIP; empty macOS placeholders are not allowed")
    except (ZipError, OSError) as error:
        print("release_zip: " + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
