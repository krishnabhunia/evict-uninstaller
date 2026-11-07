# Evict macOS feature parity

Tracking: [Issue #39](https://github.com/krishnabhunia/evict-uninstaller/issues/39).

The approved Windows experience is implemented with native macOS services and controls. Existing application inventory, sortable/customizable columns, batch review, confidence tiers, protected paths and Trash removal remain the foundation.

## Feature map

| Windows experience | Native macOS implementation |
| --- | --- |
| Home dashboard and orange navigation | Native SwiftUI dashboard with live application/storage counts, update state, cleanup and startup findings; adaptive branding |
| Header version and update button | Full native release identity beside Evict, conditional update action, offline What's New notes |
| Startup splash | Actual preferences/inventory/recovery stages, progress, delayed-scan fallback and log access |
| Application filters and bulk review | Search, recent/large/rarely-used/source filters, Select All/None, customizable table and per-app batch review |
| Running-app safeguard | Require the exact application and its known helper bundles to quit before removal; graceful user-requested quit |
| Software Updater | Installed Homebrew casks, source-aware package identity, installed bundle and provider versions, explicit selected upgrade queue |
| Update exclusions | Durable exclusions keyed to the fully qualified package identity; explicit restore to the update list |
| Seven-day update history | Persistent per-app outcomes, before/target/after versions, command errors, interrupted-state recovery, details and CSV export |
| Version Manager | Native mac-v releases, exact version selection, latest stable/beta labels, optional older builds, pin/skip preferences and release details |
| Update and rollback preparation | SHA-256 download verification, native bundle identity/version/signature validation, verified recovery copy, explicit Finder replacement and relaunch reconciliation |
| Scheduled scan | Explicit Apply/Cancel; daily/weekly per-user launchd schedule; verified registration with rollback and saved-configuration authorization |
| Startup management | User-owned LaunchAgent controls; Apple/system jobs and Evict's scheduled scan remain protected |
| Cleanup | Bounded cache/log scan, reviewed selection, path/symlink/identity checks, Trash removal and truthful moved-byte reporting |
| Browser extensions | Profile/extension/version/permission inventory with changes performed through the owning browser's management screen |
| Install Monitor | Explicit bounded before/after filesystem snapshots, persisted recording and comparison, JSON export; concurrent changes are not attributed automatically |
| Security tools | Observed Gatekeeper/FileVault/System Integrity Protection state and native settings links; no invented antivirus verdict |
| Notification-area actions and search | Native menu-bar actions, one main window and Command-K page/application search |
| Settings persistence | Backward-compatible field decoding, serialized atomic writes, surfaced failures and preservation of corrupted preferences until explicit reset |
| Platform releases | Independent macOS pipeline, native three-digit betas, universal Apple Silicon/Intel packages and implementation notes |

## Native behavior and boundaries

Homebrew is the automated third-party update provider. App Store, operating-system and vendor-managed apps open their own update controls. Homebrew does not offer a universal historical-version catalog, so Evict does not promise arbitrary rollback for those applications.

Evict's own installer is verified and prepared in the app. The user completes replacement through Finder, allowing macOS to request normal administrator or Gatekeeper approval. A prepared download is not reported as an installation. On the next launch, the exact version, installed path and executable digest must match before history reports success. Recovery copies use the same explicit handoff.

Removal uses Trash; moved bytes are not described as reclaimed disk space. Windows Registry cleanup, Windows Update removal, Defender operations and guaranteed SSD shredding are not macOS facilities. Security controls and quarantine attributes are not bypassed by the updater.

Scheduled scans run while the user is logged in, even when the application window is closed. They only record inventory and orphan startup-item information. They never remove files or install software. Moving Evict to another location requires reapplying the schedule.

## Release identity

The source `Version.current` and `CFBundleShortVersionString` remain a numeric native core such as `1.1.0`. `EvictReleaseVersion` contains the exact shipped identity such as `1.1.0-beta003`. The application header and updater use the latter. Windows suite release numbers are never compared against the installed native Mac version.

Native release tags use `mac-v<version>`. Packages use `Evict-<version>-macOS-universal.zip` and `.dmg`, each with an exact SHA-256 sidecar. Release notes are embedded in the signed app bundle. The workflow builds both architectures, runs Swift tests, verifies native bundles and archives and proves the application opens before publication.

The Windows download can retain its existing three-folder layout by carrying an already verified Mac installer. Reusing that installer does not rebuild macOS or create a new Mac release.

## Validation

Portable fixtures cover package selection and identity, archive traversal/symlinks/checksums, recovery reconciliation, provider version evidence, exclusions/history persistence, interrupted updates, scan scheduling/rollback, settings migration/corruption, bounded subprocesses, cleanup boundaries, browser parsing and snapshot comparison.

Native macOS CI additionally compiles SwiftUI, exercises signed universal update/recovery fixtures, builds and verifies Apple Silicon and Intel packages, and runs the launch smoke test. Physical-device acceptance still covers real Homebrew upgrades, Finder replacement/rollback, launchd scheduling across login/sleep, browser management and administrator-controlled locations. Apple Developer ID signing/notarization requires the repository owner's Apple credentials; ad-hoc builds are labeled accurately.
