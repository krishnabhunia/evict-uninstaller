# Changelog

## 0.2.0 — 2026-10-07

<!-- Evict automatic release; base: mac-v0.1.0 -->

- Upgrade Actions to Node.js 24 and move CI from the retiring macOS 14 runner to macOS 15.
- Automatically calculate release versions, record the app version and build number, and publish tested source commits with matching tags.
- Keep PR validation read-only and preserve published assets when release jobs are retried.

- Keep vendor-only and shared leftovers unchecked, select the application bundle deliberately and revalidate owned command-line links before moving them to Trash.
- Correct history date decoding, application deduplication, Apple app eligibility and verified removal counters.
- Apply scan and review settings, prevent dismissing an active removal, and describe bytes moved to Trash accurately.
- Merge pull request #10 from krishnabhunia/fix/audit-safety-and-reliability
- feat(release): automate semantic versions and fix installer shutdown

## 0.1.0 — Build 1 (18 Sep 2026)

First build. Nothing here has been run on a Mac yet — it compiles on CI and the core logic is
covered by unit tests, but the interface is untested.

### Added
- **Applications** — inventory from `/Applications`, `~/Applications` and one level of grouping
  folders, with `Info.plist` details, on-disk size, install date and last-used date. Source detected
  from App Store receipts, `pkgutil` receipts, Homebrew casks or a plain drag-install.
- **Uninstall** — leftover scan across 17 user Library folders and 14 shared `/Library` folders,
  grouped by kind, each item carrying a confidence tier and a plain-English reason.
- **Force Uninstall** — same scan driven by a name (or a dropped bundle) for apps already deleted.
- **Startup Items** — launch agents and daemons, orphans flagged, unloaded via `launchctl` before
  their plist is removed.
- **History** and **Settings** pages; Full Disk Access detection with a link to System Settings.
- Safety: every path passes `SafePaths.check`; removals go to the Trash and are verified afterwards.
- CI on `macos-14`: tests, universal build, `.app` bundle, ad-hoc signature, zipped artifact.

### Known gaps
- Not signed with a Developer ID, so Gatekeeper needs right-click → Open on first launch.
- Homebrew casks are detected and flagged, but `brew uninstall --cask` is not run for you.
- `pkgutil` receipts are listed and reported; forgetting one still needs `sudo pkgutil --forget`.
- Planned for later builds: browser extensions, Software Updater (Homebrew + Mac App Store),
  Install Monitor (FSEvents), System Cleanup, Health dashboard, menu-bar item, Finder Quick Action.
