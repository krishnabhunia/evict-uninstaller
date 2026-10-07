"""Release archive contract: real payloads, three folders, integrity and native Mac metadata."""
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


class ReleaseZipTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.setup = self.root / "Installed"
        self.portable = self.root / "Portable"
        self.output = self.root / "Zip"
        self.setup.mkdir()
        self.portable.mkdir()
        self.invalid_mac_attempts = 0
        self.version = "1.10.0"
        self.installer = self.setup / f"Evict-Setup-{self.version}.exe"
        self.installer.write_bytes(pe_bytes())
        (self.portable / "Evict.exe").write_bytes(pe_bytes() + b"portable")
        self.mac = self.root / "Evict-native.zip"
        write_zip(self.mac, mac_entries())

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

    def assert_invalid_mac(self, entries):
        self.invalid_mac_attempts += 1
        self.output = self.root / f"RejectedZip-{self.invalid_mac_attempts}"
        write_zip(self.mac, entries)
        with self.assertRaises(release_zip.ZipError):
            self.build()
        self.assertFalse(self.output.exists(), "invalid inputs must fail before making an archive")

    def test_exact_three_folders_and_six_payload_entries(self):
        archive = self.build()
        with zipfile.ZipFile(archive) as reader:
            self.assertEqual(
                set(reader.namelist()),
                {
                    "portable/Evict.exe", "portable/Evict.exe.sha256",
                    "windows-installer/Evict-Setup-1.10.0.exe",
                    "windows-installer/Evict-Setup-1.10.0.exe.sha256",
                    "macOS/Evict-macOS-0.2.0.zip", "macOS/Evict-macOS-0.2.0.zip.sha256",
                },
            )
        release_zip.check_zip(archive)
        self.assertEqual(Path(str(archive) + ".sha256").read_text().strip(),
                         hashlib.sha256(archive.read_bytes()).hexdigest().upper())

    def test_nested_mac_bytes_permissions_and_symlinks_survive(self):
        archive = self.build()
        with zipfile.ZipFile(archive) as reader:
            embedded = reader.read("macOS/Evict-macOS-0.2.0.zip")
        self.assertEqual(embedded, self.mac.read_bytes())
        with zipfile.ZipFile(io.BytesIO(embedded)) as inner:
            binary = inner.getinfo("Evict.app/Contents/MacOS/Evict")
            link = inner.getinfo("Evict.app/Contents/Resources/current")
            self.assertEqual((binary.external_attr >> 16) & 0o777, 0o755)
            self.assertTrue(stat.S_ISLNK(link.external_attr >> 16))
            self.assertEqual(inner.read(link), b"../MacOS/Evict")

    def test_independent_mac_archive_version_does_not_rename_windows_payload(self):
        archive = self.build(archive_version="0.2.0")
        self.assertEqual(archive.name, "Evict-0.2.0.zip")
        with zipfile.ZipFile(archive) as reader:
            self.assertIn("windows-installer/Evict-Setup-1.10.0.exe", reader.namelist())

    def test_full_beta_version_is_preserved(self):
        self.installer.unlink()
        self.version = "1.11.0-beta.13.32.1"
        (self.setup / f"Evict-Setup-{self.version}.exe").write_bytes(pe_bytes())
        archive = self.build()
        self.assertEqual(archive.name, f"Evict-{self.version}.zip")
        release_zip.check_zip(archive)

    def test_reject_incomplete_or_unsafe_versions(self):
        for version in ("1.10", "01.10.0", "../1.10.0", "1.10.0-beta",
                        "1.10.0-beta.13.32", "1.10.0-beta.0.32.1", "1.10.0\n"):
            with self.subTest(version=version):
                self.version = version
                with self.assertRaises(release_zip.ZipError):
                    self.build()
        self.assertFalse(self.output.exists())

    def test_reject_unsafe_archive_version(self):
        with self.assertRaises(release_zip.ZipError):
            self.build(archive_version="../0.2.0")

    def test_require_exact_installer_version(self):
        self.installer.rename(self.setup / "Evict-Setup-1.9.0.exe")
        with self.assertRaises(release_zip.ZipError):
            self.build()

    def test_reject_multiple_installers(self):
        (self.setup / "other.exe").write_bytes(pe_bytes())
        with self.assertRaises(release_zip.ZipError):
            self.build()

    def test_reject_empty_or_non_pe_windows_payloads(self):
        for data in (b"", b"placeholder", b"MZ" + bytes(62), b"MZ" + bytes(94)):
            with self.subTest(data=data[:10]):
                self.installer.write_bytes(data)
                with self.assertRaises(release_zip.ZipError):
                    self.build()

    def test_reject_missing_mac_archive(self):
        self.mac.unlink()
        with self.assertRaises(release_zip.ZipError):
            self.build()

    def test_reject_mac_placeholder(self):
        self.mac.write_bytes(b"Build coming soon")
        with self.assertRaises(release_zip.ZipError):
            self.build()

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
            self.build()
        self.assertFalse(self.output.exists())

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
        archive = self.build()
        release_zip.check_zip(archive)

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

    def test_check_unpacked_folder_verifies_files_and_checksums(self):
        archive = self.build()
        unpacked = self.root / "unpacked"
        with zipfile.ZipFile(archive) as reader:
            reader.extractall(unpacked)
        release_zip.check_folder(unpacked)
        (unpacked / "portable" / "Evict.exe").write_bytes(b"tampered")
        with self.assertRaises(release_zip.ZipError):
            release_zip.check_folder(unpacked)

    def test_reject_unpacked_extra_directory_or_nested_directory(self):
        for nested in (False, True):
            with self.subTest(nested=nested):
                archive = self.build()
                unpacked = self.root / ("unpacked-nested" if nested else "unpacked-extra")
                with zipfile.ZipFile(archive) as reader:
                    reader.extractall(unpacked)
                (unpacked / "portable" / "nested" if nested else unpacked / "extra").mkdir()
                with self.assertRaises(release_zip.ZipError):
                    release_zip.check_folder(unpacked)

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
            archive = self.output / f"Evict-{self.version}.zip"
            checked = release_zip.main(["--check", str(archive)])
        self.assertEqual(result, 0)
        self.assertEqual(checked, 0)
        self.assertIn("windows-installer/", stdout.getvalue())

    def test_invalid_inputs_preserve_previous_local_archive(self):
        original = self.build()
        previous = original.read_bytes()
        self.mac.write_bytes(b"invalid replacement")
        with self.assertRaises(release_zip.ZipError):
            self.build()
        self.assertEqual(original.read_bytes(), previous)


if __name__ == "__main__":
    unittest.main()
