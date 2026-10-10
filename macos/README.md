# Evict for Mac

> Part of [evict-uninstaller](../README.md). Everything below lives in the `macos/` folder; run the commands from there.

An application uninstaller and leftover cleaner for macOS — the Mac counterpart of
[Evict for Windows](../windows/README.md).

Dragging an app to the Trash leaves its support files, preferences, caches, containers and launch
agents behind. Evict finds them, shows you what it found and why, and moves the lot to the Trash —
so nothing it does is permanent.

## What it does (Build 1)

| Page | What it does |
|---|---|
| Applications | Every app on the Mac in a native table: size, version, bundle ID and where it came from (App Store, installer package, Homebrew, drag-installed). Click a row to select it (⌘ / ⇧ for several), every second row is shaded, click a header to sort, drag headers to move columns, drag edges to resize, right-click a header to show Installed, Last used or Location — the layout is remembered. Double-click, Return or ⌘⌫ opens the review; several apps are reviewed one after another. Drag an app onto the window to remove it. |
| Uninstall | Finds everything belonging to the app across `~/Library` and `/Library`, groups it by kind, and lets you tick each item before anything moves. |
| Force Uninstall | For apps that are already gone: search by name, or drop a bundle, and clean up what is left. |
| Startup Items | Launch agents and daemons, with the ones whose program no longer exists flagged as orphans. |
| History | What was moved to the Trash, when, and its measured size. Disk space is reclaimed only after the Trash is emptied. |
| Settings | Appearance, scan scope, confirmation behaviour, Full Disk Access status. |

## Safety

1. **Nothing is deleted.** Every removal is a move to the Trash, so anything can be put back.
2. **One safety policy for every path.** `SafePaths` refuses anything outside a known-cleanable folder,
   anything in a SIP-protected location, anything in your own Documents/Desktop/Downloads/Mail, and
   the shared folders themselves (`~/Library/Caches` can be cleaned inside, never removed).
3. **Verified, not assumed.** After a move, the path is read again. If it is still there, the item is
   reported as failed rather than counted as removed.
4. **Uncertain finds require review.** Low-confidence items are unchecked by default; their visibility
   and preselection can be changed in Settings. Vendor-only or shared matches always require manual selection.
5. **Owned command-line links only.** Shortcuts in `/usr/local/bin` and `/opt/homebrew/bin` must still
   point inside the selected removable application when they are moved. Other Homebrew files are not allowed.

## Requirements

- macOS 14 Sonoma or later (Apple Silicon or Intel — the release build is universal). Macs on macOS 13 stay on Evict for Mac 0.2.x.
- **Full Disk Access** for Evict, otherwise other apps' Library folders stay invisible to it.
  Evict shows a banner and a button that opens the right System Settings pane.

## Installing

1. Download `Evict_<fullversion>.zip` from **Evict Build & Release** in Actions or from a Release, then unzip it.
2. Open `Evict_<fullversion>/macOS/Evict_<fullversion>.dmg`, then drag `Evict.app` into `/Applications`.
3. Open Evict from Applications.

**If macOS says it can't verify Evict** (builds are not notarized until the Apple secrets below are added):

| Step | Do this |
|---|---|
| 1 | Double-click Evict once and close the message. |
| 2 |  → **System Settings → Privacy & Security**, scroll to *"Evict was blocked…"*, click **Open Anyway**, enter your Mac password, click **Open**. |
| or | In Terminal: `xattr -dr com.apple.quarantine /Applications/Evict.app` then `open /Applications/Evict.app` |

Right-click → Open no longer skips this check on macOS 15 and later. The DMG carries the same steps in
`If Evict won't open.txt` whenever the build is not notarized.

The DMG preserves executable permissions and bundle symlinks. The same enclosing folder contains `portable/Evict_<fullversion>.exe` and `windows-x64/Evict_<fullversion>.exe` for Windows. The installation ZIP contains exactly these three payload files; checksums are separate release assets. Filenames use the suite version, while the native Mac app keeps its independent numeric version.

## Building it yourself

```bash
swift test                     # unit tests
Scripts/make-app.sh            # build/Evict.app for this Mac
Scripts/make-app.sh --universal # arm64 + x86_64
Scripts/make-dmg.sh 1.12.2      # build/Evict_1.12.2.dmg, suite release version
```

With a Developer ID certificate in the keychain:

```bash
SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" Scripts/make-app.sh --universal
Scripts/make-dmg.sh <version>
APPLE_ID=… APPLE_TEAM_ID=… APPLE_APP_PASSWORD=… SIGN_IDENTITY="…" Scripts/notarize.sh build/Evict_<version>.dmg
Scripts/smoke-launch.sh build/Evict.app      # opens the app and checks it shows a window and stays up
```

### Signing and notarization in CI

The **mac-package** job signs with a Developer ID, notarizes and staples the DMG by itself as soon as these
five repository secrets exist (Settings → Secrets and variables → Actions). Without them builds stay ad-hoc
signed and every run prints a Gatekeeper warning.

| Secret | What it holds |
|---|---|
| `MACOS_CERT_P12_BASE64` | The *Developer ID Application* certificate + private key exported as `.p12`, base64-encoded (`base64 -i cert.p12 \| pbcopy`) |
| `MACOS_CERT_PASSWORD` | The password chosen when exporting that `.p12` |
| `APPLE_ID` | The Apple ID of the Apple Developer Program account |
| `APPLE_TEAM_ID` | The 10-character Team ID (developer.apple.com → Membership) |
| `APPLE_APP_PASSWORD` | An app-specific password for that Apple ID (appleid.apple.com → Sign-In and Security) |

Every build — with or without them — is opened on the macOS runner by `Scripts/smoke-launch.sh`: the app must
start, show a window and stay up, both straight after the build and from the published DMG.

## Layout

| Path | What it holds |
|---|---|
| `Sources/EvictKit` | All logic: app inventory, leftover scanning, the safety gate, the remover, launchd items, settings, history. No UI. |
| `Sources/EvictApp` | SwiftUI interface (macOS only). |
| `Tests/EvictKitTests` | Unit tests for the rules that decide what may be removed. |
| `Scripts/make-app.sh` | Builds and signs the `.app` bundle. |
| `Scripts/make-dmg.sh` | Builds and mounts the versioned DMG to verify the native application. |
| `docs/DESIGN.md` | How the Windows features map to macOS, and what is still to come. |

## Release versions

GitHub Actions automatically maintains numeric `x.y.z` versions and the bundle build number from PR and commit release intent. The single **Evict Build & Release** workflow tests the exact release source on native Windows and Mac runners, verifies the universal bundle and disk image, and publishes one combined suite release. A `mac-vX.Y.Z` component tag is recorded after verified stable publication. Trusted PRs publish an optional suite beta without merging or writing to main; fork and bot PRs validate with read-only access. See [automatic versioning](../docs/versioning.md).
