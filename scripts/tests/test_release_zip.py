"""Versioned installation ZIPs, native payload safety and historical compatibility."""
import contextlib
import hashlib
import importlib.util
import io
from pathlib import Path
import plistlib
import stat
import tempfile
import unittest
import warnings
import zipfile

MODULE_PATH = Path(__file__).resolve().parents[2] / "windows" / "build" / "release_zip.py"
SPEC = importlib.util.spec_from_file_location("evict_release_zip", MODULE_PATH)
release_zip = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(release_zip)


def pe_bytes():
    # A minimal PE header fixture; never executed.
    content = bytearray(96)
    content[:2] = b"MZ"
    content[60:64] = (64).to_bytes(4, "little")
    content[64:68] = b"PE\0\0"
    return bytes(content)


def mac_entries(version="0.2.0", executable_mode=0o755):
    metadata = {
        "CFBundleExecutable": "Evict",
        "CFBundleShortVersionString": version,
        "CFBundleIdentifier": "com.evict.app",
        "CFBundleVersion": "2",
    }
    # A minimal Mach-O header fixture; never executed.
    binary = b"\xcf\xfa\xed\xfe" + bytes(60)
    return [
        ("Evict.app/Contents/Info.plist", plistlib.dumps(metadata), stat.S_IFREG | 0o644),
        ("Evict.app/Contents/MacOS/Evict", binary, stat.S_IFREG | executable_mode),
        ("Evict.app/Contents/Resources/current", b"../MacOS/Evict", stat.S_IFLNK | 0o777),
        ("__MACOSX/Evict.app/Contents/._Info.plist", b"resource metadata", stat.S_IFREG | 0o644),
    ]


def write_zip(path, entries):
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", UserWarning)
        with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED) as archive:
            for name, data, mode in entries:
                info = zipfile.ZipInfo(name)
                # ZipInfo normalizes Windows separators and truncates NULs.
                # Security fixtures must write the original untrusted filename bytes.
                info.filename = name
                info.orig_filename = name
                info.create_system = 3
                info.external_attr = mode << 16
                archive.writestr(info, data)



def dmg_bytes():
    # Minimal UDIF structural fixture; never mounted or executed.
    data = b"disk image fixture" * 16
    xml = plistlib.dumps({"resource-fork": {"blkx": [{"Name": "fixture", "Data": b"data"}]}})
    trailer = bytearray(512)
    trailer[:4] = b"koly"
    for offset, value, width in (
        (4, 4, 4), (8, 512, 4), (12, 1, 4),
        (32, len(data), 8), (56, 1, 4), (60, 1, 4),
        (216, len(data), 8), (224, len(xml), 8),
        (488, 1, 4), (492, 1, 8),
    ):
        trailer[offset:offset + width] = value.to_bytes(width, "big")
    return data + xml + bytes(trailer)


class InstallationZipTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.setup = self.root / "Installed"
        self.portable = self.root / "Portable"
        self.output = self.root / "Zip"
        self.setup.mkdir()
        self.portable.mkdir()
        self.version = "1.12.1"
        self.installer = self.setup / f"Evict-Setup-{self.version}.exe"
        self.installer.write_bytes(pe_bytes())
        (self.portable / "Evict.exe").write_bytes(pe_bytes() + b"portable")
        self.mac = self.root / "Evict.dmg"
        self.mac.write_bytes(dmg_bytes())

    def build(self, **kwargs):
        return Path(release_zip.make_zip(
            self.version, self.setup, self.portable, self.output, self.mac, **kwargs
        ))

    def rewrite_outer(self, transform):
        archive = self.build()
        with zipfile.ZipFile(archive) as reader:
            entries = [(item.filename, reader.read(item), stat.S_IFREG | 0o644)
                       for item in reader.infolist()]
        write_zip(archive, transform(entries))
        return archive

    def unpack(self, name="unpacked"):
        archive = self.build()
        folder = self.root / name
        with zipfile.ZipFile(archive) as reader:
            reader.extractall(folder)
        return folder, folder / f"Evict_{self.version}"

    def test_exact_versioned_root_three_payloads_and_no_internal_checksums(self):
        archive = self.build()
        self.assertEqual(archive.name, "Evict_1.12.1.zip")
        with zipfile.ZipFile(archive) as reader:
            self.assertEqual(set(reader.namelist()), {
                "Evict_1.12.1/portable/Evict_1.12.1.exe",
                "Evict_1.12.1/windows-x64/Evict_1.12.1.exe",
                "Evict_1.12.1/macOS/Evict_1.12.1.dmg",
            })
            self.assertEqual(reader.read("Evict_1.12.1/portable/Evict_1.12.1.exe"),
                             (self.portable / "Evict.exe").read_bytes())
            self.assertEqual(reader.read("Evict_1.12.1/windows-x64/Evict_1.12.1.exe"),
                             self.installer.read_bytes())
            self.assertEqual(reader.read("Evict_1.12.1/macOS/Evict_1.12.1.dmg"),
                             self.mac.read_bytes())
        release_zip.check_zip(archive)
        self.assertEqual(Path(str(archive) + ".sha256").read_text().strip(),
                         hashlib.sha256(archive.read_bytes()).hexdigest().upper())

    def test_full_beta_version_in_archive_root_and_all_payload_names(self):
        self.installer.unlink()
        self.version = "1.12.1-beta.42.16.1"
        (self.setup / f"Evict-Setup-{self.version}.exe").write_bytes(pe_bytes())
        archive = self.build()
        self.assertEqual(archive.name, f"Evict_{self.version}.zip")
        with zipfile.ZipFile(archive) as reader:
            self.assertTrue(all(name.startswith(f"Evict_{self.version}/")
                                and name.endswith((f"Evict_{self.version}.exe",
                                                   f"Evict_{self.version}.dmg"))
                                for name in reader.namelist()))
        release_zip.check_zip(archive)

    def test_reject_incomplete_or_unsafe_versions(self):
        for version in ("1.12", "01.12.1", "../1.12.1", "1.12.1-beta",
                        "1.12.1-beta.42.16", "1.12.1-beta.0.16.1", "1.12.1\n"):
            with self.subTest(version=version):
                self.version = version
                with self.assertRaises(release_zip.ZipError):
                    self.build()
        self.assertFalse(self.output.exists())

    def test_archive_version_must_match_every_payload_label(self):
        for version in ("0.2.0", "../1.12.1", "1.12.1-beta.42.16.1"):
            with self.subTest(version=version), self.assertRaises(release_zip.ZipError):
                self.build(archive_version=version)
        release_zip.check_zip(self.build(archive_version=self.version))

    def test_require_exact_installer_version_and_one_installer(self):
        self.installer.rename(self.setup / "Evict-Setup-1.11.0.exe")
        with self.assertRaises(release_zip.ZipError):
            self.build()
        (self.setup / f"Evict-Setup-{self.version}.exe").write_bytes(pe_bytes())
        with self.assertRaises(release_zip.ZipError):
            self.build()

    def test_reject_empty_or_non_pe_windows_payloads(self):
        for data in (b"", b"placeholder", b"MZ" + bytes(62), b"MZ" + bytes(94)):
            with self.subTest(data=data[:10]):
                self.installer.write_bytes(data)
                with self.assertRaises(release_zip.ZipError):
                    self.build()

    def test_reject_pe_header_offset_out_of_bounds(self):
        raw = bytearray(pe_bytes())
        for offset in (0, len(raw), 2 ** 32 - 1):
            raw[60:64] = offset.to_bytes(4, "little")
            self.installer.write_bytes(raw)
            with self.subTest(offset=offset), self.assertRaises(release_zip.ZipError):
                self.build()

    def test_reject_missing_mac_image(self):
        self.mac.unlink()
        with self.assertRaises(release_zip.ZipError):
            self.build()

    def test_reject_mac_placeholders_and_legacy_app_zip_as_new_payload(self):
        for data in (b"", b"Build coming soon", b"koly" + bytes(508), bytes(512)):
            self.mac.write_bytes(data)
            with self.subTest(data=data[:4]), self.assertRaises(release_zip.ZipError):
                self.build()
        write_zip(self.mac, mac_entries())
        with self.assertRaises(release_zip.ZipError):
            self.build()

    def test_accept_udif_standalone_zero_and_one_segment_conventions(self):
        # Each field may be zero in standalone vendor images, or explicitly one.
        for segment_number, segment_count in ((0, 0), (0, 1), (1, 0), (1, 1)):
            raw = bytearray(dmg_bytes())
            start = len(raw) - 512
            raw[start + 56:start + 60] = segment_number.to_bytes(4, "big")
            raw[start + 60:start + 64] = segment_count.to_bytes(4, "big")
            self.mac.write_bytes(raw)
            with self.subTest(number=segment_number, count=segment_count):
                release_zip.check_zip(self.build())

    def test_reject_udif_split_segments_with_zero_or_one_legacy_markers(self):
        for number, count, running_offset in ((0, 2, 0), (1, 2, 0), (2, 2, 256),
                                              (2, 1, 0), (0, 0, 1), (1, 0, 256)):
            raw = bytearray(dmg_bytes())
            start = len(raw) - 512
            raw[start + 16:start + 24] = running_offset.to_bytes(8, "big")
            raw[start + 56:start + 60] = number.to_bytes(4, "big")
            raw[start + 60:start + 64] = count.to_bytes(4, "big")
            with self.subTest(number=number, count=count, running_offset=running_offset):
                with self.assertRaisesRegex(release_zip.ZipError, "segmented macOS"):
                    release_zip.check_dmg(io.BytesIO(raw), len(raw), "split image")

    def test_udif_rejections_report_numeric_trailer_fields(self):
        raw = bytearray(dmg_bytes())
        start = len(raw) - 512
        raw[start + 56:start + 60] = (2).to_bytes(4, "big")
        self.mac.write_bytes(raw)
        with self.assertRaises(release_zip.ZipError) as error:
            self.build()
        message = str(error.exception)
        for field in ("version=4", "header_size=512", "flags=1",
                      "running_data_offset=0", "data_offset=0", "data_length=",
                      "resource_offset=0", "resource_length=0",
                      "segment_number=2", "segment_count=1", "xml_offset=",
                      "xml_length=", "image_variant=1", "sector_count=1",
                      f"file_size={len(raw)}"):
            self.assertIn(field, message)

    def test_reject_bad_udif_header_or_segment_fields(self):
        for offset, value, width in ((4, 3, 4), (8, 0, 4), (56, 2, 4),
                                     (60, 2, 4), (16, 1, 8)):
            raw = bytearray(dmg_bytes())
            start = len(raw) - 512 + offset
            raw[start:start + width] = value.to_bytes(width, "big")
            self.mac.write_bytes(raw)
            with self.subTest(offset=offset), self.assertRaises(release_zip.ZipError):
                self.build()

    def test_reject_udif_bounds_and_empty_image(self):
        for offset, value in ((24, 2 ** 64 - 1), (32, 0), (32, 2 ** 64 - 1),
                              (40, 2 ** 64 - 1), (216, 0), (216, 2 ** 64 - 1),
                              (224, 0), (224, 2 ** 64 - 1), (492, 0)):
            raw = bytearray(dmg_bytes())
            start = len(raw) - 512 + offset
            raw[start:start + 8] = value.to_bytes(8, "big")
            if offset == 40:
                raw[len(raw) - 512 + 48:len(raw) - 512 + 56] = (1).to_bytes(8, "big")
            self.mac.write_bytes(raw)
            with self.subTest(offset=offset), self.assertRaises(release_zip.ZipError):
                self.build()

    def test_reject_invalid_udif_metadata(self):
        raw = bytearray(dmg_bytes())
        trailer = raw[-512:]
        xml = int.from_bytes(trailer[216:224], "big")
        raw[xml:xml + 8] = b"notplist"
        self.mac.write_bytes(raw)
        with self.assertRaises(release_zip.ZipError):
            self.build()

    def test_reject_missing_udif_block_descriptors(self):
        original = dmg_bytes()
        trailer = bytearray(original[-512:])
        xml_offset = int.from_bytes(trailer[216:224], "big")
        for metadata in ({}, {"resource-fork": {}}, {"resource-fork": {"blkx": []}},
                         {"resource-fork": {"blkx": "not a list"}}):
            xml = plistlib.dumps(metadata)
            trailer[224:232] = len(xml).to_bytes(8, "big")
            self.mac.write_bytes(original[:xml_offset] + xml + bytes(trailer))
            with self.subTest(metadata=metadata), self.assertRaises(release_zip.ZipError):
                self.build()

    def test_reject_outer_extra_missing_duplicate_and_old_platform_folder(self):
        transforms = (
            lambda e: e + [("README.txt", b"extra", stat.S_IFREG | 0o644)],
            lambda e: e[:-1],
            lambda e: e[:-1] + [e[0]],
            lambda e: [(n.replace("/windows-x64/", "/windows-installer/"), d, m) for n, d, m in e],
            lambda e: e + [(e[0][0] + ".sha256", b"F" * 64, stat.S_IFREG | 0o644)],
        )
        for transform in transforms:
            with self.subTest(transform=transform), self.assertRaises(release_zip.ZipError):
                release_zip.check_zip(self.rewrite_outer(transform))

    def test_reject_unversioned_payloads_mismatched_versions_and_wrong_extensions(self):
        for old, new in (("Evict_1.12.1.exe", "Evict.exe"),
                         ("Evict_1.12.1.dmg", "Evict_0.2.0.dmg"),
                         ("Evict_1.12.1.dmg", "Evict_1.12.1.zip"),
                         ("Evict_1.12.1.exe", "Evict_1.11.0.exe")):
            with self.subTest(new=new), self.assertRaises(release_zip.ZipError):
                release_zip.check_zip(self.rewrite_outer(
                    lambda e: [(n.replace(old, new), d, m) for n, d, m in e]
                ))

    def test_reject_wrong_root_missing_root_and_multiple_roots(self):
        transforms = (
            lambda e: [(n.replace("Evict_1.12.1/", "Other_1.12.1/"), d, m) for n, d, m in e],
            lambda e: [(n.split("/", 1)[1], d, m) for n, d, m in e],
            lambda e: [(n.replace("Evict_1.12.1/", "Evict_01.12.1/"), d, m) for n, d, m in e],
            lambda e: [(e[0][0].replace("Evict_1.12.1/", "Evict_1.11.0/"), e[0][1], e[0][2])] + e[1:],
        )
        for transform in transforms:
            with self.subTest(transform=transform), self.assertRaises(release_zip.ZipError):
                release_zip.check_zip(self.rewrite_outer(transform))

    def test_reject_zip_filename_version_different_from_root(self):
        archive = self.build()
        archive = archive.rename(archive.with_name("Evict_1.11.0.zip"))
        with self.assertRaises(release_zip.ZipError):
            release_zip.check_zip(archive)

    def test_reject_outer_traversal_backslash_nul_and_symlink(self):
        for name, mode in (
            ("Evict_1.12.1/portable/../Evict_1.12.1.exe", stat.S_IFREG | 0o644),
            ("Evict_1.12.1\\portable\\Evict_1.12.1.exe", stat.S_IFREG | 0o644),
            ("Evict_1.12.1/portable/Evict_1.12.1.exe\0hidden", stat.S_IFREG | 0o644),
            ("Evict_1.12.1/portable/Evict_1.12.1.exe", stat.S_IFLNK | 0o777),
            ("Evict_1.12.1/portable/Evict_1.12.1.exe", stat.S_IFDIR | 0o755),
            ("/Evict_1.12.1/portable/Evict_1.12.1.exe", stat.S_IFREG | 0o644),
        ):
            with self.subTest(name=name, mode=mode), self.assertRaises(release_zip.ZipError):
                release_zip.check_zip(self.rewrite_outer(
                    lambda e: [(name, e[0][1], mode)] + e[1:]
                ))

    def test_reject_corrupt_outer_payload_headers(self):
        for index in (0, 1, 2):
            with self.subTest(index=index), self.assertRaises(release_zip.ZipError):
                release_zip.check_zip(self.rewrite_outer(
                    lambda e: e[:index] + [(e[index][0], b"corrupt", e[index][2])] + e[index + 1:]
                ))

    def test_reject_outer_member_crc_mismatch(self):
        archive = self.build()
        with zipfile.ZipFile(archive) as reader:
            entries = [(item.filename, reader.read(item)) for item in reader.infolist()]
        with zipfile.ZipFile(archive, "w", zipfile.ZIP_STORED) as writer:
            for name, data in entries:
                writer.writestr(name, data)
        with zipfile.ZipFile(archive) as reader:
            info = reader.infolist()[2]
        raw = bytearray(archive.read_bytes())
        header = info.header_offset
        name_size = int.from_bytes(raw[header + 26:header + 28], "little")
        extra_size = int.from_bytes(raw[header + 28:header + 30], "little")
        data_offset = header + 30 + name_size + extra_size
        raw[data_offset] ^= 1
        archive.write_bytes(raw)
        with self.assertRaises(release_zip.ZipError):
            release_zip.check_zip(archive)

    def test_check_unpacked_wrapper_and_versioned_root(self):
        wrapper, root = self.unpack()
        release_zip.check_folder(wrapper)
        release_zip.check_folder(root)
        (root / "portable" / f"Evict_{self.version}.exe").write_bytes(b"tampered")
        with self.assertRaises(release_zip.ZipError):
            release_zip.check_folder(wrapper)

    def test_cli_check_folder_accepts_wrapper_and_versioned_root(self):
        wrapper, root = self.unpack()
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            self.assertEqual(release_zip.main(["--check-folder", str(wrapper)]), 0)
            self.assertEqual(release_zip.main(["--check-folder", str(root)]), 0)
        self.assertEqual(stdout.getvalue().count("native payloads verified"), 2)

    def test_reject_unpacked_extra_root_platform_or_nested_folder(self):
        for position in ("wrapper", "root", "payload"):
            wrapper, root = self.unpack(f"unpacked-{position}")
            parent = {"wrapper": wrapper, "root": root, "payload": root / "portable"}[position]
            (parent / "extra").mkdir()
            with self.subTest(position=position), self.assertRaises(release_zip.ZipError):
                release_zip.check_folder(wrapper)

    def test_reject_unpacked_root_or_payload_symlink(self):
        wrapper, root = self.unpack()
        payload = root / "portable" / f"Evict_{self.version}.exe"
        target = self.root / "linked-native.exe"
        payload.rename(target)
        try:
            payload.symlink_to(target)
        except OSError:
            self.skipTest("Creating symlinks is not available for this runner")
        with self.assertRaises(release_zip.ZipError):
            release_zip.check_folder(wrapper)

    def test_cli_rejects_missing_required_mac_payload(self):
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr), self.assertRaises(SystemExit) as error:
            release_zip.main([self.version, "--setup", str(self.setup),
                              "--exe", str(self.portable), "--out", str(self.output)])
        self.assertEqual(error.exception.code, 2)
        self.assertIn("--macos", stderr.getvalue())
        self.assertFalse(self.output.exists())

    def test_cli_build_and_verify(self):
        stdout = io.StringIO()
        with contextlib.redirect_stdout(stdout):
            result = release_zip.main([self.version, "--setup", str(self.setup),
                                       "--exe", str(self.portable), "--macos", str(self.mac),
                                       "--out", str(self.output)])
            archive = self.output / f"Evict_{self.version}.zip"
            checked = release_zip.main(["--check", str(archive)])
        self.assertEqual(result, 0)
        self.assertEqual(checked, 0)
        self.assertIn("native payloads verified", stdout.getvalue())

    def test_invalid_inputs_preserve_previous_local_archive(self):
        original = self.build()
        previous = original.read_bytes()
        checksum = Path(str(original) + ".sha256").read_bytes()
        self.mac.write_bytes(b"invalid replacement")
        with self.assertRaises(release_zip.ZipError):
            self.build()
        self.assertEqual(original.read_bytes(), previous)
        self.assertEqual(Path(str(original) + ".sha256").read_bytes(), checksum)


class LegacyMacArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.mac = self.root / "Evict-native.zip"
        write_zip(self.mac, mac_entries())

    def assert_invalid_mac(self, entries):
        write_zip(self.mac, entries)
        with self.assertRaises(release_zip.ZipError):
            release_zip.check_macos_archive(self.mac)

    def test_legacy_native_permissions_symlinks_and_version_survive(self):
        self.assertEqual(release_zip.check_macos_archive(self.mac), "0.2.0")
        with zipfile.ZipFile(self.mac) as inner:
            binary = inner.getinfo("Evict.app/Contents/MacOS/Evict")
            link = inner.getinfo("Evict.app/Contents/Resources/current")
            self.assertEqual((binary.external_attr >> 16) & 0o777, 0o755)
            self.assertTrue(stat.S_ISLNK(link.external_attr >> 16))
            self.assertEqual(inner.read(link), b"../MacOS/Evict")

    def test_reject_empty_mac_app_zip(self):
        self.assert_invalid_mac([])

    def test_reject_mac_archive_missing_executable(self):
        self.assert_invalid_mac([mac_entries()[0]])

    def test_reject_mac_executable_without_permissions(self):
        self.assert_invalid_mac(mac_entries(executable_mode=0o644))

    def test_reject_non_macho_mac_executable(self):
        entries = mac_entries()
        entries[1] = (entries[1][0], b"placeholder" + bytes(64), entries[1][2])
        self.assert_invalid_mac(entries)

    def test_reject_invalid_mac_bundle_version(self):
        for version in ("0.2", "0.2.0-beta.1.2.3", "00.2.0"):
            with self.subTest(version=version):
                self.assert_invalid_mac(mac_entries(version=version))

    def test_reject_invalid_mac_plist(self):
        entries = mac_entries()
        for payload in (b"invalid", b'<?xml version="1.0"?><plist><dict>', plistlib.dumps(["not a dictionary"])):
            with self.subTest(payload=payload[:10]):
                entries[0] = (entries[0][0], payload, entries[0][2])
                self.assert_invalid_mac(entries)

    def test_reject_mac_plist_wrong_executable(self):
        entries = mac_entries()
        metadata = plistlib.loads(entries[0][1])
        metadata["CFBundleExecutable"] = "Different"
        entries[0] = (entries[0][0], plistlib.dumps(metadata), entries[0][2])
        self.assert_invalid_mac(entries)


    def test_reject_corrupted_mac_resource_crc(self):
        name = "Evict.app/Contents/Resources/data.bin"
        write_zip(self.mac, mac_entries() + [(name, b"a" * 8192, stat.S_IFREG | 0o644)])
        with zipfile.ZipFile(self.mac) as archive:
            info = archive.getinfo(name)
        raw = bytearray(self.mac.read_bytes())
        header = info.header_offset
        name_size = int.from_bytes(raw[header + 26:header + 28], "little")
        extra_size = int.from_bytes(raw[header + 28:header + 30], "little")
        data_offset = header + 30 + name_size + extra_size
        raw[data_offset + info.compress_size - 1] ^= 1
        self.mac.write_bytes(raw)
        with self.assertRaises(release_zip.ZipError):
            release_zip.check_macos_archive(self.mac)

    def test_reject_nested_duplicate_or_unrelated_files(self):
        for extra in (mac_entries()[0], ("README.txt", b"extra", stat.S_IFREG | 0o644)):
            with self.subTest(name=extra[0]):
                self.assert_invalid_mac(mac_entries() + [extra])


    def test_security_fixtures_preserve_raw_archive_names_on_every_platform(self):
        names = ("Evict.app\\evil", "Evict.app/Contents/evil\0hidden")
        write_zip(self.mac, [(name, b"unsafe", stat.S_IFREG | 0o644) for name in names])
        with zipfile.ZipFile(self.mac) as archive:
            self.assertEqual([entry.orig_filename for entry in archive.infolist()], list(names))
        with self.assertRaises(release_zip.ZipError):
            release_zip.check_macos_archive(self.mac)

    def test_reject_nested_traversal_backslash_absolute_and_device_entries(self):
        for name, mode in (
            ("Evict.app/../evil", stat.S_IFREG),
            ("Evict.app\\evil", stat.S_IFREG),
            ("Evict.app/Contents/evil\0hidden", stat.S_IFREG),
            ("/Evict.app/evil", stat.S_IFREG),
            ("Evict.app/Contents/pipe", stat.S_IFIFO),
            ("__MACOSX/Other.app/info", stat.S_IFREG),
        ):
            with self.subTest(name=name):
                self.assert_invalid_mac(mac_entries() + [(name, b"unsafe", mode | 0o644)])

    def test_reject_bundle_symlinks_escaping_app(self):
        for target in (b"../../../evil", b"/tmp/evil", b"C:\\evil", b"\xff"):
            with self.subTest(target=target):
                entries = mac_entries()
                entries[2] = (entries[2][0], target, entries[2][2])
                self.assert_invalid_mac(entries)


    def test_reject_chained_symlink_escape(self):
        entries = mac_entries() + [
            ("Evict.app/Contents/a", b"..", stat.S_IFLNK | 0o777),
            ("Evict.app/Contents/x", b"a/../evil", stat.S_IFLNK | 0o777),
        ]
        self.assert_invalid_mac(entries)

    def test_reject_symlink_cycles(self):
        entries = mac_entries() + [
            ("Evict.app/Contents/a", b"b", stat.S_IFLNK | 0o777),
            ("Evict.app/Contents/b", b"a", stat.S_IFLNK | 0o777),
        ]
        self.assert_invalid_mac(entries)

    def test_allow_safe_framework_symlink_chains(self):
        entries = mac_entries() + [
            ("Evict.app/Contents/Frameworks/F.framework/Versions/Current", b"A", stat.S_IFLNK | 0o777),
            ("Evict.app/Contents/Frameworks/F.framework/F", b"Versions/Current/F", stat.S_IFLNK | 0o777),
            ("Evict.app/Contents/Frameworks/F.framework/Versions/A/F", b"framework", stat.S_IFREG | 0o755),
        ]
        write_zip(self.mac, entries)
        release_zip.check_macos_archive(self.mac)

    def test_reject_directory_modes_masquerading_as_required_files(self):
        for index in (0, 1):
            with self.subTest(index=index):
                entries = mac_entries()
                name, data, mode = entries[index]
                entries[index] = (name, data, stat.S_IFDIR | stat.S_IMODE(mode))
                self.assert_invalid_mac(entries)

    def test_reject_redirected_or_file_bundle_parents(self):
        for name, data, mode in (
            ("Evict.app/Contents", b"Other", stat.S_IFLNK | 0o777),
            ("Evict.app/Contents/MacOS", b"Elsewhere", stat.S_IFLNK | 0o777),
            ("Evict.app", b"ordinary file", stat.S_IFREG | 0o644),
            ("Evict.app/Contents", b"ordinary file", stat.S_IFREG | 0o644),
        ):
            with self.subTest(name=name, mode=mode):
                self.assert_invalid_mac(mac_entries() + [(name, data, mode)])


class LegacyReleaseZipTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.mac = self.root / "native.zip"
        write_zip(self.mac, mac_entries())
        payloads = (
            ("portable/Evict.exe", pe_bytes()),
            ("windows-installer/Evict-Setup-1.10.0.exe", pe_bytes()),
            ("macOS/Evict-macOS-0.2.0.zip", self.mac.read_bytes()),
        )
        self.entries = []
        for name, data in payloads:
            self.entries.extend((
                (name, data, stat.S_IFREG | 0o644),
                (name + ".sha256", hashlib.sha256(data).hexdigest().encode("ascii"),
                 stat.S_IFREG | 0o644),
            ))
        self.archive = self.root / "Evict-1.10.0.zip"

    def build(self):
        write_zip(self.archive, self.entries)
        return self.archive

    def rewrite_outer(self, transform):
        write_zip(self.archive, transform(list(self.entries)))
        return self.archive

    def test_historical_six_payload_zip_and_folder_remain_readable(self):
        release_zip.check_zip(self.build())
        folder = self.root / "unpacked"
        with zipfile.ZipFile(self.archive) as reader:
            reader.extractall(folder)
        release_zip.check_folder(folder)
        (folder / "portable" / "Evict.exe").write_bytes(b"tampered")
        with self.assertRaises(release_zip.ZipError):
            release_zip.check_folder(folder)

    def test_reject_outer_extra_missing_duplicate_and_old_installer_folder(self):
        transforms = (
            lambda e: e + [("README.txt", b"extra", stat.S_IFREG | 0o644)],
            lambda e: e[:-1],
            lambda e: e[:-1] + [e[0]],
            lambda e: [(n.replace("windows-installer/", "installer/"), d, m) for n, d, m in e],
        )
        for transform in transforms:
            with self.subTest(transform=transform):
                with self.assertRaises(release_zip.ZipError):
                    release_zip.check_zip(self.rewrite_outer(transform))

    def test_reject_outer_traversal_or_symlink(self):
        for new_name, mode in (
            ("portable/../Evict.exe", stat.S_IFREG | 0o644),
            ("portable\\Evict.exe", stat.S_IFREG | 0o644),
            ("portable/Evict.exe\0hidden", stat.S_IFREG | 0o644),
            ("portable/Evict.exe", stat.S_IFLNK | 0o777),
        ):
            with self.subTest(name=new_name, mode=mode):
                def transform(entries):
                    return [(new_name, entries[0][1], mode)] + entries[1:]
                with self.assertRaises(release_zip.ZipError):
                    release_zip.check_zip(self.rewrite_outer(transform))

    def test_reject_tampered_payload_or_checksum(self):
        for index, payload in ((0, pe_bytes() + b"tampered"), (1, b"F" * 64 + b"\n"), (1, b"bad")):
            with self.subTest(index=index, payload=payload[:10]):
                def transform(entries):
                    n, _, m = entries[index]
                    entries[index] = (n, payload, m)
                    return entries
                with self.assertRaises(release_zip.ZipError):
                    release_zip.check_zip(self.rewrite_outer(transform))

    def test_reject_outer_mac_name_bundle_version_mismatch(self):
        def transform(entries):
            return [(n.replace("Evict-macOS-0.2.0", "Evict-macOS-0.3.0"), d, m)
                    for n, d, m in entries]
        with self.assertRaises(release_zip.ZipError):
            release_zip.check_zip(self.rewrite_outer(transform))


if __name__ == "__main__":
    unittest.main()
