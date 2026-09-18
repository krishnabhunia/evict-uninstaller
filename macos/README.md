# Evict for Mac

An application uninstaller and leftover cleaner for macOS — the Mac counterpart of
[Evict for Windows](https://github.com/krishnabhunia/evict).

Dragging an app to the Trash leaves its support files, preferences, caches, containers and launch
agents behind. Evict finds them, shows you what it found and why, and moves the lot to the Trash —
so nothing it does is permanent.

## What it does (Build 1)

| Page | What it does |
|---|---|
| Applications | Every app on the Mac, with size, version, bundle ID and where it came from (App Store, installer package, Homebrew, drag-installed). Search, sort, drag an app onto the window to remove it. |
| Uninstall | Finds everything belonging to the app across `~/Library` and `/Library`, groups it by kind, and lets you tick each item before anything moves. |
| Force Uninstall | For apps that are already gone: search by name, or drop a bundle, and clean up what is left. |
| Startup Items | Launch agents and daemons, with the ones whose program no longer exists flagged as orphans. |
| History | What was removed, when, and how much space it freed. |
| Settings | Appearance, scan scope, confirmation behaviour, Full Disk Access status. |

## Safety

1. **Nothing is deleted.** Every removal is a move to the Trash, so anything can be put back.
2. **One gate for every path.** `SafePaths.check` refuses anything outside a known-cleanable folder,
   anything in a SIP-protected location, anything in your own Documents/Desktop/Downloads/Mail, and
   the shared folders themselves (`~/Library/Caches` can be cleaned inside, never removed).
3. **Verified, not assumed.** After a move, the path is read again. If it is still there, the item is
   reported as failed rather than counted as removed.
4. **Low-confidence finds are never pre-ticked.** They are listed with the reason they were flagged
   so you can decide.

## Requirements

- macOS 13 Ventura or later (Apple Silicon or Intel — the release build is universal).
- **Full Disk Access** for Evict, otherwise other apps' Library folders stay invisible to it.
  Evict shows a banner and a button that opens the right System Settings pane.

## Installing

Builds are not signed with an Apple Developer ID yet, so Gatekeeper will complain on first launch:

1. Download `Evict-<version>.zip` from the Actions run (or a Release) and unzip it.
2. Move `Evict.app` to `/Applications`.
3. **Right-click the app → Open → Open.** After that it starts normally.

## Building it yourself

```bash
swift test                     # unit tests
Scripts/make-app.sh            # build/Evict.app for this Mac
Scripts/make-app.sh --universal # arm64 + x86_64
```

With a Developer ID certificate in the keychain:

```bash
SIGN_IDENTITY="Developer ID Application: Your Name (TEAMID)" Scripts/make-app.sh --universal
```

## Layout

| Path | What it holds |
|---|---|
| `Sources/EvictKit` | All logic: app inventory, leftover scanning, the safety gate, the remover, launchd items, settings, history. No UI. |
| `Sources/EvictApp` | SwiftUI interface (macOS only). |
| `Tests/EvictKitTests` | Unit tests for the rules that decide what may be removed. |
| `Scripts/make-app.sh` | Builds the `.app` bundle and signs it. |
| `docs/DESIGN.md` | How the Windows features map to macOS, and what is still to come. |
