# Changelog

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
