"""Regression coverage for stable Windows inputs to Mac delivery bundles."""
from __future__ import annotations

import hashlib
import importlib.util
import io
import os
import subprocess
import tempfile
import unittest
import urllib.request
from pathlib import Path
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location(
    "fetch_windows_bundle", Path(__file__).resolve().parents[1] / "fetch_windows_bundle.py"
)
bundle = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(bundle)


def release(version="1.9.0", *, draft=False, prerelease=False, tag=None):
    tag = tag or f"win-v{version}"
    value = {"tag_name": tag, "draft": draft, "prerelease": prerelease, "assets": []}
    for name in (
        f"Evict-Setup-{version}.exe", f"Evict-Setup-{version}.exe.sha256",
        "Evict.exe", "Evict.exe.sha256",
    ):
        value["assets"].append({
            "name": name, "size": 64 if name.endswith(".sha256") else 10,
            "state": "uploaded",
            "browser_download_url": f"https://github.com/{bundle.REPOSITORY}/releases/download/{tag}/{name}",
        })
    return value


class FakeOpener:
    def __init__(self, contents):
        self.contents = contents
        self.requests = []

    def open(self, request, timeout):
        self.requests.append(request)
        return io.BytesIO(self.contents)


class WindowsBundleSourceTests(unittest.TestCase):
    def test_pagination_is_explicit_and_complete(self):
        calls = []
        def fetch(page):
            calls.append(page)
            return [{}] * 100 if page == 1 else [release()]
        self.assertEqual(101, len(bundle.list_releases(fetch)))
        self.assertEqual([1, 2], calls)

    def test_pagination_limit_fails_closed(self):
        with self.assertRaisesRegex(RuntimeError, "finite pagination"):
            bundle.list_releases(lambda page: [{}] * 100)

    def test_invalid_api_shape_and_page_size_fail(self):
        for response in ({"message": "rate limited"}, [None], [{}] * 101):
            with self.subTest(response_type=type(response).__name__):
                with self.assertRaises(RuntimeError):
                    bundle.list_releases(lambda page: response)

    def test_numeric_maximum_main_release_excludes_off_main_stable(self):
        visited = []
        def on_main(tag):
            visited.append(tag)
            return tag != "win-v1.10.0"
        version, chosen = bundle.choose_release(
            [release("1.8.9"), release("1.10.0"), release("1.9.0")], on_main
        )
        self.assertEqual("1.9.0", version)
        self.assertEqual("win-v1.9.0", chosen["tag_name"])
        self.assertEqual(["win-v1.10.0", "win-v1.9.0"], visited)

    def test_drafts_betas_mac_tags_and_short_versions_are_ignored(self):
        candidates = [
            release("4.0.0", draft=True), release("5.0.0", prerelease=True),
            release("6.0.0", tag="mac-v6.0.0"),
            release("7.0.0", tag="win-v7.0"), release("8.0.0", tag="win-v8.0.0-beta.1.2.1"),
            release("1.9.0"), {"tag_name": "win-v9.0.0"},
        ]
        self.assertEqual("1.9.0", bundle.choose_release(candidates, lambda tag: True)[0])

    def test_legacy_numeric_windows_tag_can_be_selected(self):
        self.assertEqual(
            "1.9.0", bundle.choose_release([release(tag="v1.9.0")], lambda tag: True)[0]
        )

    def test_no_main_release_and_native_numeric_overflow_fail(self):
        with self.assertRaisesRegex(RuntimeError, "reachable from main"):
            bundle.choose_release([release()], lambda tag: False)
        with self.assertRaisesRegex(RuntimeError, "numeric version limit"):
            bundle.choose_release([release("65536.0.0")], lambda tag: True)

    def test_ancestry_checks_resolved_commit_against_origin_main(self):
        calls = []
        def run(arguments, **options):
            calls.append(arguments)
            return subprocess.CompletedProcess(arguments, 0, "a" * 40 + "\n", "")
        self.assertTrue(bundle.tag_is_on_main("win-v1.9.0", run))
        self.assertEqual(
            ["git", "merge-base", "--is-ancestor", "a" * 40, "origin/main"], calls[1]
        )

    def test_off_main_ancestry_returns_false(self):
        answers = iter((
            subprocess.CompletedProcess([], 0, "a" * 40, ""),
            subprocess.CompletedProcess([], 1, "", ""),
        ))
        self.assertFalse(bundle.tag_is_on_main("win-v1.10.0", lambda *args, **kwargs: next(answers)))

    def test_missing_tags_and_git_errors_fail_closed(self):
        with self.assertRaisesRegex(RuntimeError, "Cannot resolve"):
            bundle.tag_is_on_main(
                "win-v1.9.0", lambda *args, **kwargs: subprocess.CompletedProcess([], 128, "", "")
            )
        answers = iter((
            subprocess.CompletedProcess([], 0, "a" * 40, ""),
            subprocess.CompletedProcess([], 128, "", ""),
        ))
        with self.assertRaisesRegex(RuntimeError, "ancestry"):
            bundle.tag_is_on_main("win-v1.9.0", lambda *args, **kwargs: next(answers))
        with self.assertRaises(ValueError):
            bundle.tag_is_on_main("win-v1.9.0; echo secret")

    def test_exact_uploaded_official_assets_are_required(self):
        self.assertEqual(4, len(bundle.expected_assets("1.9.0", release())))
        for mutation in ("duplicate", "missing", "url", "empty", "boolean", "state", "huge_checksum"):
            value = release()
            if mutation == "duplicate":
                value["assets"].append(dict(value["assets"][0]))
            elif mutation == "missing":
                value["assets"].pop()
            elif mutation == "url":
                value["assets"][0]["browser_download_url"] += "?untrusted=true"
            elif mutation == "empty":
                value["assets"][0]["size"] = 0
            elif mutation == "boolean":
                value["assets"][0]["size"] = True
            elif mutation == "state":
                value["assets"][0]["state"] = "new"
            else:
                value["assets"][1]["size"] = 4097
            with self.subTest(mutation=mutation):
                with self.assertRaises(RuntimeError):
                    bundle.expected_assets("1.9.0", value)

    def test_api_redirects_and_untrusted_asset_redirects_are_rejected(self):
        request = urllib.request.Request("https://github.com/krishnabhunia/evict-uninstaller/releases/download/win-v1.9.0/Evict.exe")
        with self.assertRaisesRegex(RuntimeError, "API"):
            bundle.NoApiRedirect().redirect_request(request, None, 302, "", {}, "https://api.github.com/other")
        for destination in (
            "http://release-assets.githubusercontent.com/file", "https://github.example/file",
            "https://release-assets.githubusercontent.com.evil.test/file",
            "https://user@release-assets.githubusercontent.com/file",
            "https://release-assets.githubusercontent.com:444/file",
        ):
            with self.subTest(destination=destination):
                with self.assertRaisesRegex(RuntimeError, "trusted GitHub CDN"):
                    bundle.ReleaseAssetRedirect().redirect_request(request, None, 302, "", {}, destination)
        redirected = bundle.ReleaseAssetRedirect().redirect_request(
            request, None, 302, "", {}, "https://release-assets.githubusercontent.com/file?signature=value"
        )
        self.assertEqual("https://release-assets.githubusercontent.com/file?signature=value", redirected.full_url)

    def test_download_uses_no_token_and_does_not_overwrite(self):
        asset = release()["assets"][0]
        opener = FakeOpener(b"0123456789")
        with tempfile.TemporaryDirectory() as temporary, patch.dict(os.environ, {"GH_TOKEN": "private-test-token"}):
            folder = Path(temporary)
            self.assertEqual(b"0123456789", bundle.download_asset(asset, folder, opener).read_bytes())
            self.assertFalse(opener.requests[0].has_header("Authorization"))
            with self.assertRaisesRegex(RuntimeError, "overwrite"):
                bundle.download_asset(asset, folder, opener)

    def test_truncated_and_oversized_downloads_leave_no_component(self):
        asset = release()["assets"][0]
        for data in (b"short", b"x" * 11):
            with tempfile.TemporaryDirectory() as temporary:
                folder = Path(temporary)
                with self.assertRaisesRegex(RuntimeError, "published size"):
                    bundle.download_asset(asset, folder, FakeOpener(data))
                self.assertEqual([], list(folder.iterdir()))

    def test_sidecars_accept_hash_only_or_exact_basename(self):
        with tempfile.TemporaryDirectory() as temporary:
            sidecar = Path(temporary) / "Evict.exe.sha256"
            digest = "a" * 64
            for contents in (digest + "\n", digest.upper() + "  Evict.exe\n", digest + " *Evict.exe\n"):
                sidecar.write_text(contents, encoding="utf-8")
                self.assertEqual(digest, bundle.read_checksum(sidecar, "Evict.exe"))
            for contents in ("bad", digest + "  Other.exe", digest + "  ../Evict.exe", digest + "\nextra"):
                sidecar.write_text(contents, encoding="utf-8")
                with self.assertRaisesRegex(RuntimeError, "checksum"):
                    bundle.read_checksum(sidecar, "Evict.exe")

    def test_both_components_must_match_downloaded_sha256(self):
        with tempfile.TemporaryDirectory() as temporary:
            folder = Path(temporary)
            for name in ("Evict-Setup-1.9.0.exe", "Evict.exe"):
                component = "windows-installer" if name.startswith("Evict-Setup-") else "portable"
                path = folder / component / name
                path.parent.mkdir(exist_ok=True)
                path.write_bytes(name.encode())
                path.with_name(name + ".sha256").write_text(
                    hashlib.sha256(path.read_bytes()).hexdigest() + "\n", encoding="utf-8"
                )
            self.assertEqual(2, len(bundle.verify_components(folder, "1.9.0")))
            (folder / "portable" / "Evict.exe").write_bytes(b"corrupted")
            with self.assertRaisesRegex(RuntimeError, "SHA-256"):
                bundle.verify_components(folder, "1.9.0")


if __name__ == "__main__":
    unittest.main()
