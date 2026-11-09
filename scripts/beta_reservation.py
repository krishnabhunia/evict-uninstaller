#!/usr/bin/env python3
"""Reserve the next short PR beta number (design BT2) by creating its tag.

A PR test build is called ``<x.y.z>-beta<NNN>`` (NNN = 001..999, zero-padded, one SemVer identifier).
The counter is per core version x.y.z and shared by every PR of the repository:

1. list ``refs/tags/<prefix><core>-beta`` and keep exact ``<prefix><core>-beta<NNN>`` tags (old long
   ``-beta.<run>.<pr>.<attempt>`` tags are ignored), peeling annotated tags to their commit;
2. a tag that already points at the source commit is reused (a re-run of the same commit);
3. otherwise the next number is ``max + 1`` (or 001); 1000 fails and asks for a version bump;
4. the tag is created with ``POST /git/refs``; when another build created that tag first (422) the
   list is read again, up to ``MAX_ATTEMPTS`` times.

A number reserved by a build that later fails is simply skipped; gaps in the sequence are normal.
This script creates exactly one tag ref and nothing else. Only the Python standard library is used.

CI runs it only with ``--dry-run`` and a read-only token (it is pull-request code): the tag itself is created
by the trusted workflow-side twin of this algorithm in the ``reserve-beta`` job of .github/workflows/windows.yml,
which never checks out or executes pull-request code. scripts/tests/test_beta_reservation.py runs both against
the same fake GitHub so they cannot drift apart.
"""
from __future__ import annotations

import argparse
import json
import os
from pathlib import Path
import re
import sys
from typing import Any, Callable
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlsplit
from urllib.request import Request, urlopen

