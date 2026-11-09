#!/usr/bin/env python3
"""Reuse an already verified native Mac DMG in a Windows installation download.

Windows delivery never builds or launches macOS. Prefer a stable native mac-v
release; during migration an older verified combined release is acceptable only
when its immutable source has the exact native version/build in the Windows
snapshot. Published bytes, checksums, tag ancestry and native CI evidence are
checked before reuse and again after the Windows download is published.
"""
from __future__ import annotations

import argparse
import base64
from datetime import datetime
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import tempfile
import time
import urllib.parse
import urllib.request
from typing import Callable

from fetch_windows_bundle import (
    API_ROOT, REPOSITORY, MAX_API_BYTES, MAX_COMPONENT_BYTES, NoApiRedirect,
    download_asset, list_releases, read_checksum,
)

CORE = r"(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)"
WINDOWS_VERSION = re.compile(CORE + r"(?:-beta(?:(?!000)[0-9]{3}|\.[1-9][0-9]*\.[1-9][0-9]*\.[1-9][0-9]*))?\Z")
STABLE_TAG = re.compile(r"(mac-v|win-v|v)(" + CORE + r")\Z")
SHA = re.compile(r"[0-9a-f]{40}\Z")
MANAGED_FILES = frozenset((
    "windows/Directory.Build.props", "windows/installer/Evict.iss", "windows/CHANGELOG.md",
    "macos/Sources/EvictKit/Version.swift", "macos/CHANGELOG.md",
))


class ReleaseUnavailable(RuntimeError):
    """A matching, completed native release may still be publishing in parallel."""


def native_metadata(text: str) -> tuple[str, str]:
    versions = re.findall(r'public static let current\s*=\s*"([^"]+)"', text)
    builds = re.findall(r'public static let build\s*=\s*"([^"]+)"', text)
    if len(versions) != 1 or len(builds) != 1 or not re.fullmatch(CORE, versions[0]) or not re.fullmatch(r"[0-9]+", builds[0]):
        raise RuntimeError("Native version source must contain one numeric version and build")
    if any(int(part) > 65535 for part in versions[0].split(".")):
        raise RuntimeError("Native version exceeds the numeric component limit")
    return versions[0], builds[0]


def marker(body: str, label: str) -> str:
    if not isinstance(body, str):
        raise RuntimeError("Release provenance body is missing")
    matches = re.findall(r"^" + re.escape(label) + r": ([^\r\n]+)$", body, re.M)
    if len(matches) != 1:
        raise RuntimeError(f"Release is missing one unambiguous {label} marker")
    return matches[0]


def timestamp(value: str) -> datetime:
    if not isinstance(value, str):
        raise RuntimeError("Missing GitHub provenance timestamp")
    try:
        result = datetime.fromisoformat(value.replace("Z", "+00:00"))
    except ValueError as error:
        raise RuntimeError("Invalid GitHub provenance timestamp") from error
    if result.tzinfo is None:
        raise RuntimeError("GitHub provenance timestamp has no timezone")
    return result


