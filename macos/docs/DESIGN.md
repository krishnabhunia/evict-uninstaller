# Evict for Mac — design notes

## Why this is a rewrite, not a port

The Windows app is built around the registry: uninstall entries, `HKCU\Software`, CLSIDs, shell
verbs, firewall rules. macOS has none of that, so roughly 70 % of `Evict.Core` has no counterpart
here. What carries over is the product design — the pages, the confidence tiers, the review step,
verifying a removal instead of assuming it — and that is what this project reuses.

## Feature map

| Windows | macOS | Build |
|---|---|---|
| Programs list (Uninstall registry keys) | App bundles + `pkgutil` receipts + Homebrew casks + App Store receipts | 1 |
| Uninstall wizard with review | Same three steps, everything to the Trash | 1 |
| Registry leftovers | Files under `~/Library` and `/Library` | 1 |
| Force Uninstall | Name/dropped-bundle search | 1 |
| Startup Apps (Run keys) | LaunchAgents, LaunchDaemons, login items | 1 |
| History, Settings | Same | 1 |
| Browser Extensions | Chrome/Edge/Brave/Firefox profiles under `~/Library/Application Support` | 2 |
| Health dashboard, Easy Uninstall widget | Same dashboard, menu-bar item | 2 |
| Explorer right-click | Finder Quick Action / Services menu | 2 |
| Install Monitor (registry + file snapshot) | FSEvents watcher over `/Applications`, `~/Library`, `/Library` | 3 |
| Software Updater (winget) | Homebrew `brew outdated` + Mac App Store (`mas`) + Sparkle apps | 3 |
| System Cleanup | User/system caches, Xcode DerivedData, iOS backups, Homebrew cache, Trash | 3 |
| Restore points | No equivalent — the Trash is the undo | — |
| Registry cleaning | No equivalent — orphaned preferences and launch agents instead | 1–2 |

## The safety gate

Everything funnels through `SafePaths.check(path)`, which answers `.allowed` only when all of these hold:

1. The path is absolute and contains no `..`.
2. It is not one of the shared folders themselves (`~/Library/Caches`, `/Library/Application Support`, …).
3. It is not inside a SIP-protected or Apple-owned root (`/System`, `/bin`, `/usr/bin`, `/Library/Apple`, …).
4. It is not inside the user's own data (`Documents`, `Desktop`, `Downloads`, `Pictures`, `Mail`, `Keychains`,
   `Mobile Documents`, `CloudStorage`, …).
5. It sits at least one level inside one of the known removable roots.

`Remover` re-checks the verdict even though the scanner already did, moves the item to the Trash with
`FileManager.trashItem`, then reads the path again and reports "still present" instead of success if
anything survived.

## Confidence tiers

| Tier | When | Pre-ticked |
|---|---|---|
| High | Named after the bundle ID (or a sub-identifier of it), or a launch agent whose program lives inside the app bundle, or a `/usr/local/bin` symlink pointing into it | Yes |
| Medium | Same vendor prefix (`com.vendor.*`), or a folder named exactly like the app | Yes |
| Low | Name merely looks like the app's | No — listed with its reason |

Filler words (`app`, `helper`, `player`, `updater`, `mac`, vendor suffixes like `inc`, `llc`) never
count as evidence on their own, which is what stops "Media Player" matching "Acme Player".

## What needs a Mac

Compiling SwiftUI, running the interface and confirming real removals all need macOS; the cloud
container can only build and test `EvictKit`. So CI on `macos-14` is the compiler of record, and the
first real test is on Krishna's Mac Mini.
