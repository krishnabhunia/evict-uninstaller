# Installed update release test

The open Windows PRs publish beta builds. A normal stable release is published only after merge into `main`; keep the PRs unmerged while testing.

| Build | Application label | Windows numeric resources |
| --- | --- | --- |
| Main stable | x.y.z | x.y.z.0 |
| PR beta | x.y.z-beta.<run>.<PR>.<attempt> | x.y.z.0 |

GitHub calculates the core from the change type and synchronizes source, installer and changelog metadata. The workflow run is first in the beta suffix so newer builds sort above older builds across PRs. A final stable version sorts above its beta at the same core.

## PR metadata

```text
Release-Type-Windows: minor
Release-Type-Macos: none
Release-Test-Windows: beta
```

The optional beta test directive enables real published-update verification. It cannot make a PR stable or choose an arbitrary core. Test PRs calculate from the latest main stable release. After main released 1.11.0, a new minor PR calculates 1.12.0 automatically.

## Archive layout

| ZIP folder | Build file |
| --- | --- |
| `portable/` | `Evict.exe` and its SHA-256 sidecar |
| `windows-installer/` | `Evict-Setup-<WindowsVersion>.exe` and its SHA-256 sidecar |
| `macOS/` | `Evict-macOS-<MacVersion>.zip` and its SHA-256 sidecar |

There are no other top-level files or folders. The macOS app ZIP contains the native `Evict.app` bundle and preserves its executable permissions and framework symlinks. Windows releases build both native components from the frozen source. Mac releases pair their native bundle with the latest published Windows stable whose source is reachable from main.

## Check the installed application

1. Leave **Settings → Updates → Include beta releases** off. Exit Evict completely and restart it with automatic checks enabled.
2. Expect a background GitHub check and only a newer main stable release, if available. Reopening a window while the process is still running is not an application restart.
3. Enable beta releases, exit and restart again. Expect the newest eligible beta or stable release, with its full version label and release notes.
4. Download and install the offered update, then confirm the version after restart. Installed Evict uses Setup; portable Evict updates its executable.
5. Cancel a beta confirmation or download once. The current installation must remain usable.
6. Disable beta releases and restart. No beta should be offered, and an already installed beta must not be downgraded.
7. After merge into main publishes the final stable version, check again. The final version is newer than its beta at the same core.

The withdrawn historical PR #12 stable test is no longer a stable update source. Preserving its tag and asset bytes prevents version reuse.

## Automated evidence and limits

Version tests cover beta-only PRs, exact-main stable delivery, automatic synchronization and major/minor/patch arithmetic. Startup tests cover each process lifetime, saved timestamps, current preferences, manual request deduplication, cancellation and stale channel results.

Packaging checks require all three exact folders, checksum-valid Windows files and a real native macOS app archive. Live CI uses the real GitHub catalog, tests beta opt-out/opt-in, downloads the requested release's exact Setup and verifies its hash and native version. It captures installer handoff arguments without launching the application. Parallel PRs can legitimately make another preview the catalog's newest version.

The downloaded installer also runs in five isolated native fixtures. Actual GUI restarting, retained settings and Bitdefender behavior still require testing on the user's Windows machine.
