#!/usr/bin/env python3
"""Build and verify Evict_<version>.zip installation downloads.

New downloads contain one Evict_<version>/ root with exactly three payloads:
portable/Evict_<version>.exe, windows-x64/Evict_<version>.exe and
macOS/Evict_<version>.dmg. SHA-256 sidecars remain outside the installation ZIP.

python build/release_zip.py 1.12.1-beta.41.15.1 --setup Installed --exe Portable \
    --macos ../macos/build/Evict.dmg --out Zip
python build/release_zip.py --check Zip/Evict_1.12.1-beta.41.15.1.zip
python build/release_zip.py --check-folder CiDownload

Legacy release readers remain available for importing historical builds.
DMG trailer checks are structural; macOS CI mounts and verifies the native app.
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

FOLDERS = ("portable", "windows-x64", "macOS")
LEGACY_FOLDERS = ("portable", "windows-installer", "macOS")
MAX_PAYLOAD_BYTES = 2 * 1024 * 1024 * 1024
MAX_DMG_METADATA_BYTES = 16 * 1024 * 1024
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
                parts = safe_name(info.orig_filename)
                if info.orig_filename != info.filename:
                    raise ZipError(f"archive path was normalized before validation: {info.orig_filename!r}")
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


def _check_legacy_payloads(names, open_payload, size_payload):
    if len(names) != 6 or len(set(names)) != 6:
        raise ZipError("release ZIP must contain exactly three payload files and their SHA-256 sidecars")
    for name in names:
        parts = safe_name(name)
        if len(parts) != 2 or parts[0] not in LEGACY_FOLDERS:
            raise ZipError(f"release ZIP permits files only inside {', '.join(LEGACY_FOLDERS)}: {name}")
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


def check_dmg(stream, size, label):
    """Check an ordinary, unsegmented UDIF image without extracting its contents."""
    if size < 513 or size > MAX_PAYLOAD_BYTES:
        raise ZipError(f"{label}: invalid macOS disk image size")
    stream.seek(size - 512)
    trailer = stream.read(512)
    if len(trailer) != 512 or trailer[:4] != b"koly":
        raise ZipError(f"{label}: missing macOS UDIF disk image trailer")

    def number(start, length=8):
        return int.from_bytes(trailer[start:start + length], "big")

    fields = {
        "version": number(4, 4), "header_size": number(8, 4),
        "flags": number(12, 4), "running_data_offset": number(16),
        "data_offset": number(24), "data_length": number(32),
        "resource_offset": number(40), "resource_length": number(48),
        "segment_number": number(56, 4), "segment_count": number(60, 4),
        "xml_offset": number(216), "xml_length": number(224),
        "image_variant": number(488, 4), "sector_count": number(492),
        "file_size": size,
    }
    details = ", ".join(f"{key}={value}" for key, value in fields.items())
    if fields["version"] != 4 or fields["header_size"] != 512:
        raise ZipError(f"{label}: unsupported macOS UDIF header ({details})")
    # Standalone UDIF writers can leave either segment field at zero.
    # VirtualBox validates segment count <= 1 and number in {0, 1} independently:
    # https://github.com/mirror/vbox/blob/master/src/VBox/Storage/DMG.cpp
    # A later segment, multiple segments, or an aggregate data offset still fails.
    if (
        fields["segment_number"] not in (0, 1)
        or fields["segment_count"] not in (0, 1)
        or fields["running_data_offset"] != 0
    ):
        raise ZipError(f"{label}: segmented macOS disk images are not supported ({details})")
    boundary = size - 512
    data_offset, data_length = fields["data_offset"], fields["data_length"]
    resource_offset, resource_length = fields["resource_offset"], fields["resource_length"]
    xml_offset, xml_length = fields["xml_offset"], fields["xml_length"]
    if (
        not 0 < data_length <= boundary
        or data_offset + data_length > boundary
        or not 0 < xml_length <= MAX_DMG_METADATA_BYTES
        or xml_offset < data_offset + data_length
        or xml_offset + xml_length > boundary
        or (resource_length and resource_offset + resource_length > boundary)
        or fields["sector_count"] == 0
    ):
        raise ZipError(f"{label}: invalid macOS UDIF data or metadata bounds ({details})")
    stream.seek(xml_offset)
    try:
        metadata = plistlib.loads(stream.read(xml_length))
    except (ValueError, plistlib.InvalidFileException, ExpatError) as error:
        raise ZipError(f"{label}: invalid macOS UDIF metadata ({details})") from error
    if not isinstance(metadata, dict) or not isinstance(metadata.get("resource-fork"), dict):
        raise ZipError(f"{label}: macOS UDIF metadata lacks a resource dictionary ({details})")
    blocks = metadata["resource-fork"].get("blkx")
    if not isinstance(blocks, list) or not blocks:
        raise ZipError(f"{label}: macOS UDIF metadata lacks image block descriptors ({details})")


def installation_root(version):
    if not isinstance(version, str) or re.fullmatch(VERSION, version) is None:
        raise ZipError("Installation version must be x.y.z or a complete x.y.z-beta.RUN.PR.ATTEMPT version")
    return f"Evict_{version}"


def _check_installation_payloads(names, open_payload, size_payload):
    if len(names) != 3 or len(set(names)) != 3:
        raise ZipError("installation ZIP must contain exactly three payload files")
    parts = [safe_name(name) for name in names]
    roots = {path[0] for path in parts}
    if len(roots) != 1 or any(len(path) != 3 for path in parts):
        raise ZipError("installation payloads must share one versioned root and three platform folders")
    root = next(iter(roots))
    version = root.removeprefix("Evict_")
    if root != installation_root(version):
        raise ZipError("installation root must be Evict_<full-version>")
    payloads = (
        f"{root}/portable/Evict_{version}.exe",
        f"{root}/windows-x64/Evict_{version}.exe",
        f"{root}/macOS/Evict_{version}.dmg",
    )
    if set(names) != set(payloads):
        raise ZipError("installation ZIP contains missing, extra or incorrectly named files")
    for payload in payloads:
        if not 0 < size_payload(payload) <= MAX_PAYLOAD_BYTES:
            raise ZipError(f"empty or excessively large payload: {payload}")
    for payload in payloads[:2]:
        with open_payload(payload) as stream:
            check_pe(stream, size_payload(payload), payload)
    with open_payload(payloads[2]) as stream:
        check_dmg(stream, size_payload(payloads[2]), payloads[2])
    return root


def check_zip(file):
    try:
        with zipfile.ZipFile(file) as archive:
            infos = archive.infolist()
            for info in infos:
                safe_name(info.orig_filename)
                if info.orig_filename != info.filename:
                    raise ZipError(f"archive path was normalized before validation: {info.orig_filename!r}")
                if info.is_dir() or stat.S_IFMT(info.external_attr >> 16) not in (0, stat.S_IFREG):
                    raise ZipError(f"outer release ZIP must contain ordinary payload files only: {info.filename}")
            names = [info.filename for info in infos]
            if names and any(name.split("/", 1)[0].startswith("Evict_") for name in names):
                root = _check_installation_payloads(
                    names, archive.open, lambda name: archive.getinfo(name).file_size,
                )
                if isinstance(file, (str, os.PathLike)) and Path(file).name != f"{root}.zip":
                    raise ZipError("installation ZIP filename must match its versioned root")
            else:
                _check_legacy_payloads(
                    names, archive.open, lambda name: archive.getinfo(name).file_size,
                )
            corrupt = archive.testzip()
            if corrupt is not None:
                raise ZipError(f"corrupt release ZIP member: {corrupt}")
    except (OSError, zipfile.BadZipFile, RuntimeError) as error:
        raise ZipError(f"invalid release ZIP: {error}") from error


def _folder_payloads(folder, folders, prefix=""):
    found = sorted(path.name for path in folder.iterdir())
    if found != sorted(folders) or any(
        (folder / name).is_symlink() or not (folder / name).is_dir() for name in found
    ):
        raise ZipError(f"{folder}: platform folders must be exactly {', '.join(folders)}; found {found}")
    names = []
    for name in folders:
        for path in (folder / name).iterdir():
            regular_file(path)
            names.append(f"{prefix}{name}/{path.name}")
    return names


def check_folder(folder):
    folder = Path(folder)
    try:
        if folder.is_symlink() or not folder.is_dir():
            raise ZipError(f"expected an ordinary unpacked directory: {folder}")
        found = sorted(path.name for path in folder.iterdir())
        if found == sorted(LEGACY_FOLDERS):
            names = _folder_payloads(folder, LEGACY_FOLDERS)
            _check_legacy_payloads(
                names, lambda name: (folder / name).open("rb"),
                lambda name: (folder / name).stat().st_size,
            )
            return
        if folder.name.startswith("Evict_") and found == sorted(FOLDERS):
            root_folder = folder
            base = folder.parent
        else:
            if len(found) != 1 or not found[0].startswith("Evict_"):
                raise ZipError("unpacked installation must contain exactly one Evict_<full-version> root")
            root_folder = folder / found[0]
            base = folder
        if root_folder.is_symlink() or not root_folder.is_dir():
            raise ZipError("installation root must be an ordinary directory")
        names = _folder_payloads(root_folder, FOLDERS, root_folder.name + "/")
        _check_installation_payloads(
            names, lambda name: (base / name).open("rb"),
            lambda name: (base / name).stat().st_size,
        )
    except OSError as error:
        raise ZipError(f"invalid unpacked release folder: {error}") from error

def make_zip(version, setup_dir, exe_dir, out_dir, macos_archive, archive_version=None):
    root = installation_root(version)
    if archive_version is not None and archive_version != version:
        raise ZipError("Archive version must match the full Windows installation version")
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
    with macos.open("rb") as stream:
        check_dmg(stream, macos.stat().st_size, str(macos))
    destinations = (
        (f"{root}/portable/Evict_{version}.exe", exe),
        (f"{root}/windows-x64/Evict_{version}.exe", setup),
        (f"{root}/macOS/Evict_{version}.dmg", macos),
    )
    output_dir = Path(out_dir)
    output_dir.mkdir(parents=True, exist_ok=True)
    output = output_dir / f"{root}.zip"
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
    parser.add_argument("--macos", help="required verified native macOS UDIF .dmg (made on a Mac)")
    parser.add_argument("--out", help="folder for Evict_<version>.zip")
    parser.add_argument("--archive-version", help="optional compatibility argument; must equal the full installation version")
    parser.add_argument("--check", metavar="ZIP", help="verify an existing cross-platform release ZIP")
    parser.add_argument("--check-folder", metavar="DIR", help="verify an unpacked cross-platform release ZIP")
    args = parser.parse_args(argv)
    try:
        if args.check_folder:
            check_folder(args.check_folder)
            print(f"{args.check_folder}: installation folders and native payloads verified")
        elif args.check:
            check_zip(args.check)
            print(f"{args.check}: installation folders and native payloads verified")
        elif args.version and args.setup and args.exe and args.out and args.macos:
            print(make_zip(args.version, args.setup, args.exe, args.out, args.macos, args.archive_version))
        else:
            parser.error("give <version> --setup --exe --macos <native DMG> --out, or --check ZIP; empty macOS placeholders are not allowed")
    except (ZipError, OSError) as error:
        print("release_zip: " + str(error), file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