class GitHub:
    def get(self, path: str):
        if not path.startswith("/") or path.startswith("//") or "#" in path:
            raise ValueError("Expected a repository-relative API path")
        headers = {"Accept": "application/vnd.github+json", "User-Agent": "Evict-native-reuse"}
        if token := os.environ.get("GH_TOKEN"):
            headers["Authorization"] = f"Bearer {token}"
        request = urllib.request.Request(API_ROOT + path, headers=headers)
        with urllib.request.build_opener(NoApiRedirect()).open(request, timeout=30) as response:
            raw = response.read(MAX_API_BYTES + 1)
        if len(raw) > MAX_API_BYTES:
            raise RuntimeError("GitHub provenance response exceeded its size limit")
        return json.loads(raw)

    def releases(self) -> list[dict]:
        return list_releases(lambda page: self.get(f"/releases?per_page=100&page={page}"))

    def collection(self, path: str, key: str) -> list[dict]:
        entries = []
        seen = set()
        total = None
        for page in range(1, 11):
            separator = "&" if "?" in path else "?"
            response = self.get(f"{path}{separator}per_page=100&page={page}")
            if not isinstance(response, dict):
                raise RuntimeError("Malformed GitHub verification collection")
            items = response.get(key)
            count = response.get("total_count")
            if (not isinstance(items, list) or len(items) > 100 or any(not isinstance(item, dict) for item in items)
                    or isinstance(count, bool) or not isinstance(count, int) or not 0 <= count <= 1000
                    or total is not None and total != count):
                raise RuntimeError("Incomplete or changing GitHub verification collection")
            for item in items:
                identity = item.get("id")
                if isinstance(identity, bool) or not isinstance(identity, int) or identity <= 0 or identity in seen:
                    raise RuntimeError("Ambiguous GitHub verification collection identity")
                seen.add(identity)
            total = count
            entries.extend(items)
            if len(entries) == total:
                return entries
            if len(items) != 100 or len(entries) > total:
                raise RuntimeError("GitHub verification pagination was incomplete")
        raise RuntimeError("GitHub verification collection exceeded its finite limit")

    def resolve_tag(self, tag: str, *, allow_beta: bool = False) -> str:
        if not STABLE_TAG.fullmatch(tag) and not (allow_beta and tag.startswith("win-v") and WINDOWS_VERSION.fullmatch(tag[5:])):
            raise ValueError("Unexpected release tag")
        item = self.get("/git/ref/tags/" + urllib.parse.quote(tag, safe=""))["object"]
        for _ in range(5):
            if not isinstance(item, dict) or not SHA.fullmatch(str(item.get("sha", ""))):
                raise RuntimeError("Invalid Git tag target")
            if item.get("type") == "commit":
                return item["sha"]
            if item.get("type") != "tag":
                break
            item = self.get("/git/tags/" + item["sha"])["object"]
        raise RuntimeError("Release tag did not resolve to one immutable commit")

    def source_metadata(self, source: str) -> tuple[str, str]:
        if not SHA.fullmatch(source):
            raise RuntimeError("Invalid native source identity")
        path = "macos/Sources/EvictKit/Version.swift"
        file = self.get("/contents/" + path + "?ref=" + source)
        if file.get("type") != "file" or file.get("path") != path or file.get("encoding") != "base64":
            raise RuntimeError("Native source metadata is not an ordinary GitHub file")
        try:
            raw = base64.b64decode("".join(file["content"].split()), validate=True)
        except (ValueError, KeyError) as error:
            raise RuntimeError("Native source metadata could not be decoded") from error
        if len(raw) > 16384 or file.get("size") != len(raw):
            raise RuntimeError("Native source metadata has an unexpected size")
        blob = hashlib.sha1(b"blob " + str(len(raw)).encode("ascii") + b"\0" + raw).hexdigest()
        if file.get("sha") != blob:
            raise RuntimeError("Native source blob identity does not match its contents")
        return native_metadata(raw.decode("utf-8-sig"))

    def on_main(self, source: str) -> bool:
        if not SHA.fullmatch(source):
            raise RuntimeError("Invalid stable source identity")
        result = self.get("/compare/" + source + "...main")
        return result.get("status") in ("ahead", "identical")

    def event_source(self, source: str) -> str:
        """Legacy workflows commit only managed version files after their triggering head."""
        for _ in range(8):
            commit = self.get("/commits/" + source)
            if commit.get("sha") != source:
                raise RuntimeError("Legacy commit identity changed")
            message = commit.get("commit", {}).get("message", "").split("\n", 1)[0]
            if not re.fullmatch(r"chore\(release\): (?:windows|macos) " + CORE, message):
                return source
            files = commit.get("files")
            parents = commit.get("parents")
            if (not isinstance(files, list) or not files or any(f.get("filename") not in MANAGED_FILES or f.get("status") != "modified" for f in files)
                    or not isinstance(parents, list) or len(parents) != 1 or not SHA.fullmatch(str(parents[0].get("sha", "")))):
                raise RuntimeError("Legacy release metadata commit contains unreviewed changes")
            source = parents[0]["sha"]
        raise RuntimeError("Excessive legacy release metadata ancestry")

    def native_source_matches(self, source: str, original: str) -> bool:
        if source == original:
            return True
        commit = self.get("/commits/" + source)
        files = commit.get("files")
        parents = commit.get("parents")
        return (commit.get("sha") == source
                and re.fullmatch(r"chore\(release\): macos " + CORE, commit.get("commit", {}).get("message", "").split("\n", 1)[0]) is not None
                and isinstance(files, list) and bool(files)
                and all(f.get("filename") in {"macos/Sources/EvictKit/Version.swift", "macos/CHANGELOG.md"}
                        and f.get("status") == "modified" for f in files)
                and isinstance(parents, list) and len(parents) == 1
                and parents[0].get("sha") == original)

    def verified_run(self, release: dict, source: str, native: bool) -> dict:
        if native:
            url = marker(release["body"], "Verification run")
            match = re.fullmatch(r"https://github\.com/" + re.escape(REPOSITORY) + r"/actions/runs/([1-9][0-9]*)", url)
            if not match:
                raise RuntimeError("Native verification run belongs to an unexpected repository")
            original = marker(release["body"], "Original source")
            if not SHA.fullmatch(original):
                raise RuntimeError("Invalid original native source")
            if not self.native_source_matches(source, original):
                raise RuntimeError("Native release source differs from its verified original application source")
            runs = [self.get("/actions/runs/" + match[1])]
            workflow = ".github/workflows/macos.yml"
        else:
            original = self.event_source(source)
            runs = self.collection("/actions/workflows/windows.yml/runs?head_sha=" + original + "&status=success", "workflow_runs")
            workflow = ".github/workflows/windows.yml"
        for run in runs:
            if (run.get("status") != "completed" or run.get("conclusion") != "success"
                    or run.get("head_sha") != original or run.get("path") != workflow
                    or run.get("event") not in ("push", "workflow_dispatch")
                    or run.get("repository", {}).get("full_name") != REPOSITORY
                    or run.get("head_repository", {}).get("full_name") != REPOSITORY
                    or not isinstance(run.get("id"), int) or isinstance(run.get("id"), bool) or run["id"] <= 0):
                continue
            if not timestamp(run.get("created_at")) <= timestamp(release.get("published_at")) <= timestamp(run.get("updated_at")):
                continue
            jobs = self.collection(f"/actions/runs/{run['id']}/jobs?filter=latest", "jobs")
            expected = {
                "build": ["Build and verify Apple Silicon and Intel application"], "publish": []
            } if native else {
                "mac-package": ["Build native universal macOS app", "Prove the app opens (launch test)"],
                "verify-published-macos": ["Mount the downloaded image and verify native installation", "Prove the published app opens after a drag-install (launch test)"],
            }
            if all(self.job_succeeded(jobs, name, steps) for name, steps in expected.items()):
                return run
        raise ReleaseUnavailable("The matching Mac release has no completed native verification run yet")

    @staticmethod
    def job_succeeded(jobs: list[dict], name: str, expected_steps: list[str]) -> bool:
        matches = [job for job in jobs if job.get("name") == name]
        if len(matches) != 1 or matches[0].get("status") != "completed" or matches[0].get("conclusion") != "success":
            return False
        steps = matches[0].get("steps", [])
        if not isinstance(steps, list) or any(not isinstance(step, dict) for step in steps):
            return False
        return all(len(found := [s for s in steps if s.get("name") == expected]) == 1
                   and found[0].get("conclusion") == "success" for expected in expected_steps)


