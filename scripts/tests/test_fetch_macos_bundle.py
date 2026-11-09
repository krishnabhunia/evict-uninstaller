"""Native provenance and byte preservation for the Windows compatibility DMG."""
from __future__ import annotations

import base64
import copy
import hashlib
import io
import json
import os
from pathlib import Path
import plistlib
import sys
import tempfile
import unittest
from unittest.mock import patch

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
import fetch_macos_bundle as bundle

SOURCE = "a" * 40
ORIGINAL = "b" * 40
WINDOWS_SOURCE = "c" * 40
CORE = "1.0.1"
BUILD = "5"
EXPECTED = (CORE, BUILD)
RUN_ID = 12345


def dmg_bytes():
    # UDIF structural fixture. Never mounted or executed.
    data = b"disk image fixture" * 16
    xml = plistlib.dumps({"resource-fork": {"blkx": [{"Name": "fixture", "Data": b"data"}]}})
    trailer = bytearray(512)
    trailer[:4] = b"koly"
    for offset, value, width in (
        (4, 4, 4), (8, 512, 4), (12, 1, 4), (32, len(data), 8),
        (56, 1, 4), (60, 1, 4), (216, len(data), 8), (224, len(xml), 8),
        (488, 1, 4), (492, 1, 8),
    ):
        trailer[offset:offset + width] = value.to_bytes(width, "big")
    return data + xml + bytes(trailer)


def source_file(version=CORE, build=BUILD):
    raw = (f'public enum Version {{\n public static let current = "{version}"\n'
           f' public static let build = "{build}"\n}}\n').encode()
    return {
        "type": "file", "path": "macos/Sources/EvictKit/Version.swift", "encoding": "base64",
        "content": base64.b64encode(raw).decode(), "size": len(raw),
        "sha": hashlib.sha1(b"blob " + str(len(raw)).encode() + b"\0" + raw).hexdigest(),
    }


def release(tag="mac-v1.0.1", *, source=SOURCE, native=True, data=None):
    data = dmg_bytes() if data is None else data
    name = f"Evict-{CORE}-macOS-universal.dmg" if native else f"Evict_{tag.removeprefix('win-v')}.dmg"
    checksum = (hashlib.sha256(data).hexdigest() + "  " + name + "\n").encode()
    blobs = {name: data, name + ".sha256": checksum}
    body = f"Built and tested release source: {source}\n"
    body += (f"Original source: {source}\nVerification run: https://github.com/{bundle.REPOSITORY}/actions/runs/{RUN_ID}\n"
             if native else f"macOS bundle version: {CORE}\n")
    value = {"tag_name": tag, "body": body, "draft": False, "prerelease": False,
             "published_at": "2026-11-05T10:30:00Z", "assets": []}
    for identity, (filename, contents) in enumerate(blobs.items(), 101):
        value["assets"].append({
            "id": identity, "name": filename, "size": len(contents), "state": "uploaded",
            "updated_at": "2026-11-05T10:29:00Z", "digest": "sha256:" + hashlib.sha256(contents).hexdigest(),
            "browser_download_url": f"https://github.com/{bundle.REPOSITORY}/releases/download/{tag}/{filename}",
        })
    return value, blobs


def run(native=True, *, source=SOURCE):
    return {"id": RUN_ID, "status": "completed", "conclusion": "success", "head_sha": source,
            "path": ".github/workflows/" + ("macos.yml" if native else "windows.yml"), "event": "push",
            "repository": {"full_name": bundle.REPOSITORY}, "head_repository": {"full_name": bundle.REPOSITORY},
            "created_at": "2026-11-05T10:00:00Z", "updated_at": "2026-11-05T11:00:00Z"}


def jobs(native=True):
    steps = {"build": ["Build and verify Apple Silicon and Intel application"], "publish": []} if native else {
        "mac-package": ["Build native universal macOS app", "Prove the app opens (launch test)"],
        "verify-published-macos": ["Mount the downloaded image and verify native installation", "Prove the published app opens after a drag-install (launch test)"],
    }
    return [{"id": identity, "name": name, "status": "completed", "conclusion": "success",
             "steps": [{"name": step, "conclusion": "success"} for step in expected]}
            for identity, (name, expected) in enumerate(steps.items(), 1001)]


