#!/usr/bin/env python3
"""Read-only CI planning; export the exact two generated native metadata files."""
import argparse
import importlib.util
import json
import os
from pathlib import Path
import re
import subprocess
import sys
from urllib.error import HTTPError, URLError
from urllib.request import HTTPRedirectHandler, Request, build_opener

ROOT = Path(__file__).resolve().parents[2]
ALLOWED = ("macos/Sources/EvictKit/Version.swift", "macos/CHANGELOG.md")


class NoAPIRedirect(HTTPRedirectHandler):
    """Repository credentials must never follow a redirect to an asset or another host."""

    def redirect_request(self, request, file, code, message, headers, new_url):
        return None


def native_release_metadata(repository, token, opener=None):
    """Include draft release reservations as well as published releases, with bounded pagination."""
    if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
        raise ValueError("Invalid repository for native release planning")
    opener = opener or build_opener(NoAPIRedirect()).open
    headers = {"Accept": "application/vnd.github+json", "User-Agent": "Evict-native-release-planner",
               "X-GitHub-Api-Version": "2022-11-28"}
    if token:
        headers["Authorization"] = "Bearer " + token
    releases = []
    for page in range(1, 21):
        url = f"https://api.github.com/repos/{repository}/releases?per_page=100&page={page}"
        try:
            with opener(Request(url, headers=headers), timeout=30) as response:
                data = json.load(response)
        except (HTTPError, URLError, TimeoutError, OSError, ValueError):
            raise ValueError("Could not inspect native release reservations; no version was allocated") from None
        if not isinstance(data, list) or any(not isinstance(item, dict) for item in data):
            raise ValueError("Invalid native release reservation metadata")
        releases.extend(data)
        if len(data) < 100:
            return releases
    raise ValueError("Native release reservations exceed the supported pagination window")


def reserve_beta_version(core, proposed_version, tags, releases):
    """Never reuse a compact beta number, even after tag creation or a draft upload failed."""
    if not re.fullmatch(r"(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)", core):
        raise ValueError("Invalid native core version")
    prefix = "mac-v" + core + "-beta"
    proposed = re.fullmatch(re.escape(core) + r"-beta([0-9]{3})", proposed_version)
    if not proposed or int(proposed[1]) == 0:
        raise ValueError("Invalid proposed native beta version")
    # A draft may precede tag creation; a tag may precede the release or its asset upload.
    # Both are durable reservations. Never delete/move either merely to recycle a number.
    identifiers = list(tags) + [str(item.get("tag_name", "")) for item in releases]
    used = []
    for identifier in identifiers:
        match = re.fullmatch(re.escape(prefix) + r"([0-9]{3})", identifier)
        if match:
            serial = int(match[1])
            if serial == 0:
                raise ValueError("Native beta 000 is reserved")
            used.append(serial)
    serial = max([int(proposed[1]) - 1, *used]) + 1
    if serial > 999:
        raise ValueError("Three-digit native beta series exhausted; advance the macOS core version")
    return f"{core}-beta{serial:03d}"


def require_safe_stable_retry(plan, tags, releases):
    """An immutable stable tag is a reservation, not proof of successful publication.

    A full new run must not silently skip an incomplete release or rebuild different bytes
    under its existing tag. The original failed publish job can resume from its tested artifact.
    """
    tag = plan["tag"]
    if tag not in set(tags):
        return
    instructions = (f"Native stable tag {tag} is already reserved. Re-run the failed publish job using "
                    "GitHub Actions 'Re-run failed jobs' so it keeps the original frozen source and artifact. "
                    "If those artifacts have expired, prepare a new patch release; do not move or overwrite this tag.")
    if plan["publish"]:
        raise ValueError(instructions)
    candidates = [item for item in releases if item.get("tag_name") == tag]
    if len(candidates) != 1:
        raise ValueError("The existing native stable tag has no complete published release. " + instructions)
    release = candidates[0]
    if release.get("draft") is not False or release.get("prerelease") is not False or not release.get("published_at"):
        raise ValueError("The native stable release is still incomplete. " + instructions)
    version = plan["core_version"]
    expected = {f"Evict-{version}-macOS-universal.{extension}{suffix}"
                for extension in ("zip", "dmg") for suffix in ("", ".sha256")}
    assets = release.get("assets")
    if not isinstance(assets, list):
        raise ValueError("The native stable release has no asset manifest. " + instructions)
    for name in expected:
        matches = [asset for asset in assets if isinstance(asset, dict) and asset.get("name") == name]
        if (len(matches) != 1 or matches[0].get("state") != "uploaded"
                or not isinstance(matches[0].get("size"), int) or matches[0]["size"] <= 0):
            raise ValueError("The native stable release has missing or incomplete assets. " + instructions)


def changelog_notes(text, core):
    match = re.search(r"^## " + re.escape(core) + r"(?:\s[^\n]*)?\n(.*?)(?=^## |\Z)", text, re.M | re.S)
    if not match:
        raise ValueError("Generated native changelog does not describe this release")
    return re.sub(r"<!--.*?-->\s*", "", match[1], flags=re.S).strip() + "\n"


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--mode", choices=("beta", "stable"), required=True)
    parser.add_argument("--out", required=True)
    args = parser.parse_args()
    command = [sys.executable, str(ROOT / "scripts/versioning.py"), "plan", "--platform", "macos",
               "--mode", args.mode, "--sync", "--event-file", os.environ["GITHUB_EVENT_PATH"],
               "--ref", os.environ["GITHUB_REF"], "--main-ref", "origin/main"]
    if args.mode == "beta":
        command += ["--beta-sequence", os.environ["GITHUB_RUN_NUMBER"] + "." + os.environ["GITHUB_RUN_ATTEMPT"]]
    plan = json.loads(subprocess.check_output(command, cwd=ROOT, text=True))
    tags = subprocess.check_output(["git", "tag", "--list"], cwd=ROOT, text=True).splitlines()
    repository = os.environ.get("GITHUB_REPOSITORY", "krishnabhunia/evict-uninstaller")
    releases = native_release_metadata(repository, os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN"))
    if args.mode == "beta":
        plan["version"] = reserve_beta_version(plan["core_version"], plan["version"], tags, releases)
        plan["tag"] = "mac-v" + plan["version"]
    else:
        require_safe_stable_retry(plan, tags, releases)
    changed = subprocess.check_output(["git", "diff", "--name-only"], cwd=ROOT, text=True).splitlines()
    if not set(changed).issubset(ALLOWED):
        raise ValueError("Generated files escaped the native metadata allowlist")
    notes = changelog_notes((ROOT / ALLOWED[1]).read_text(), plan["core_version"])
    if plan["publish"]:
        spec = importlib.util.spec_from_file_location("release_notes_check", ROOT / "scripts/release_notes_check.py")
        helper = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(helper)
        if not helper.meaningful_changes(notes):
            raise ValueError("Describe implementations in the PR Changes section before releasing")
    output = Path(args.out)
    output.mkdir(parents=True, exist_ok=True)
    (output / "manifest.json").write_text(json.dumps({"plan": plan, "files": {p: (ROOT / p).read_text() for p in ALLOWED}, "notes": notes}), encoding="utf-8")
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as stream:
        for key in ("version", "core_version", "source_sha", "publish"):
            value = plan[key]
            stream.write(f"{key}={str(value).lower() if isinstance(value, bool) else value}\n")


if __name__ == "__main__":
    main()
