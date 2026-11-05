"""Reject implementation-free release notes for new application-changing builds."""
import argparse
from pathlib import Path
import re


def meaningful_changes(markdown):
    changes = []
    for raw in markdown.splitlines():
        line = raw.strip()
        if line.startswith("- ") and len(line) >= 35 and not line.startswith("- <!--"):
            if not re.match(r"- (?:feat|fix|chore|docs)(?:\([^)]*\))?:\s", line, re.I):
                changes.append(line)
    return changes


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("notes")
    args = parser.parse_args()
    notes = Path(args.notes).read_text(encoding="utf-8-sig")
    if not meaningful_changes(notes):
        raise SystemExit("Release notes are missing detailed implementation descriptions. Update the PR's Changes section.")
    print(f"Release notes contain {len(meaningful_changes(notes))} implementation details")


if __name__ == "__main__":
    main()