class FakeGitHub(bundle.GitHub):
    def __init__(self, value=None, *, native=True):
        self.release = value or release(native=native)[0]
        tag = self.release["tag_name"]
        self.routes = {
            "/git/ref/tags/" + tag: {"object": {"type": "commit", "sha": SOURCE}},
            "/compare/" + SOURCE + "...main": {"status": "ahead"},
            "/contents/macos/Sources/EvictKit/Version.swift?ref=" + SOURCE: source_file(),
            "/actions/runs/12345": run(native),
            "/actions/runs/12345/jobs?filter=latest&per_page=100&page=1": {"total_count": 2, "jobs": jobs(native)},
            "/commits/" + SOURCE: {"sha": SOURCE, "commit": {"message": "Reviewed implementation"}},
            "/actions/workflows/windows.yml/runs?head_sha=" + SOURCE + "&status=success&per_page=100&page=1": {
                "total_count": 1, "workflow_runs": [run(False)]},
            "/releases/tags/" + tag: self.release,
        }
        self.visited = []

    def get(self, path):
        self.visited.append(path)
        return copy.deepcopy(self.routes[path])


class NativeMetadataTests(unittest.TestCase):
    def test_exact_numeric_metadata_and_blob_identity(self):
        api = FakeGitHub()
        self.assertEqual(EXPECTED, api.source_metadata(SOURCE))
        for key, value in (("sha", "0" * 40), ("size", 1), ("type", "symlink"), ("encoding", "plain"), ("content", "!")):
            with self.subTest(key=key):
                changed = FakeGitHub()
                changed.routes["/contents/macos/Sources/EvictKit/Version.swift?ref=" + SOURCE][key] = value
                with self.assertRaises(RuntimeError):
                    changed.source_metadata(SOURCE)

    def test_missing_duplicate_beta_and_overflow_versions_rejected(self):
        valid = base64.b64decode(source_file()["content"]).decode()
        invalid = ["", valid + valid, valid.replace(CORE, "1.0.1-beta001"),
                   valid.replace(CORE, "65536.0.0"), valid.replace(CORE, "01.0.1"), valid.replace('"5"', '"dev"')]
        for contents in invalid:
            with self.subTest(contents=contents):
                with self.assertRaises(RuntimeError):
                    bundle.native_metadata(contents)

    def test_native_freeze_changes_only_generated_metadata_from_original(self):
        api = FakeGitHub()
        commit = {"sha": SOURCE, "commit": {"message": "chore(release): macos 1.0.1"},
                  "parents": [{"sha": ORIGINAL}], "files": [{"filename": "macos/CHANGELOG.md", "status": "modified"}]}
        api.routes["/commits/" + SOURCE] = commit
        self.assertTrue(api.native_source_matches(SOURCE, ORIGINAL))
        commit["files"].append({"filename": "macos/Sources/EvictApp/AppState.swift", "status": "modified"})
        self.assertFalse(api.native_source_matches(SOURCE, ORIGINAL))
        commit["files"].pop()
        commit["parents"][0]["sha"] = WINDOWS_SOURCE
        self.assertFalse(api.native_source_matches(SOURCE, ORIGINAL))

    def test_legacy_metadata_is_peeled_without_unrelated_application_edits(self):
        api = FakeGitHub()
        commit = {"sha": SOURCE, "commit": {"message": "chore(release): windows 4.1.2"},
                  "parents": [{"sha": ORIGINAL}], "files": [{"filename": "windows/Directory.Build.props", "status": "modified"}]}
        api.routes["/commits/" + SOURCE] = commit
        api.routes["/commits/" + ORIGINAL] = {"sha": ORIGINAL, "commit": {"message": "Reviewed implementation"}}
        self.assertEqual(ORIGINAL, api.event_source(SOURCE))
        for filename, status in (("windows/Unreviewed.cs", "modified"), ("windows/CHANGELOG.md", "added")):
            commit["files"] = [{"filename": filename, "status": status}]
            with self.assertRaisesRegex(RuntimeError, "unreviewed"):
                api.event_source(SOURCE)

    def test_collection_rejects_duplicates_changing_count_and_incomplete_pages(self):
        api = FakeGitHub()
        for contents in ([], {"total_count": 2, "jobs": [{"id": 1}]},
                         {"total_count": 2, "jobs": [{"id": 1}, {"id": 1}]},
                         {"total_count": 1001, "jobs": []}, {"total_count": True, "jobs": []}):
            api.routes["/jobs?per_page=100&page=1"] = contents
            with self.subTest(contents=contents):
                with self.assertRaises(RuntimeError):
                    api.collection("/jobs", "jobs")
        api.routes["/jobs?per_page=100&page=1"] = {"total_count": 101, "jobs": [{"id": n} for n in range(1, 101)]}
        api.routes["/jobs?per_page=100&page=2"] = {"total_count": 102, "jobs": [{"id": 101}]}
        with self.assertRaisesRegex(RuntimeError, "changing"):
            api.collection("/jobs", "jobs")

    def test_api_token_stays_on_nonredirected_repository_endpoint(self):
        class Opener:
            def open(self, request, timeout):
                self.request = request
                return io.BytesIO(b'{"ok": true}')
        opener = Opener()
        with patch.dict(os.environ, {"GH_TOKEN": "test-secret"}), patch.object(bundle.urllib.request, "build_opener", return_value=opener) as factory:
            self.assertEqual({"ok": True}, bundle.GitHub().get("/actions/runs/12345"))
            self.assertEqual(bundle.API_ROOT + "/actions/runs/12345", opener.request.full_url)
            self.assertEqual("Bearer test-secret", opener.request.get_header("Authorization"))
            self.assertIsInstance(factory.call_args.args[0], bundle.NoApiRedirect)
        for path in ("https://other.test", "//other.test", "/releases#other"):
            with self.assertRaises(ValueError):
                bundle.GitHub().get(path)


