#!/usr/bin/env python3
"""Fetch verified Windows components from the latest stable release on main.

Mac delivery bundles use a separately versioned Mac app. Windows PR betas and
off-main test releases are never imported into a stable Mac delivery bundle.
"""
from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import subprocess
import urllib.parse
import urllib.request
from pathlib import Path
from typing import Callable

REPOSITORY = "krishnabhunia/evict-uninstaller"
API_ROOT = f"https://api.github.com/repos/{REPOSITORY}"
CORE_PATTERN = r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)"
TAG_PATTERN = re.compile(rf"(?:win-v|v){CORE_PATTERN}\Z")
MAX_PAGES = 100
MAX_API_BYTES = 16 * 1024 * 1024
MAX_COMPONENT_BYTES = 2 * 1024 * 1024 * 1024
CDN_HOSTS = frozenset(("release-assets.githubusercontent.com", "objects.githubusercontent.com"))


class NoApiRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        raise RuntimeError("Unexpected redirect from the GitHub releases API")


class ReleaseAssetRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, request, response, code, message, headers, new_url):
        parsed = urllib.parse.urlsplit(new_url)
        if (
            parsed.scheme != "https"
            or parsed.hostname not in CDN_HOSTS
            or parsed.username is not None
            or parsed.password is not None
            or parsed.port not in (None, 443)
        ):
            raise RuntimeError("Release asset redirect left the trusted GitHub CDN")
        # Asset requests never carry GH_TOKEN or Authorization, including redirects.
        return super().redirect_request(request, response, code, message, headers, new_url)


def get_releases_page(page: int) -> list[dict]:
    if not 1 <= page <= MAX_PAGES:
        raise ValueError("Release page is outside the finite pagination limit")
    url = f"{API_ROOT}/releases?per_page=100&page={page}"
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "Evict-release-bundle"}
    token = os.environ.get("GH_TOKEN")
    if token:
        headers["Authorization"] = f"Bearer {token}"
    request = urllib.request.Request(url, headers=headers)
    with urllib.request.build_opener(NoApiRedirect()).open(request, timeout=30) as response:
        raw = response.read(MAX_API_BYTES + 1)
    if len(raw) > MAX_API_BYTES:
        raise RuntimeError("GitHub release response exceeded its size limit")
    releases = json.loads(raw)
    if not isinstance(releases, list) or any(not isinstance(item, dict) for item in releases):
        raise RuntimeError("GitHub releases API returned an unexpected response")
    return releases


def list_releases(fetch_page: Callable[[int], list[dict]] = get_releases_page) -> list[dict]:
    releases: list[dict] = []
    for page in range(1, MAX_PAGES + 1):
        items = fetch_page(page)
        if not isinstance(items, list) or any(not isinstance(item, dict) for item in items):
            raise RuntimeError("GitHub releases API returned an unexpected response")
        if len(items) > 100:
            raise RuntimeError("GitHub releases page exceeded per_page=100")
        releases.extend(items)
        if len(items) < 100:
            return releases
    raise RuntimeError("GitHub releases exceeded the finite pagination limit")


def tag_is_on_main(tag: str, run: Callable = subprocess.run) -> bool:
    if not TAG_PATTERN.fullmatch(tag):
        raise ValueError("Only full stable Windows version tags may be resolved")
    source = run(
        ["git", "rev-parse", "--verify", f"refs/tags/{tag}^{{commit}}"],
        text=True, capture_output=True, check=False,
    )
    if source.returncode or not re.fullmatch(r"[0-9a-f]{40}", source.stdout.strip()):
        raise RuntimeError(f"Cannot resolve {tag}; fetch origin/main and all tags before packaging")
    reachable = run(
        ["git", "merge-base", "--is-ancestor", source.stdout.strip(), "origin/main"],
        text=True, capture_output=True, check=False,
    )
    if reachable.returncode not in (0, 1):
        raise RuntimeError("Cannot check release tag ancestry against origin/main")
    return reachable.returncode == 0


def choose_release(releases: list[dict], on_main: Callable[[str], bool] = tag_is_on_main) -> tuple[str, dict]:
    candidates = []
    for release in releases:
        if release.get("draft") is not False or release.get("prerelease") is not False:
            continue
        tag = release.get("tag_name")
        match = TAG_PATTERN.fullmatch(tag) if isinstance(tag, str) else None
        if match:
            core = tuple(int(part) for part in match.groups())
            if any(part > 65535 for part in core):
                raise RuntimeError("Published Windows version exceeds the native numeric version limit")
            candidates.append((core, tag, release))
    for core, tag, release in sorted(candidates, key=lambda item: (item[0], item[1]), reverse=True):
        if on_main(tag):
            return ".".join(str(part) for part in core), release
    raise RuntimeError("No published stable Windows release is reachable from main")


