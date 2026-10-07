#!/usr/bin/env python3
"""Plan platform releases from published stable tags and unreleased change intent.

This script never commits, pushes, creates tags, or publishes releases. --sync writes
only the selected platform's generated version/changelog files for the caller to
commit with a compare-and-swap push, test, and publish from the resulting exact SHA.
"""
from __future__ import annotations

import argparse
from dataclasses import dataclass
from datetime import date
from enum import IntEnum
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import tempfile
from typing import Any, Callable
from urllib.error import HTTPError, URLError
from urllib.parse import quote, urlsplit
from urllib.request import Request, urlopen


class VersionError(RuntimeError):
    pass


class Bump(IntEnum):
    NONE = 0
    PATCH = 1
    MINOR = 2
    MAJOR = 3

    @classmethod
    def parse(cls, value: str) -> "Bump":
        try:
            return cls[value.strip().upper()]
        except KeyError as error:
            raise VersionError("Release intent must be major, minor, patch, or none.") from error


@dataclass(frozen=True, order=True)
class CoreVersion:
    major: int
    minor: int
    patch: int

    @classmethod
    def parse(cls, value: str) -> "CoreVersion":
        if not re.fullmatch(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)", value):
            raise VersionError("Expected a stable major.minor.patch version.")
        return cls(*(int(part) for part in value.split(".")))

    def bump(self, intent: Bump) -> "CoreVersion":
        if intent == Bump.MAJOR:
            return CoreVersion(self.major + 1, 0, 0)
        if intent == Bump.MINOR:
            return CoreVersion(self.major, self.minor + 1, 0)
        if intent == Bump.PATCH:
            return CoreVersion(self.major, self.minor, self.patch + 1)
        return self

    def __str__(self) -> str:
        return f"{self.major}.{self.minor}.{self.patch}"


PREFIX = {"windows": "win-v", "macos": "mac-v"}
GENERATED = {
    "windows": ("windows/Directory.Build.props", "windows/installer/Evict.iss", "windows/CHANGELOG.md"),
    "macos": ("macos/Sources/EvictKit/Version.swift", "macos/CHANGELOG.md"),
}
CONVENTIONAL = re.compile(r"^([a-z][a-z0-9-]*)(?:\(([^)\r\n]+)\))?(!)?:\s+\S", re.I)


def stable_tag_version(tag: str, platform: str) -> CoreVersion | None:
    prefixes = (PREFIX[platform], "v") if platform == "windows" else (PREFIX[platform],)
    for prefix in prefixes:
        if tag.startswith(prefix):
            try:
                return CoreVersion.parse(tag[len(prefix):])
            except VersionError:
                return None
    return None


def highest_stable_tag(tags: list[str], platform: str) -> tuple[str, CoreVersion] | None:
    choices = [(version, tag.startswith(PREFIX[platform]), tag)
               for tag in tags if (version := stable_tag_version(tag, platform)) is not None]
    if not choices:
        return None
    version, _, tag = max(choices)
    return tag, version


def is_document_or_test(path: str) -> bool:
    path = path.replace("\\", "/").lower()
    components = path.split("/")
    return (path.endswith((".md", ".rst")) or path.startswith(("docs/", ".github/issue_template/"))
            or any(part in ("tests", "test", "__tests__") or part.endswith(".tests") for part in components))


def affects_platform(paths: list[str], platform: str) -> bool:
    for path in paths:
        path = path.replace("\\", "/")
        if is_document_or_test(path):
            continue
        if path.startswith(platform + "/") or path.startswith("scripts/"):
            return True
        if path == f".github/workflows/{'windows' if platform == 'windows' else 'macos'}.yml":
            return True
        if path in (".github/release-policy.json", "Directory.Build.props", "global.json"):
            return True
    return False


def generated_release_commit(message: str, paths: list[str], platform: str) -> CoreVersion | None:
    match = re.match(rf"^chore\(release\):\s+{platform}\s+([0-9]+\.[0-9]+\.[0-9]+)(?:\s|$)", message, re.I)
    if not match or not paths or not set(paths).issubset(GENERATED[platform]):
        return None
    return CoreVersion.parse(match[1])


