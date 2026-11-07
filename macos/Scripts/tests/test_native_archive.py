import hashlib
import importlib.util
from pathlib import Path
import plistlib
import stat
import tempfile
import unittest
import warnings
import zipfile

SCRIPT = Path(__file__).resolve().parents[1] / "verify-release.py"
SPEC = importlib.util.spec_from_file_location("verify_native_release", SCRIPT)
HELPER = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(HELPER)


class NativeArchiveTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.path = Path(self.temp.name) / "Evict.zip"
        self.version = "1.2.0-beta003"
        self.notes = "- Native updates include verified files and saved recovery copies.\n"

    def fixture(self, info=None, extra=None, notes=None, executable=True):
        metadata = {"CFBundleIdentifier": "app.evict.mac", "CFBundleExecutable": "Evict",
                    "CFBundleShortVersionString": "1.2.0", "EvictReleaseVersion": self.version}
        metadata.update(info or {})
        with zipfile.ZipFile(self.path, "w") as archive:
            archive.writestr("Evict.app/Contents/Info.plist", plistlib.dumps(metadata))
            binary = zipfile.ZipInfo("Evict.app/Contents/MacOS/Evict")
            binary.external_attr = (stat.S_IFREG | (0o755 if executable else 0o644)) << 16
            archive.writestr(binary, b"\xca\xfe\xba\xbe" + bytes(5000))
            archive.writestr("Evict.app/Contents/Resources/release-notes.md", self.notes if notes is None else notes)
            for name, value in extra or []:
                archive.writestr(name, value)

    def verify(self):
        return HELPER.validate_archive(self.path, self.version, "1.2.0", self.notes)

    def test_full_beta_identity_is_distinct_from_native_numeric_core(self):
        self.fixture()
        self.assertEqual(self.verify(), hashlib.sha256(self.path.read_bytes()).hexdigest())

    def test_wrong_bundle_and_windows_release_identity_are_refused(self):
        for patch in [{"CFBundleIdentifier": "another.app"}, {"EvictReleaseVersion": "4.1.2"},
                      {"CFBundleShortVersionString": self.version}, {"CFBundleExecutable": "Other"}]:
            with self.subTest(patch=patch):
                self.fixture(info=patch)
                with self.assertRaises(ValueError):
                    self.verify()

    def test_exact_embedded_implementation_notes_are_required(self):
        self.fixture(notes="old version notes")
        with self.assertRaisesRegex(ValueError, "implementation notes"):
            self.verify()

    def test_archive_paths_cannot_escape_bundle(self):
        for name in ["/absolute", "Evict.app/../../escape", "Evict.app\\Contents\\escape", "Other.app/file"]:
            with self.subTest(name=name):
                self.fixture(extra=[(name, b"x")])
                with self.assertRaisesRegex(ValueError, "Unsafe"):
                    self.verify()

    def test_symlink_and_duplicate_entries_are_refused(self):
        link = zipfile.ZipInfo("Evict.app/Contents/link")
        link.external_attr = (stat.S_IFLNK | 0o777) << 16
        self.fixture(extra=[(link, b"/etc/passwd")])
        with self.assertRaisesRegex(ValueError, "symbolic links"):
            self.verify()
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            self.fixture(extra=[("Evict.app/Contents/Info.plist", b"duplicate")])
        with self.assertRaisesRegex(ValueError, "Duplicate"):
            self.verify()

    def test_nonexecutable_app_is_refused(self):
        self.fixture(executable=False)
        with self.assertRaisesRegex(ValueError, "native executable"):
            self.verify()

    def test_invalid_beta_and_mismatched_core_are_refused(self):
        self.fixture()
        for version in ["1.2.0-beta000", "1.2.0-beta1000", "1.2.0-beta.3", "1.3.0", "../../x"]:
            with self.subTest(version=version), self.assertRaises(ValueError):
                HELPER.validate_archive(self.path, version, "1.2.0", self.notes)


if __name__ == "__main__":
    unittest.main()