def expected_assets(release: dict, name: str) -> dict[str, dict]:
    assets = release.get("assets")
    if not isinstance(assets, list) or any(not isinstance(a, dict) for a in assets):
        raise RuntimeError("Native release assets are missing or malformed")
    selected = {}
    for filename in (name, name + ".sha256"):
        found = [a for a in assets if a.get("name") == filename]
        if len(found) != 1:
            raise RuntimeError(f"Native release must contain exactly one {filename}")
        asset = found[0]
        limit = 4096 if filename.endswith(".sha256") else MAX_COMPONENT_BYTES
        if (asset.get("state") != "uploaded" or not isinstance(asset.get("size"), int)
                or isinstance(asset["size"], bool) or not 0 < asset["size"] <= limit
                or not isinstance(asset.get("id"), int) or isinstance(asset["id"], bool) or asset["id"] <= 0
                or asset.get("browser_download_url") != f"https://github.com/{REPOSITORY}/releases/download/{release['tag_name']}/{filename}"):
            raise RuntimeError(f"Native asset {filename} is incomplete or has an unexpected identity")
        digest = asset.get("digest")
        if digest is not None and not re.fullmatch(r"sha256:[0-9a-f]{64}", str(digest)):
            raise RuntimeError("Native asset has a malformed GitHub digest")
        selected[filename] = asset
    return selected


def verify_release(release: dict, expected: tuple[str, str], api: GitHub) -> dict:
    tag = release.get("tag_name", "")
    match = STABLE_TAG.fullmatch(tag)
    if not match or release.get("draft") is not False or release.get("prerelease") is not False:
        raise RuntimeError("Only published stable native components may be reused")
    native = match[1] == "mac-v"
    source = marker(release.get("body", ""), "Built and tested release source")
    if not SHA.fullmatch(source) or api.resolve_tag(tag) != source or not api.on_main(source):
        raise RuntimeError("Native component tag does not identify its tested main source")
    actual = api.source_metadata(source)
    if actual != expected:
        raise ReleaseUnavailable("Native component version/build differs from the frozen Windows source")
    if native and match[2] != actual[0]:
        raise RuntimeError("Native release tag and source version disagree")
    if not native and marker(release.get("body", ""), "macOS bundle version") != actual[0]:
        raise RuntimeError("Legacy release declares the wrong native component version")
    name = f"Evict-{actual[0]}-macOS-universal.dmg" if native else f"Evict_{match[2]}.dmg"
    assets = expected_assets(release, name)
    run = api.verified_run(release, source, native)
    for asset in assets.values():
        if timestamp(asset.get("updated_at")) > timestamp(run.get("updated_at")):
            raise RuntimeError("Native release assets changed after the successful verification run")
    return {"release": release, "source": source, "version": actual[0], "build": actual[1],
            "assets": assets, "name": name, "run_id": run["id"]}