def conventional_intent(text: str, platform: str) -> Bump | None:
    first_line = text.splitlines()[0] if text.strip() else ""
    match = CONVENTIONAL.match(first_line)
    if not match:
        return None
    kind, scope, breaking = match.groups()
    if scope and ((scope.lower() == "windows" and platform != "windows")
                  or (scope.lower() in ("mac", "macos") and platform != "macos")):
        return Bump.NONE
    if breaking:
        return Bump.MAJOR
    if kind.lower() == "feat":
        return Bump.MINOR
    if kind.lower() in ("docs", "test", "style", "chore"):
        return Bump.NONE
    return Bump.PATCH


def release_intent(message: str, metadata: dict[str, Any] | None, platform: str) -> Bump:
    metadata = metadata or {}
    title = str(metadata.get("title") or "")
    body = str(metadata.get("body") or "")
    texts = (message, title, body)
    if any(re.search(r"(?im)^\s*BREAKING[ -]CHANGE\s*:", text) for text in texts):
        return Bump.MAJOR
    # A breaking Conventional Commit/title cannot be reduced by labels or directives.
    if any(conventional_intent(text, platform) == Bump.MAJOR for text in (message, title)):
        return Bump.MAJOR
    global_values: list[Bump] = []
    scoped_values: list[Bump] = []
    for label in metadata.get("labels") or []:
        name = label.get("name", "") if isinstance(label, dict) else str(label)
        match = re.fullmatch(r"release:(?:(windows|macos):)?(major|minor|patch|none)", name, re.I)
        if match:
            if match[1] is None:
                global_values.append(Bump.parse(match[2]))
            elif match[1].lower() == platform:
                scoped_values.append(Bump.parse(match[2]))
    for text in texts:
        for match in re.finditer(r"(?im)^\s*Release-Type(?:-(Windows|Macos))?\s*:\s*(major|minor|patch|none)\s*$", text):
            if match[1] is None:
                global_values.append(Bump.parse(match[2]))
            elif match[1].lower() == platform:
                scoped_values.append(Bump.parse(match[2]))
    if scoped_values:
        return max(scoped_values)
    conventional = [value for text in (message, title)
                    if (value := conventional_intent(text, platform)) is not None]
    declared = global_values + conventional
    return max(declared) if declared else Bump.PATCH


class Git:
    def __init__(self, root: Path):
        self.root = root.resolve()

    def run(self, *arguments: str) -> str:
        result = subprocess.run(["git", "-c", "core.quotepath=false", *arguments],
                                cwd=self.root, capture_output=True, text=True, encoding="utf-8", check=False)
        if result.returncode:
            raise VersionError("Git could not resolve complete release history: " + " ".join(arguments[:2]))
        return result.stdout.strip()

    def resolve(self, ref: str) -> str:
        return self.run("rev-parse", "--verify", "--end-of-options", ref + "^{commit}")

    def tags(self, target: str) -> list[str]:
        return self.run("tag", "--merged", target).splitlines()

    def merge_base(self, left: str, right: str) -> str:
        return self.run("merge-base", left, right)

    def paths(self, before: str | None, after: str) -> list[str]:
        if before:
            output = self.run("diff", "--name-only", "-z", "--no-renames", before, after, "--")
        else:
            output = self.run("diff-tree", "--root", "--no-commit-id", "--name-only", "-r", "-z", after, "--")
        return [path for path in output.split("\0") if path]

    def commits(self, base: str | None, target: str) -> list[tuple[str, str, list[str]]]:
        revisions = self.run("rev-list", "--first-parent", "--reverse", f"{base}..{target}" if base else target)
        result = []
        for sha in revisions.splitlines():
            parents = self.run("rev-list", "--parents", "-n", "1", sha).split()[1:]
            message = self.run("show", "-s", "--format=%B", sha)
            result.append((sha, message, self.paths(parents[0] if parents else None, sha)))
        return result