MAX_NUMBER = 999
MAX_ATTEMPTS = 10
CORE = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)")
PREFIX = re.compile(r"[A-Za-z][A-Za-z0-9-]*")
SHA = re.compile(r"[0-9a-f]{40}")
REPOSITORY = re.compile(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+")


class ReservationError(RuntimeError):
    pass


def short_tag(prefix: str, core: str, number: int) -> str:
    return f"{prefix}{core}-beta{number:03d}"


def short_number(tag: str, prefix: str, core: str) -> int | None:
    """The NNN of an exact short beta tag for this core, else None (old long tags, other cores)."""
    match = re.fullmatch(re.escape(prefix + core) + r"-beta([0-9]{3})", tag)
    if not match or match[1] == "000":
        return None
    return int(match[1])


def choose(existing: dict[int, str], sha: str) -> tuple[int, bool]:
    """(number, reused) for this source commit, given number -> commit of the existing short tags."""
    reused = sorted(number for number, target in existing.items() if target == sha)
    if reused:
        return reused[0], True
    number = max(existing, default=0) + 1
    if number > MAX_NUMBER:
        raise ReservationError(
            f"All {MAX_NUMBER} beta numbers of this version are used. Bump the version (a new x.y.z starts again at 001).")
    return number, False


class GitHubRefs:
    """The three Git refs calls the reservation needs, through an injectable opener for tests."""

    def __init__(self, repository: str, token: str | None, opener: Callable[..., Any] = urlopen,
                 api: str = "https://api.github.com"):
        if not REPOSITORY.fullmatch(repository):
            raise ReservationError("Invalid GitHub repository identity.")
        self.repository = repository
        self.token = token
        self.opener = opener
        self.api = api.rstrip("/")

    def _request(self, method: str, url: str, body: dict[str, Any] | None = None) -> tuple[int, Any, str]:
        parsed = urlsplit(url)
        if (parsed.scheme != "https" or parsed.netloc != urlsplit(self.api).netloc
                or not (parsed.path.startswith(f"/repos/{self.repository}/git/")
                        or re.match(r"/repositories/[0-9]+/git/", parsed.path))):
            raise ReservationError("Refusing an unexpected GitHub API address.")
        headers = {"Accept": "application/vnd.github+json", "User-Agent": "Evict-beta-reservation",
                   "X-GitHub-Api-Version": "2022-11-28"}
        if self.token:
            headers["Authorization"] = "Bearer " + self.token
        data = None
        if body is not None:
            data = json.dumps(body).encode("utf-8")
            headers["Content-Type"] = "application/json"
        try:
            with self.opener(Request(url, data=data, headers=headers, method=method), timeout=30) as response:
                raw = response.read()
                return response.status, json.loads(raw) if raw else None, response.headers.get("Link", "") or ""
        except HTTPError as error:
            return error.code, None, ""
        except (URLError, TimeoutError, OSError, ValueError) as error:
            raise ReservationError(f"GitHub could not be reached for the beta reservation ({type(error).__name__}).") from None

    def matching_tags(self, name_prefix: str) -> list[dict[str, Any]]:
        url: str | None = f"{self.api}/repos/{self.repository}/git/matching-refs/tags/{quote(name_prefix)}?per_page=100"
        refs: list[dict[str, Any]] = []
        visited: set[str] = set()
        while url:
            if url in visited or len(visited) >= 20:
                raise ReservationError("GitHub returned incomplete tag pagination.")
            visited.add(url)
            status, page, link = self._request("GET", url)
            if status != 200 or not isinstance(page, list):
                raise ReservationError(f"Could not list existing beta tags (GitHub HTTP {status}).")
            refs.extend(item for item in page if isinstance(item, dict))
            matches = re.findall(r'<([^>]+)>\s*;\s*rel="next"', link)
            url = matches[0] if matches else None
        return refs

    def peel(self, target: dict[str, Any]) -> str | None:
        for _ in range(5):
            if target.get("type") == "commit":
                sha = target.get("sha")
                return sha if isinstance(sha, str) and SHA.fullmatch(sha) else None
            if target.get("type") != "tag" or not SHA.fullmatch(str(target.get("sha") or "")):
                return None
            status, tag, _ = self._request("GET", f"{self.api}/repos/{self.repository}/git/tags/{target['sha']}")
            if status != 200 or not isinstance(tag, dict) or not isinstance(tag.get("object"), dict):
                raise ReservationError(f"Could not read an annotated beta tag (GitHub HTTP {status}).")
            target = tag["object"]
        return None

    def create_tag(self, tag: str, sha: str) -> int:
        status, _, _ = self._request("POST", f"{self.api}/repos/{self.repository}/git/refs",
                                     {"ref": "refs/tags/" + tag, "sha": sha})
        return status


def existing_numbers(refs: GitHubRefs, prefix: str, core: str) -> dict[int, str]:
    found: dict[int, str] = {}
    for ref in refs.matching_tags(f"{prefix}{core}-beta"):
        name = str(ref.get("ref") or "").removeprefix("refs/tags/")
        number = short_number(name, prefix, core)
        if number is None or not isinstance(ref.get("object"), dict):
            continue
        commit = refs.peel(ref["object"])
        if commit:
            found[number] = commit
    return found


def reserve(refs: GitHubRefs, core: str, prefix: str, sha: str, attempts: int = MAX_ATTEMPTS,
            dry_run: bool = False) -> dict[str, Any]:
    if not CORE.fullmatch(core):
        raise ReservationError("The core version must be x.y.z.")
    if not PREFIX.fullmatch(prefix):
        raise ReservationError("Invalid tag prefix.")
    if not SHA.fullmatch(sha):
        raise ReservationError("The source must be a full 40-character commit SHA.")
    for _ in range(attempts):
        number, reused = choose(existing_numbers(refs, prefix, core), sha)
        tag = short_tag(prefix, core, number)
        if not reused and not dry_run:
            status = refs.create_tag(tag, sha)
            if status == 422:
                continue  # another build took this number first: read the list again
            if status != 201:
                raise ReservationError(f"Could not create beta tag {tag} (GitHub HTTP {status}).")
        return {"beta_number": f"{number:03d}", "version": f"{core}-beta{number:03d}", "tag": tag,
                "source_sha": sha, "reused": reused}
    raise ReservationError(f"Could not reserve a beta number for {core} after {attempts} attempts; try the run again.")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--repository", default=os.environ.get("GITHUB_REPOSITORY", ""))
    parser.add_argument("--core", required=True, help="x.y.z the beta tests")
    parser.add_argument("--prefix", required=True, help="tag prefix, e.g. win-v")
    parser.add_argument("--sha", required=True, help="the PR source commit (never the synthetic merge commit)")
    parser.add_argument("--github-output", type=Path)
    parser.add_argument("--dry-run", action="store_true", help="only report the number that would be reserved")
    arguments = parser.parse_args(argv)
    try:
        token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
        api = os.environ.get("GITHUB_API_URL") or "https://api.github.com"
        result = reserve(GitHubRefs(arguments.repository, token, api=api), arguments.core, arguments.prefix, arguments.sha,
                         dry_run=arguments.dry_run)
        if arguments.github_output:
            with arguments.github_output.open("a", encoding="utf-8") as stream:
                for key, value in result.items():
                    stream.write(f"{key}={str(value).lower() if isinstance(value, bool) else value}\n")
        print(json.dumps(result, sort_keys=True))
        return 0
    except ReservationError as error:
        print("Beta reservation failed: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
