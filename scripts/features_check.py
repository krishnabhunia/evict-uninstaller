#!/usr/bin/env python3
"""Validate docs/FEATURES.md, the feature list shown in the app's Info window.

Checks:
  * an intro line, then exactly one table with the header
    ``| SrNo. | Area | What it does | Since | Windows | Mac |``;
  * SrNo. runs 1..n in order, Area and "What it does" are filled in,
    Since is x.y.z and not newer than the version being built,
    Windows and Mac say "Yes" or "Not yet";
  * a pull request that adds a feature also changes docs/FEATURES.md. In this repository a new
    feature raises the minor number y and a major change raises x (docs/versioning.md), so a PR
    planned as a minor or major release must touch the file.
"""
from __future__ import annotations

import argparse
from pathlib import Path
import re
import sys

HEADER = ("SrNo.", "Area", "What it does", "Since", "Windows", "Mac")
FEATURES_PATH = "docs/FEATURES.md"
AVAILABILITY = ("Yes", "Not yet")
CORE = re.compile(r"(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)")


class FeaturesError(ValueError):
    pass


def core(value: str) -> tuple[int, int, int]:
    match = CORE.fullmatch(value.strip())
    if not match:
        raise FeaturesError(f"'{value}' is not an x.y.z version.")
    return int(match[1]), int(match[2]), int(match[3])


def cells(line: str) -> list[str]:
    line = line.strip()
    if not (line.startswith("|") and line.endswith("|")) or len(line) < 2:
        raise FeaturesError(f"Not a table row: {line[:60]}")
    return [cell.strip() for cell in line[1:-1].split("|")]


def parse(text: str) -> list[dict[str, str]]:
    lines = [line.rstrip() for line in text.replace("\r\n", "\n").split("\n")]
    table = [index for index, line in enumerate(lines) if line.lstrip().startswith("|")]
    if not table:
        raise FeaturesError("docs/FEATURES.md has no table.")
    start, end = table[0], table[-1]
    if table != list(range(start, end + 1)):
        raise FeaturesError("docs/FEATURES.md must contain exactly one table.")
    if not any(line.strip() for line in lines[:start]):
        raise FeaturesError("docs/FEATURES.md must start with an intro line above the table.")
    if tuple(cells(lines[start])) != HEADER:
        raise FeaturesError("The table header must be exactly: | " + " | ".join(HEADER) + " |")
    if end == start or not all(re.fullmatch(r":?-{3,}:?", cell) for cell in cells(lines[start + 1])) \
            or len(cells(lines[start + 1])) != len(HEADER):
        raise FeaturesError("The header must be followed by a |---| separator row with one cell per column.")
    rows = []
    for number, line in enumerate(lines[start + 2:end + 1], start=1):
        values = cells(line)
        if len(values) != len(HEADER):
            raise FeaturesError(f"Row {number} has {len(values)} cells instead of {len(HEADER)}.")
        row = dict(zip(HEADER, values))
        if row["SrNo."] != str(number):
            raise FeaturesError(f"SrNo. must count 1, 2, 3 … in order; row {number} says '{row['SrNo.']}'.")
        if not row["Area"] or not row["What it does"]:
            raise FeaturesError(f"Row {number} needs an Area and a 'What it does' text.")
        try:
            core(row["Since"])
        except FeaturesError:
            raise FeaturesError(f"Row {number}: Since must be an x.y.z version, not '{row['Since']}'.") from None
        for column in ("Windows", "Mac"):
            if row[column] not in AVAILABILITY:
                raise FeaturesError(f"Row {number}: {column} must be 'Yes' or 'Not yet', not '{row[column]}'.")
        rows.append(row)
    if not rows:
        raise FeaturesError("The feature table has no rows.")
    for line in lines[end + 1:]:
        if line.strip():
            raise FeaturesError("Nothing may follow the feature table.")
    return rows


def check(text: str, version: str, bump: str | None = None, changed: list[str] | None = None) -> list[dict[str, str]]:
    rows = parse(text)
    current = core(version)
    for row in rows:
        if core(row["Since"]) > current:
            raise FeaturesError(f"Row {row['SrNo.']} ({row['Area']}) says Since {row['Since']}, "
                                f"which is newer than the version being built ({version}).")
    if bump is not None:
        if bump not in ("none", "patch", "minor", "major"):
            raise FeaturesError("The release bump must be none, patch, minor or major.")
        normalized = {path.replace("\\", "/").strip() for path in changed or []}
        if bump in ("minor", "major") and FEATURES_PATH not in normalized:
            raise FeaturesError(
                f"This pull request adds a feature ({bump} release), so it must also add or update its row in "
                f"{FEATURES_PATH}. Add the row (Since = the new version) or mark the PR as a fix.")
    return rows


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--file", type=Path, default=Path(__file__).resolve().parents[1] / FEATURES_PATH)
    parser.add_argument("--version", required=True, help="x.y.z being built (the planned core version)")
    parser.add_argument("--bump", choices=("none", "patch", "minor", "major"),
                        help="planned release increment of a pull request")
    parser.add_argument("--changed-files", type=Path,
                        help="file listing the paths the pull request changes, one per line")
    arguments = parser.parse_args(argv)
    try:
        if arguments.bump is not None and arguments.changed_files is None:
            raise FeaturesError("--bump needs --changed-files.")
        changed = (arguments.changed_files.read_text(encoding="utf-8").splitlines()
                   if arguments.changed_files else None)
        rows = check(arguments.file.read_text(encoding="utf-8-sig"), arguments.version, arguments.bump, changed)
        print(f"{FEATURES_PATH}: {len(rows)} features, all valid for {arguments.version}.")
        return 0
    except (FeaturesError, OSError) as error:
        print(f"::error file={FEATURES_PATH}::{error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