def choose_release(releases: list[dict], expected: tuple[str, str], api: GitHub) -> dict:
    candidates = []
    for release in releases:
        if release.get("draft") is not False or release.get("prerelease") is not False:
            continue
        match = STABLE_TAG.fullmatch(str(release.get("tag_name", "")))
        if not match:
            continue
        native = match[1] == "mac-v"
        if native and match[2] != expected[0]:
            continue
        if not native and f"macOS bundle version: {expected[0]}" not in str(release.get("body", "")).splitlines():
            continue
        candidates.append((native, tuple(map(int, match[2].split("."))), release))
    for _, _, release in sorted(candidates, key=lambda entry: (entry[0], entry[1]), reverse=True):
        try:
            return verify_release(release, expected, api)
        except ReleaseUnavailable:
            continue
    raise ReleaseUnavailable(f"No verified stable macOS {expected[0]} (build {expected[1]}) is available; finish the macOS pipeline first")


def download_verified(selection: dict, folder: Path, downloader: Callable = download_asset) -> tuple[Path, str]:
    folder.mkdir(parents=True, exist_ok=False)
    for asset in selection["assets"].values():
        target = downloader(asset, folder)
        with target.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if asset.get("digest") is not None and asset["digest"] != "sha256:" + digest:
            raise RuntimeError("Downloaded native asset differs from its GitHub digest")
    image = folder / selection["name"]
    expected = read_checksum(image.with_name(image.name + ".sha256"), image.name)
    with image.open("rb") as stream:
        actual = hashlib.file_digest(stream, "sha256").hexdigest()
    if actual != expected:
        raise RuntimeError("Native component SHA-256 verification failed")
    helper_path = Path(__file__).resolve().parents[1] / "windows/build/release_zip.py"
    spec = importlib.util.spec_from_file_location("release_zip", helper_path)
    helper = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(helper)
    try:
        with image.open("rb") as stream:
            helper.check_dmg(stream, image.stat().st_size, image.name)
    except helper.ZipError as error:
        raise RuntimeError("Native component is not a valid disk image: " + str(error)) from error
    return image, actual