def expected_assets(version: str, release: dict) -> dict[str, dict]:
    tag = release["tag_name"]
    names = (
        f"Evict-Setup-{version}.exe", f"Evict-Setup-{version}.exe.sha256",
        "Evict.exe", "Evict.exe.sha256",
    )
    assets = release.get("assets")
    if not isinstance(assets, list) or any(not isinstance(item, dict) for item in assets):
        raise RuntimeError("Release asset metadata is missing or malformed")
    selected = {}
    for name in names:
        matches = [asset for asset in assets if asset.get("name") == name]
        if len(matches) != 1:
            raise RuntimeError(f"Release must contain exactly one {name}")
        asset = matches[0]
        size = asset.get("size")
        limit = 4096 if name.endswith(".sha256") else MAX_COMPONENT_BYTES
        if isinstance(size, bool) or not isinstance(size, int) or not 0 < size <= limit:
            raise RuntimeError(f"Release asset {name} has an invalid size")
        if asset.get("state") != "uploaded":
            raise RuntimeError(f"Release asset {name} is not completely uploaded")
        expected_url = f"https://github.com/{REPOSITORY}/releases/download/{tag}/{name}"
        if asset.get("browser_download_url") != expected_url:
            raise RuntimeError(f"Release asset {name} has an unexpected download URL")
        selected[name] = asset
    return selected


def download_asset(asset: dict, folder: Path, opener=None) -> Path:
    destination = folder / asset["name"]
    temporary = destination.with_name(destination.name + ".download")
    if destination.exists() or temporary.exists():
        raise RuntimeError(f"Refusing to overwrite an existing component: {destination.name}")
    opener = opener or urllib.request.build_opener(ReleaseAssetRedirect())
    request = urllib.request.Request(
        asset["browser_download_url"], headers={"User-Agent": "Evict-release-bundle"}
    )
    written = 0
    try:
        with opener.open(request, timeout=60) as response, temporary.open("xb") as output:
            while chunk := response.read(1024 * 1024):
                written += len(chunk)
                if written > asset["size"]:
                    raise RuntimeError(f"Downloaded {asset['name']} exceeded its published size")
                output.write(chunk)
        if written != asset["size"]:
            raise RuntimeError(f"Downloaded {asset['name']} differs from its published size")
        temporary.rename(destination)
        return destination
    except Exception:
        if temporary.exists():
            temporary.unlink()
        raise


def read_checksum(sidecar: Path, expected_name: str) -> str:
    text = sidecar.read_text(encoding="utf-8").strip()
    match = re.fullmatch(r"([0-9a-fA-F]{64})(?:[ \t]+\*?([^\r\n]+))?", text)
    if not match or (match.group(2) is not None and match.group(2) != expected_name):
        raise RuntimeError(f"Malformed or mismatched checksum for {expected_name}")
    return match.group(1).lower()


def verify_components(folder: Path, version: str) -> tuple[Path, Path]:
    paths = (
        folder / "windows-installer" / f"Evict-Setup-{version}.exe",
        folder / "portable" / "Evict.exe",
    )
    for path in paths:
        checksum = read_checksum(path.with_name(path.name + ".sha256"), path.name)
        with path.open("rb") as file:
            digest = hashlib.file_digest(file, "sha256").hexdigest()
        if digest != checksum:
            raise RuntimeError(f"SHA-256 verification failed for {path.name}")
    return paths


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--out", type=Path, required=True, help="New directory for verified Windows components")
    parser.add_argument("--github-output", type=Path)
    arguments = parser.parse_args()
    subprocess.run(
        ["git", "rev-parse", "--verify", "origin/main^{commit}"], check=True,
        text=True, capture_output=True,
    )
    version, release = choose_release(list_releases())
    assets = expected_assets(version, release)
    arguments.out.mkdir(parents=True, exist_ok=False)
    for asset in assets.values():
        component = "windows-installer" if asset["name"].startswith("Evict-Setup-") else "portable"
        folder = arguments.out / component
        folder.mkdir(exist_ok=True)
        download_asset(asset, folder)
    setup, executable = verify_components(arguments.out, version)
    if arguments.github_output:
        with arguments.github_output.open("a", encoding="utf-8") as output:
            output.write(f"windows_version={version}\n")
            output.write(f"windows_setup={setup.resolve()}\n")
            output.write(f"windows_exe={executable.resolve()}\n")
    print(f"Verified Windows {version} from {release['tag_name']} on main")


if __name__ == "__main__":
    main()