class VerifiedReleaseTests(unittest.TestCase):
    def test_native_release_preferred_over_newer_windows_legacy(self):
        native = release()[0]
        legacy = release("win-v99.0.0", native=False)[0]
        chosen = bundle.choose_release([legacy, native], EXPECTED, FakeGitHub(native))
        self.assertEqual("mac-v1.0.1", chosen["release"]["tag_name"])

    def test_legacy_bootstrap_requires_previous_native_installation_verification(self):
        legacy = release("win-v4.1.2", native=False)[0]
        api = FakeGitHub(legacy, native=False)
        self.assertEqual("Evict_4.1.2.dmg", bundle.choose_release([legacy], EXPECTED, api)["name"])
        api.routes["/actions/runs/12345/jobs?filter=latest&per_page=100&page=1"]["jobs"][1]["steps"][1]["conclusion"] = "skipped"
        with self.assertRaises(bundle.ReleaseUnavailable):
            bundle.choose_release([legacy], EXPECTED, api)

    def test_only_exact_version_and_build_can_be_reused(self):
        value = release()[0]
        for expected in (("1.0.2", BUILD), (CORE, "6")):
            with self.assertRaises(bundle.ReleaseUnavailable):
                bundle.choose_release([value], expected, FakeGitHub(value))

    def test_drafts_prereleases_and_beta_tags_are_never_reused(self):
        value = release()[0]
        for key, changed in (("draft", True), ("prerelease", True), ("tag_name", "mac-v1.0.1-beta001")):
            candidate = copy.deepcopy(value)
            candidate[key] = changed
            with self.assertRaises(bundle.ReleaseUnavailable):
                bundle.choose_release([candidate], EXPECTED, FakeGitHub())

    def test_tag_movement_off_main_source_and_duplicate_body_fail_closed(self):
        for mutation in ("tag", "main", "marker", "version"):
            value = release()[0]
            api = FakeGitHub(value)
            if mutation == "tag":
                api.routes["/git/ref/tags/mac-v1.0.1"]["object"]["sha"] = ORIGINAL
            elif mutation == "main":
                api.routes["/compare/" + SOURCE + "...main"]["status"] = "diverged"
            elif mutation == "version":
                value["body"] = value["body"].replace(SOURCE, "invalid", 1)
            else:
                value["body"] += f"Built and tested release source: {SOURCE}\n"
            with self.subTest(mutation=mutation):
                with self.assertRaises(RuntimeError):
                    bundle.verify_release(value, EXPECTED, api)

    def test_native_run_must_be_completed_successful_own_repository_and_exact_head(self):
        invalid = (("status", "in_progress"), ("conclusion", "failure"), ("head_sha", ORIGINAL),
                   ("path", ".github/workflows/windows.yml"), ("event", "pull_request"),
                   ("repository", {"full_name": "other/repo"}), ("head_repository", {"full_name": "other/repo"}),
                   ("created_at", "2026-11-05T10:31:00Z"), ("updated_at", "2026-11-05T10:29:00Z"))
        for key, changed in invalid:
            api = FakeGitHub()
            api.routes["/actions/runs/12345"][key] = changed
            with self.subTest(key=key):
                with self.assertRaises(bundle.ReleaseUnavailable):
                    bundle.verify_release(api.release, EXPECTED, api)

    def test_native_run_marker_and_successful_job_steps_are_required(self):
        for mutation in ("url", "publish", "build_step", "duplicate_step"):
            api = FakeGitHub()
            found = api.routes["/actions/runs/12345/jobs?filter=latest&per_page=100&page=1"]["jobs"]
            if mutation == "url":
                api.release["body"] = api.release["body"].replace(bundle.REPOSITORY, "other/repo")
            elif mutation == "publish":
                found[1]["conclusion"] = "failure"
            elif mutation == "build_step":
                found[0]["steps"][0]["conclusion"] = "skipped"
            else:
                found[0]["steps"].append(dict(found[0]["steps"][0]))
            with self.subTest(mutation=mutation):
                with self.assertRaises(RuntimeError):
                    bundle.verify_release(api.release, EXPECTED, api)

    def test_assets_changed_after_success_are_rejected(self):
        api = FakeGitHub()
        api.release["assets"][0]["updated_at"] = "2026-11-05T11:01:00Z"
        with self.assertRaisesRegex(RuntimeError, "changed after"):
            bundle.verify_release(api.release, EXPECTED, api)

    def test_asset_identity_url_size_duplicates_and_digest_are_validated(self):
        for mutation in ("duplicate", "missing", "url", "size", "id", "state", "checksum_size", "digest"):
            value, _ = release()
            if mutation == "duplicate":
                value["assets"].append(dict(value["assets"][0]))
            elif mutation == "missing":
                value["assets"].pop()
            elif mutation == "url":
                value["assets"][0]["browser_download_url"] += "?untrusted=true"
            elif mutation == "checksum_size":
                value["assets"][1]["size"] = 4097
            else:
                value["assets"][0][mutation] = {"size": True, "id": 0, "state": "new", "digest": "sha256:bad"}[mutation]
            with self.subTest(mutation=mutation):
                with self.assertRaises(RuntimeError):
                    bundle.expected_assets(value, "Evict-1.0.1-macOS-universal.dmg")


class PreservedBytesTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory()
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.value, self.blobs = release()
        self.api = FakeGitHub(self.value)
        self.selection = bundle.verify_release(self.value, EXPECTED, self.api)

    def download(self, asset, folder):
        path = folder / asset["name"]
        path.write_bytes(self.blobs[asset["name"]])
        return path

    def test_matching_sha256_preserves_dmg_bytes_and_legacy_filename(self):
        image, digest = bundle.download_verified(self.selection, self.root / "download", self.download)
        manifest = bundle.package_component(self.selection, image, digest, self.root / "package", "4.2.0-beta001", WINDOWS_SOURCE)
        self.assertEqual(image.read_bytes(), (self.root / "package/Evict_4.2.0-beta001.dmg").read_bytes())
        self.assertEqual(digest + "\n", (self.root / "package/Evict_4.2.0-beta001.dmg.sha256").read_text())
        self.assertEqual(manifest, json.loads((self.root / "package/mac-provenance.json").read_text()))
        self.assertEqual({"Evict_4.2.0-beta001.dmg", "Evict_4.2.0-beta001.dmg.sha256", "mac-provenance.json"},
                         {path.name for path in (self.root / "package").iterdir()})

    def test_bad_checksum_github_digest_and_non_dmg_payload_rejected(self):
        for mutation in ("sidecar", "digest", "not_dmg"):
            with self.subTest(mutation=mutation):
                value, blobs = release(data=b"not a disk image" if mutation == "not_dmg" else None)
                if mutation == "sidecar":
                    blobs[value["assets"][1]["name"]] = ("0" * 64 + "\n").encode()
                    value["assets"][1]["digest"] = None
                if mutation == "digest":
                    value["assets"][0]["digest"] = "sha256:" + "0" * 64
                selection = bundle.verify_release(value, EXPECTED, FakeGitHub(value))
                def download(asset, folder):
                    target = folder / asset["name"]
                    target.write_bytes(blobs[asset["name"]])
                    return target
                with self.assertRaises((RuntimeError, ValueError)):
                    bundle.download_verified(selection, self.root / mutation, download)

    def test_changed_payload_before_packaging_and_invalid_windows_identity_fail(self):
        image, digest = bundle.download_verified(self.selection, self.root / "download", self.download)
        image.write_bytes(image.read_bytes() + b"changed")
        with self.assertRaisesRegex(RuntimeError, "changed before"):
            bundle.package_component(self.selection, image, digest, self.root / "package", "4.2.0", WINDOWS_SOURCE)
        for version, source in (("../4.2.0", WINDOWS_SOURCE), ("4.2.0", "invalid")):
            with self.assertRaises(ValueError):
                bundle.package_component(self.selection, image, digest, self.root / "package", version, source)

    def test_published_windows_dmg_is_identical_to_reverified_native_origin(self):
        image, digest = bundle.download_verified(self.selection, self.root / "download", self.download)
        manifest = bundle.package_component(self.selection, image, digest, self.root / "package", "4.2.0", WINDOWS_SOURCE)
        published, blobs = release("win-v4.2.0", source=WINDOWS_SOURCE, native=False)
        self.blobs.update(blobs)
        self.api.routes.update({
            "/releases/tags/win-v4.2.0": published,
            "/git/ref/tags/win-v4.2.0": {"object": {"type": "commit", "sha": WINDOWS_SOURCE}},
            "/contents/macos/Sources/EvictKit/Version.swift?ref=" + WINDOWS_SOURCE: source_file(),
        })
        original = bundle.download_verified
        with patch.object(bundle, "download_verified", side_effect=lambda selected, folder: original(selected, folder, self.download)):
            bundle.verify_published(self.api, manifest, "4.2.0", WINDOWS_SOURCE, EXPECTED, "stable", self.root / "published")
        for key, changed in (("mac_build", "6"), ("release_source", ORIGINAL), ("asset_id", 999), ("sha256", "0" * 64)):
            corrupt = dict(manifest, **{key: changed})
            with self.subTest(key=key), patch.object(bundle, "download_verified", side_effect=lambda selected, folder: original(selected, folder, self.download)):
                with self.assertRaises(RuntimeError):
                    bundle.verify_published(self.api, corrupt, "4.2.0", WINDOWS_SOURCE, EXPECTED, "stable", self.root / ("changed-" + key))

    def test_published_beta_is_checked_against_its_reserved_tag_source(self):
        image, digest = bundle.download_verified(self.selection, self.root / "download", self.download)
        manifest = bundle.package_component(self.selection, image, digest, self.root / "package", "4.2.0-beta003", WINDOWS_SOURCE)
        published, blobs = release("win-v4.2.0-beta003", source=WINDOWS_SOURCE, native=False)
        published["prerelease"] = True
        reserved = "d" * 40
        self.blobs.update(blobs)
        self.api.routes.update({
            "/releases/tags/win-v4.2.0-beta003": published,
            "/git/ref/tags/win-v4.2.0-beta003": {"object": {"type": "commit", "sha": reserved}},
            "/contents/macos/Sources/EvictKit/Version.swift?ref=" + WINDOWS_SOURCE: source_file(),
        })
        original = bundle.download_verified
        with patch.object(bundle, "download_verified", side_effect=lambda selected, folder: original(selected, folder, self.download)):
            bundle.verify_published(self.api, manifest, "4.2.0-beta003", WINDOWS_SOURCE, EXPECTED, "beta",
                                    self.root / "published", tag_source=reserved)
            for tag_source in (None, WINDOWS_SOURCE, "invalid"):
                with self.subTest(tag_source=tag_source), self.assertRaises(RuntimeError):
                    bundle.verify_published(self.api, manifest, "4.2.0-beta003", WINDOWS_SOURCE, EXPECTED, "beta",
                                            self.root / ("other-" + str(tag_source)), tag_source=tag_source)


if __name__ == "__main__":
    unittest.main()