def package_component(selection: dict, image: Path, digest: str, output: Path,
                      windows_version: str, windows_source: str) -> dict:
    if not WINDOWS_VERSION.fullmatch(windows_version) or not SHA.fullmatch(windows_source):
        raise ValueError("Invalid Windows package identity")
    with image.open("rb") as stream:
        if hashlib.file_digest(stream, "sha256").hexdigest() != digest:
            raise RuntimeError("Native component changed before packaging")
    output.mkdir(parents=True, exist_ok=False)
    name = f"Evict_{windows_version}.dmg"
    shutil.copyfile(image, output / name)
    (output / (name + ".sha256")).write_text(digest + "\n", encoding="ascii")
    manifest = {
        "schema_version": 1, "mac_version": selection["version"], "mac_build": selection["build"],
        "release_tag": selection["release"]["tag_name"], "release_source": selection["source"],
        "verification_run_id": selection["run_id"], "asset_name": selection["name"],
        "asset_id": selection["assets"][selection["name"]]["id"], "sha256": digest,
        "size": image.stat().st_size, "packaged_name": name,
        "windows_version": windows_version, "windows_source": windows_source,
    }
    (output / "mac-provenance.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    return manifest


def verify_published(api: GitHub, manifest: dict, windows_version: str, windows_source: str,
                     expected: tuple[str, str], channel: str, out: Path, tag_source: str | None = None) -> None:
    # A PR beta is published on the tag reserved for its reviewed PR source (BT2); the tested source is that
    # commit plus managed version metadata. Stable tags identify the tested source itself.
    if channel == "beta" and (tag_source is None or not SHA.fullmatch(tag_source)):
        raise RuntimeError("A published beta needs the source of its reserved tag")
    expected_tag_target = tag_source if channel == "beta" else windows_source
    if (not isinstance(manifest, dict) or manifest.get("schema_version") != 1 or manifest.get("windows_version") != windows_version
            or manifest.get("windows_source") != windows_source or manifest.get("mac_version") != expected[0]
            or manifest.get("mac_build") != expected[1] or manifest.get("packaged_name") != f"Evict_{windows_version}.dmg"
            or not re.fullmatch(r"[0-9a-f]{64}", str(manifest.get("sha256", "")))):
        raise RuntimeError("Transferred Mac provenance does not match the frozen package")
    origin_tag = manifest.get("release_tag", "")
    if not STABLE_TAG.fullmatch(origin_tag):
        raise RuntimeError("Transferred native origin tag is invalid")
    origin = api.get("/releases/tags/" + urllib.parse.quote(origin_tag, safe=""))
    selection = verify_release(origin, expected, api)
    if (selection["source"] != manifest.get("release_source") or selection["run_id"] != manifest.get("verification_run_id")
            or selection["name"] != manifest.get("asset_name")
            or selection["assets"][selection["name"]]["id"] != manifest.get("asset_id")):
        raise RuntimeError("Native origin provenance changed after packaging")
    tag = "win-v" + windows_version
    release = api.get("/releases/tags/" + tag)
    if (release.get("tag_name") != tag or release.get("draft") is not False
            or release.get("prerelease") != (channel == "beta")
            or api.resolve_tag(tag, allow_beta=True) != expected_tag_target
            or marker(release.get("body", ""), "Built and tested release source") != windows_source
            or api.source_metadata(windows_source) != expected):
        raise RuntimeError("Published Windows release does not identify the frozen native component source")
    published = {"name": manifest["packaged_name"], "assets": expected_assets(release, manifest["packaged_name"])}
    out.mkdir(parents=True, exist_ok=False)
    _, origin_digest = download_verified(selection, out / "origin")
    image, published_digest = download_verified(published, out / "published")
    if origin_digest != manifest["sha256"] or published_digest != origin_digest or image.stat().st_size != manifest.get("size"):
        raise RuntimeError("Published Mac installer differs from the already verified native release")
    print(f"Published macOS bytes equal {origin_tag} from verification run {selection['run_id']}; no native rebuild was required")


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--source-file", type=Path, default=Path("macos/Sources/EvictKit/Version.swift"))
    parser.add_argument("--windows-version", required=True)
    parser.add_argument("--source-sha", required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--github-output", type=Path)
    parser.add_argument("--wait-seconds", type=int, default=0)
    parser.add_argument("--verify-published", action="store_true")
    parser.add_argument("--provenance", type=Path)
    parser.add_argument("--channel", choices=("beta", "stable"))
    parser.add_argument("--tag-source", help="commit the reserved beta tag points at (beta channel only)")
    args = parser.parse_args()
    if not WINDOWS_VERSION.fullmatch(args.windows_version) or not SHA.fullmatch(args.source_sha) or not 0 <= args.wait_seconds <= 1200:
        parser.error("Invalid version, source identity or wait limit")
    expected = native_metadata(args.source_file.read_text(encoding="utf-8-sig"))
    api = GitHub()
    if args.verify_published:
        if args.provenance is None or args.channel is None:
            parser.error("Published verification needs --provenance and --channel")
        if args.provenance.is_symlink() or args.provenance.stat().st_size > 16384:
            raise RuntimeError("Unsafe native provenance file")
        verify_published(api, json.loads(args.provenance.read_text()), args.windows_version, args.source_sha, expected,
                         args.channel, args.out, args.tag_source or None)
        return
    deadline = time.monotonic() + args.wait_seconds
    while True:
        try:
            selection = choose_release(api.releases(), expected, api)
            break
        except ReleaseUnavailable as error:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                raise
            print(str(error) + "; waiting for native publication", flush=True)
            time.sleep(min(30, remaining))
    with tempfile.TemporaryDirectory(prefix="evict-native-reuse-") as temporary:
        image, digest = download_verified(selection, Path(temporary) / "download")
        manifest = package_component(selection, image, digest, args.out, args.windows_version, args.source_sha)
    if args.github_output:
        with args.github_output.open("a", encoding="utf-8") as stream:
            stream.write(f"version={expected[0]}\nrelease_tag={manifest['release_tag']}\nrelease_source={manifest['release_source']}\n")
            stream.write(f"sha256={manifest['sha256']}\nverification_run_id={manifest['verification_run_id']}\n")
            stream.write(f"artifact=Evict-macOS-{args.source_sha}\n")
    print(f"Reused verified macOS {expected[0]} (build {expected[1]}) from {manifest['release_tag']}")


if __name__ == "__main__":
    main()
