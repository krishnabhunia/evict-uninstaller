# Changelog

## 1.2.0 — 2026-11-09

<!-- Evict automatic release; base: mac-v1.1.0 -->

- Claude : Feature: short beta names and Info window (BT1 A, IN1, #41) — 4.2.0
- PR test builds are now named `x.y.z-betaNNN` (for example `4.2.0-beta007`) with the tag `win-vx.y.z-betaNNN`; stable releases are unchanged.
- The beta number is reserved in CI by `scripts/beta_reservation.py`: it reuses the tag that already points at the PR source (a re-run keeps its number), otherwise creates the next number from 001 to 999 and stops at 1000 with a request to bump the version.
- The beta prerelease is published on that reserved tag; the build adds only the managed version files on top of the reviewed PR source, and the publish, freeze and post-publication checks verify that.
- New Info window: the ⓘ button beside the version (tooltip "Info (F1)"), F1 and Settings → About → Info open "Evict · Info" with This build, What's implemented, What's new and Under the hood, plus Copy build info.
- The main window shows a small "Beta" chip beside the version on test builds, and the "What's new" link after an update opens the Info window.
- This build lists version, channel, pull request, commit, build time, GitHub tag, download name and install type; CI passes these as build metadata, and a local build says "Local build".
- The Mac app gets the same Info window beside its version, in the Evict menu (F1) and in Settings, using its own `mac-v` version line.
- New `docs/FEATURES.md` feature table (40 rows, Windows and Mac columns) embedded in both apps and checked by `scripts/features_check.py`; a PR planned as a minor or major release must update it.
- Updater tests prove `4.2.0-beta001` ranks above an old long beta of the same version, `beta009` below `beta010`, and every beta below its stable release.
- Reservation — `scripts/beta_reservation.py`, `scripts/tests/test_beta_reservation.py`, `reserve-beta` job in `.github/workflows/windows.yml`
- Planner — `scripts/versioning.py` (`--beta-number`, `beta_reserved`, `pr_bump`)
- Feature list — `docs/FEATURES.md`, `scripts/features_check.py`, `scripts/tests/test_features_check.py`
- Windows — `Evict.Core/Services/BuildInfo.cs`, `InfoContent.cs`, `Evict.App/Views/InfoWindow.xaml`, `ViewModels/InfoViewModel.cs`, `MainWindow.xaml`, `Directory.Build.props` (AssemblyMetadata)
- Mac — `EvictKit/InfoContent.swift`, `EvictApp/InfoView.swift`, `RootView.swift`, `SettingsView.swift`, `EvictApp.swift`, `Scripts/make-app.sh`, `.github/workflows/macos.yml`

## 1.1.0 — 2026-11-07

<!-- Evict automatic release; base: mac-v1.0.1 -->

- ChatGPT-Evict-macOS-Windows-Feature-Parity
- Add the native dashboard, grouped navigation, full release version, contextual update button, embedded What's New notes, startup splash with real progress, Command-K search and menu-bar actions.
- Extend application discovery with recent, large, rarely-used and source filters, Select All/None and reviewed batch removal; require the exact application and its helper processes to quit gracefully before uninstalling.
- Add a Homebrew cask Software Updater with verified package identity, installed-versus-provider version evidence, explicit selected upgrades, persistent exclusions and post-install verification.
- Persist seven-day software-update history with before/target/after versions, command errors, interrupted-state recovery, details, CSV export and stop-after-current queue control.
- Add Evict Version Manager for native mac-v releases, exact version selection, stable/beta channels, optional older builds, pin/skip preferences and detailed implementation notes.
- Verify Evict update archives, checksums, universal architectures, bundle identity, exact version and code signature; prepare verified recovery copies and record installation only after matching relaunch evidence.
- Add explicit Apply/Cancel scheduling through an owned per-user LaunchAgent, verify registration before saving preferences, roll back failed changes and run authorized read-only background inventory scans.
- Add reviewed cache/log cleanup with protected-path, symlink and identity checks; move selected files to Trash and report actual moved bytes.
- Add browser profile/extension/permission inspection, bounded before/after install-monitor snapshots with persistent recovery and JSON export, and observed native security status.
- Add verified Enable/Disable/Remove controls for user LaunchAgents with confirmations and reversible registration changes; protect Apple/system jobs and Evict's scheduled scan.
- Migrate existing settings using backward-compatible decoding, serialize atomic persistence, surface failures and preserve corrupted preferences until explicit reset.
- Isolate native macOS releases from Windows version changes, generate three-digit Mac betas with implementation notes, validate universal app/ZIP/DMG packages and require a native launch test before publication.
- Keep the existing Windows download layout by reusing a provenance-verified Mac installer on Ubuntu instead of rebuilding macOS or publishing an unrelated Mac release.

## 1.0.1 — 2026-11-05

<!-- Evict automatic release; base: mac-v1.0.0 -->

- ChatGPT-Evict-Three-Digit-Beta-Whats-New
- Generate permanent `win-vX.Y.Z-beta001`–`beta999` tags from published releases within serialized beta CI; preserve stable tags and reject serial exhaustion.
- Populate release notes from exact feature-level PR descriptions, rather than PR titles alone. Require detail in the publish workflow.
- Add **What's New** for installed Evict in Windows Settings and a full per-version implementation-detail window in Version History. Keep exact platform, beta/stable status, publication date and current/selected version visible.
- Update every release-freeze, install-archive, artifact, promotion, and published-release validation to understand the three-digit beta format; add tests.
- Python suite: **186 tests passed** locally; workflow YAML validated.
- GitHub Actions [Evict Build & Release](https://github.com/krishnabhunia/evict-uninstaller/actions/runs/42003628693): **success**, including frozen-source validation, .NET tests, native installer/package checks, publication and post-publication verification.
- Windows manual visual review remains available for reviewer; no merge into `main`.
- GitHub API commits are marked Unverified (unsigned).

## 1.0.0 — 2026-10-10

<!-- Evict automatic release; base: mac-v0.2.0 -->

- feat(updates): publish 1.10.0 for stable update verification
- feat(updates): test beta restart updates and native packages
- fix(ci): keep one installation download and clean build artifacts
- fix(ci): one Windows and macOS workflow with versioned installation ZIP
- fix(macos): prove the app opens and get past Gatekeeper on first launch
- feat(macos)!: selectable Applications table with shaded rows and movable columns

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