class GitHub:
    def __init__(self, repository: str, repository_id: int | None = None,
                 token: str | None = None, opener: Callable[..., Any] = urlopen):
        if not re.fullmatch(r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+", repository):
            raise VersionError("Invalid GitHub repository identity.")
        self.repository = repository
        self.repository_id = repository_id
        self.token = token
        self.opener = opener
        self.cache: dict[str, list[dict[str, Any]]] = {}

    def merged_pull_requests(self, sha: str) -> list[dict[str, Any]]:
        if sha in self.cache:
            return self.cache[sha]
        if not re.fullmatch(r"[0-9a-f]{40}", sha):
            raise VersionError("Invalid source commit identity.")
        suffix = f"/commits/{sha}/pulls"
        next_url: str | None = f"https://api.github.com/repos/{self.repository}{suffix}?per_page=100"
        visited: set[str] = set()
        pulls: list[dict[str, Any]] = []
        while next_url:
            parsed = urlsplit(next_url)
            allowed_paths = [f"/repos/{self.repository}{suffix}"]
            if self.repository_id:
                allowed_paths.append(f"/repositories/{self.repository_id}{suffix}")
            if (parsed.scheme != "https" or parsed.netloc != "api.github.com" or parsed.path not in allowed_paths
                    or next_url in visited or len(visited) >= 20):
                raise VersionError("GitHub returned incomplete or untrusted pull-request pagination.")
            visited.add(next_url)
            headers = {"Accept": "application/vnd.github+json", "User-Agent": "Evict-release-versioning",
                       "X-GitHub-Api-Version": "2022-11-28"}
            if self.token:
                headers["Authorization"] = "Bearer " + self.token
            try:
                with self.opener(Request(next_url, headers=headers), timeout=20) as response:
                    page = json.load(response)
                    link = response.headers.get("Link", "")
            except (HTTPError, URLError, TimeoutError, OSError, ValueError) as error:
                status = getattr(error, "code", None)
                raise VersionError(f"Could not read release intent for commit {sha[:7]}"
                                   + (f" (GitHub HTTP {status})." if status else ".")) from None
            if not isinstance(page, list):
                raise VersionError("GitHub returned invalid pull-request metadata.")
            pulls.extend(page)
            matches = re.findall(r'<([^>]+)>\s*;\s*rel="next"', link)
            if "next" in link and not matches:
                raise VersionError("GitHub returned incomplete pull-request pagination.")
            next_url = matches[0] if matches else None
        by_number = {}
        for pull in pulls:
            base = pull.get("base") or {}
            if (pull.get("merged_at") and base.get("ref") == "main"
                    and (base.get("repo") or {}).get("full_name") == self.repository):
                by_number[pull["number"]] = pull
        self.cache[sha] = list(by_number.values())
        return self.cache[sha]


def safe_path(root: Path, relative: str) -> Path:
    target = (root / relative).resolve()
    if not target.is_relative_to(root.resolve()):
        raise VersionError("Generated version files must stay inside the repository.")
    return target


def source_version(root: Path, platform: str, git: Git | None = None, commit: str | None = None) -> str:
    relative = GENERATED[platform][0]
    text = git.run("show", f"{commit}:{relative}") if git and commit else safe_path(root, relative).read_text(encoding="utf-8-sig")
    pattern = r"<Version>\s*([^<]+)\s*</Version>" if platform == "windows" else r'public static let current\s*=\s*"([^"]+)"'
    match = re.search(pattern, text)
    if not match:
        raise VersionError("The platform source version was not found.")
    CoreVersion.parse(match[1].strip())
    return match[1].strip()


def change_notes(message: str, pulls: list[dict[str, Any]]) -> list[str]:
    titles = [str(pull.get("title") or "") for pull in pulls] or [message.splitlines()[0]]
    return [re.sub(r"\s+", " ", title).strip() for title in titles if title.strip()]


def update_changelog(text: str, version: str, base_tag: str, notes: list[str], today: str) -> str:
    text = text.replace("\r\n", "\n")
    marker = f"<!-- Evict automatic release; base: {base_tag or 'initial'} -->"
    heading = re.search(r"(?m)^## (.+)$", text)
    if heading:
        next_heading = re.search(r"(?m)^## ", text[heading.end():])
        end = heading.end() + next_heading.start() if next_heading else len(text)
        first_section = text[heading.end():end]
        reusable = ("not published" in heading[1].lower() or heading[1].lower() == "unreleased"
                    or marker in first_section)
        same_version = bool(re.match(re.escape(version) + r"(?:\s|$)", heading[1]))
        if reusable:
            section = first_section.strip()
            section = re.sub(r"(?m)^<!-- Evict automatic release; base: .* -->\n?", "", section).strip()
            for note in notes:
                bullet = "- " + note
                if bullet not in section:
                    section += ("\n" if section else "") + bullet
            replacement = f"## {version} — {today}\n\n{marker}\n\n{section}\n\n"
            return text[:heading.start()] + replacement + text[end:].lstrip("\n")
        if same_version:
            return text
    bullets = "\n".join("- " + note for note in notes) or "- Maintenance changes."
    section = f"## {version} — {today}\n\n{marker}\n\n{bullets}\n\n"
    if heading:
        return text[:heading.start()] + section + text[heading.start():]
    return text.rstrip() + "\n\n" + section


def atomic_write(path: Path, content: str) -> bool:
    if path.read_text(encoding="utf-8-sig") == content:
        return False
    descriptor, temporary = tempfile.mkstemp(prefix=".evict-version-", dir=path.parent)
    try:
        with os.fdopen(descriptor, "w", encoding="utf-8", newline="") as stream:
            stream.write(content)
        os.replace(temporary, path)
    finally:
        if os.path.exists(temporary):
            os.unlink(temporary)
    return True


def sync_sources(root: Path, platform: str, core: CoreVersion, base_tag: str,
                 notes: list[str], today: str | None = None) -> list[str]:
    current = source_version(root, platform)
    replacements: dict[str, str] = {}
    version_path = GENERATED[platform][0]
    text = safe_path(root, version_path).read_text(encoding="utf-8-sig")
    if platform == "windows":
        replacements[version_path] = re.sub(r"(<Version>)\s*[^<]+\s*(</Version>)", rf"\g<1>{core}\g<2>", text, count=1)
        installer_path = GENERATED[platform][1]
        installer = safe_path(root, installer_path).read_text(encoding="utf-8-sig")
        if not re.search(r'#define MyAppVersion "[^"]+"', installer):
            raise VersionError("The installer fallback version was not found.")
        installer = re.sub(r'(#define MyAppVersion ")[^"]+(")', rf"\g<1>{core}\g<2>", installer, count=1)
        installer = re.sub(r'(?m)^(\s*#define MyAppNumericVersion )[^\r\n]+', rf'\g<1>"{core}.0"', installer, count=1)
        installer = re.sub(r"(/DMyAppVersion=)[0-9]+\.[0-9]+\.[0-9]+", rf"\g<1>{core}", installer, count=1)
        replacements[installer_path] = installer
    else:
        text = re.sub(r'(public static let current\s*=\s*")[^"]+(")', rf"\g<1>{core}\g<2>", text, count=1)
        if current != str(core):
            build = re.search(r'public static let build\s*=\s*"([0-9]+)"', text)
            if not build:
                raise VersionError("The macOS build number was not found.")
            text = re.sub(r'(public static let build\s*=\s*")[0-9]+(")', rf"\g<1>{int(build[1]) + 1}\g<2>", text, count=1)
        replacements[version_path] = text
    changelog_path = GENERATED[platform][-1]
    replacements[changelog_path] = update_changelog(safe_path(root, changelog_path).read_text(encoding="utf-8-sig"),
                                                   str(core), base_tag, notes, today or date.today().isoformat())
    # Validate every target before the first mutation.
    paths = {relative: safe_path(root, relative) for relative in replacements}
    return [relative for relative, content in replacements.items() if atomic_write(paths[relative], content)]


def build_plan(git: Git, github: Any, policy: dict[str, Any], platform: str, mode: str,
               event: dict[str, Any] | None = None, main_ref: str = "origin/main",
               beta_sequence: str | None = None, tag: str | None = None) -> dict[str, Any]:
    event = event or {}
    head = git.resolve("HEAD")
    source = CoreVersion.parse(source_version(git.root, platform))
    target = git.resolve(main_ref) if mode == "beta" else head
    all_tags = git.tags(target)
    baseline = highest_stable_tag(all_tags, platform)
    base_tag, base = baseline if baseline else ("", source)
    tag_sha = git.resolve(base_tag) if base_tag else ""
    if mode == "tag":
        if not tag or stable_tag_version(tag, platform) is None:
            raise VersionError("Tag mode requires a stable tag for this platform.")
        version = stable_tag_version(tag, platform)
        if git.resolve(tag) != head or source != version:
            raise VersionError("The tag must identify this exact source and its committed version.")
        plan = {"bump": "none", "publish": True, "core_version": str(version), "version": str(version),
                "tag": tag, "base_tag": tag, "base_version": str(version), "tag_source_sha": head,
                "source_sha": head, "source_version": str(source), "prerelease": False, "notes": []}
    else:
        intent = Bump.NONE
        reservations: list[CoreVersion] = []
        notes: list[str] = []
        seen_prs: set[int] = set()
        runtime_delta = affects_platform(git.paths(tag_sha or None, target), platform)
        for sha, message, paths in git.commits(base_tag or None, target):
            reserved = generated_release_commit(message, paths, platform)
            if reserved:
                if source_version(git.root, platform, git, sha) != str(reserved):
                    raise VersionError("A generated release reservation disagrees with its committed source.")
                reservations.append(reserved)
                continue
            if not runtime_delta or not affects_platform(paths, platform):
                continue
            legacy = next((value for value in policy.get("legacy_pull_requests", {}).values()
                           if value.get("merge_commit_sha") == sha), None)
            if legacy:
                intent = max(intent, Bump.parse(legacy[platform]))
                notes.extend(change_notes(message, []))
                continue
            pulls = github.merged_pull_requests(sha)
            fresh = [pull for pull in pulls if pull["number"] not in seen_prs]
            seen_prs.update(pull["number"] for pull in fresh)
            if pulls:
                # Rebase merges can associate many distinct commits with one PR.
                # Deduplicate notes, never the intent carried by later commit messages.
                intent = max(intent, *(release_intent(message, pull, platform) for pull in pulls))
                notes.extend(change_notes(message, fresh))
            else:
                intent = max(intent, release_intent(message, None, platform))
                notes.extend(change_notes(message, []))
        pr_affects = False
        if mode == "beta":
            pull = event.get("pull_request") or {}
            if not pull or not beta_sequence or not re.fullmatch(r"[1-9][0-9]*(?:\.(?:0|[1-9][0-9]*))*", beta_sequence):
                raise VersionError("Beta mode requires PR metadata and a numeric run/attempt sequence.")
            number = pull.get("number") or event.get("number")
            if not isinstance(number, int) or number <= 0:
                raise VersionError("Beta mode requires a valid pull-request number.")
            before = git.resolve(pull["base"]["sha"])
            after = git.resolve(pull["head"]["sha"])
            before = git.merge_base(before, after)
            pr_affects = affects_platform(git.paths(before, after), platform)
            if pr_affects:
                intent = max(intent, release_intent("", pull, platform))
                notes.extend(change_notes("", [pull]))
        computed = base.bump(intent)
        reserved_core = max(reservations, default=base)
        core = max(computed, reserved_core)
        if source > core:
            raise VersionError("The committed source version is ahead of calculated intent; declare the intended major/minor change instead of downgrading it.")
        if platform == "windows" and any(part > 65535 for part in (core.major, core.minor, core.patch)):
            raise VersionError("The calculated version exceeds Windows numeric version-resource limits.")
        if core > computed:
            intent = Bump.MAJOR if core.major > base.major else Bump.MINOR if core.minor > base.minor else Bump.PATCH
        publish = core > base and (mode != "beta" or pr_affects)
        version_text = str(core)
        if mode == "beta":
            version_text += f"-beta.{number}.{beta_sequence}"
        plan = {"bump": intent.name.lower(), "publish": publish, "core_version": str(core), "version": version_text,
                "tag": PREFIX[platform] + version_text if mode == "beta" or publish or not base_tag else base_tag,
                "base_tag": base_tag, "base_version": str(base), "tag_source_sha": tag_sha,
                "source_sha": head, "source_version": str(source), "prerelease": mode == "beta",
                "notes": list(dict.fromkeys(notes))}
    plan["release"] = plan["publish"]
    plan["numeric_version"] = plan["core_version"] + ".0"
    plan["notes_path"] = GENERATED[platform][-1]
    plan["sync_files"] = []
    return plan


def write_outputs(plan: dict[str, Any], destination: Path) -> None:
    with destination.open("a", encoding="utf-8") as stream:
        for key, value in plan.items():
            if key == "notes":
                continue
            serialized = ("true" if value else "false") if isinstance(value, bool) else json.dumps(value, separators=(",", ":")) if isinstance(value, list) else str(value)
            if "\n" in serialized or "\r" in serialized:
                raise VersionError("Release outputs must be single-line values.")
            stream.write(f"{key}={serialized}\n")


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("plan",))
    parser.add_argument("--platform", choices=tuple(PREFIX), required=True)
    parser.add_argument("--mode", choices=("stable", "beta", "tag"), required=True)
    parser.add_argument("--repo-root", type=Path, default=Path(__file__).resolve().parents[1])
    parser.add_argument("--main-ref", default="origin/main")
    parser.add_argument("--event-file", type=Path)
    parser.add_argument("--ref", default=os.environ.get("GITHUB_REF", ""))
    parser.add_argument("--beta-sequence")
    parser.add_argument("--tag")
    parser.add_argument("--github-output", type=Path)
    parser.add_argument("--sync", action="store_true")
    arguments = parser.parse_args(argv)
    try:
        root = arguments.repo_root.resolve()
        git = Git(root)
        if git.run("rev-parse", "--is-shallow-repository") != "false":
            raise VersionError("Release planning requires full history and fetched tags.")
        policy_path = safe_path(root, ".github/release-policy.json")
        policy = json.loads(policy_path.read_text(encoding="utf-8")) if policy_path.exists() else {}
        if not policy and arguments.mode != "tag":
            raise VersionError("The release policy is required for stable and beta planning.")
        repository = os.environ.get("GITHUB_REPOSITORY") or policy.get("repository") or "krishnabhunia/evict-uninstaller"
        token = os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN")
        github = GitHub(repository, policy.get("repository_id"), token)
        event = json.loads(arguments.event_file.read_text(encoding="utf-8")) if arguments.event_file else {}
        tag = arguments.tag or (arguments.ref.removeprefix("refs/tags/") if arguments.ref.startswith("refs/tags/") else None)
        plan = build_plan(git, github, policy, arguments.platform, arguments.mode, event,
                          arguments.main_ref, arguments.beta_sequence, tag)
        if arguments.sync:
            if arguments.mode != "stable":
                raise VersionError("Only stable preparation may synchronize committed source versions.")
            if plan["publish"]:
                plan["sync_files"] = sync_sources(root, arguments.platform, CoreVersion.parse(plan["core_version"]),
                                                 plan["base_tag"], plan["notes"])
                plan["source_version"] = source_version(root, arguments.platform)
        if arguments.github_output:
            write_outputs(plan, arguments.github_output)
        print(json.dumps(plan, sort_keys=True))
        return 0
    except (VersionError, OSError, ValueError, KeyError) as error:
        print("Version planning failed: " + str(error), file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
