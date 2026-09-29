# Evict Uninstaller — Project Status & Spec

**Goal:** a complete, independent Windows uninstaller owned by Krishna, delivered as a portable EXE and an installer.
**Decisions (16 Sep 2026):** C# / .NET 8 / WPF · incremental builds · cloud-built, no local toolchain needed · portable + Inno Setup installer · code in GitHub `krishnabhunia/evict` (Actions CI, Releases) · name stays "Evict" · no references to other products/companies in the project.

## Delivery — Build 4 (v1.3.0), 16 Sep 2026

| Item | Location |
|---|---|
| `Evict.exe` 1.3.0 (66 MB, self-contained win-x64, single file) | `G:\My Drive\Windows Software and Apps\Evict\` — as 4 parts + `Join-Evict.cmd` (double-click once to join and clean up) |
| `Evict-Setup-1.3.0.exe` installer | GitHub Actions run for tag **`v1.3.0`** → Summary → Artifacts, and **Releases → v1.3.0** (Inno Setup runs on the Windows runner only) |
| Source code | GitHub `main` (tags `v1.1.0`, `v1.2.0`, `v1.3.0`) and `Evict-source-v1.3.0.zip` in the Drive folder |
| README / CHANGELOG / this status | repo root + Drive folder |
| SHA-256 of Evict.exe 1.3.0 (Linux cross-build) | `Evict.exe.sha256` in the Drive folder (CI-built exe differs – different build machine, same source) |

## Architecture

| Layer | Project | Notes |
|---|---|---|
| Platform logic | `src/Evict.Core` (net8.0, `SupportedOSPlatform=windows`) | Registry Uninstall keys, UserAssist, WMI restore points, PowerShell (Appx, Get-HotFix), winget (parallel), FileSystemWatcher + registry snapshot (Install Monitor), Recycle Bin, services/tasks, GitHub Releases update check, WMI process-creation watcher (installer detection), schtasks (scheduled scan), system cleanup |
| UI | `src/Evict.App` (net8.0-windows, WPF + WinForms NotifyIcon, CommunityToolkit.Mvvm) | Fluent-style Light/Dark theme, sidebar nav, 9 pages, 10 dialog windows, tray icon, zoom 80–300 % (default 120 %) |
| Tests | `tests/Evict.Core.Tests` (xunit, 209 tests) | Pure logic only (runs on Linux CI too) |
| Installer | `installer/Evict.iss` (Inno Setup 6.3+) | per-user default / all-users; tasks: desktop icon, context menu, Send to, autostart; uninstall removes scheduled task + autostart |
| CI | `.github/workflows/build.yml` | windows-latest: restore → tests → xaml_check → publish → sign (optional) → ISCC → sha256 → artifact; tag `v*` → Release |

## Module status

| # | Module | Build | Notes |
|---|---|---|---|
| 1–12 | Programs, uninstall wizard, Powerful Scan, batch, Force Uninstall, Windows Apps, Browser Extensions, Software Updater, Install Monitor, Tools, History, Settings/theme | B1 | |
| 13–19 | Health dashboard, Easy Uninstall widget, Explorer menu + CLI, Startup Apps, Residual Cleaner, bundleware list, text size | B2 | |
| 20–23 | GitHub CI, Inno Setup installer, in-app updates, optional code signing | B3 | update check needs the repo public |
| 24 | Text size 80–300 %, default 120 % (migration for old settings), scrollable body at large zoom | B4 | |
| 25 | Notification-area icon: menu, close/minimize to tray, start with Windows (`--tray`) | B4 | WinForms NotifyIcon inside WPF |
| 26 | Installer detection → automatic Install Monitor (Off / Ask / Auto) | B4 | WMI events (+ polling fallback); Evict's own process tree excluded; pending trees so a late "record" click still captures helpers |
| 27 | Scheduled Health scan (Task Scheduler XML: runs on battery, catches up missed runs) + notification | B4 | `--scheduled-scan`; missed runs also caught up at next start |
| 28 | System Cleanup tool (Installer cache orphans → backup, Store app data, update/DO caches, temp, WER, dumps, Windows.old) | B4 | admin needed for Windows folders; nothing deleted without confirmation |
| 29 | Software Updater: parallel updates (1–6) with MSI-busy retry | B4 | |
| 30 | Real-time Install Monitor via filter driver | ✗ | out of scope |

## Verification

| Check | Result |
|---|---|
| C# compile (Release), 0 warnings | ✅ |
| 209 xunit tests | ✅ (Linux) |
| XAML static checks | ✅ 0 problems |
| Independent desk review of Build 4 (lifecycle, threading, WinForms interop, cleanup safety) | ✅ 5 must-fix findings fixed (cleanup deletes under Windows, own-process detection, late-click recording, temp-folder age, task-folder rights) + 15 smaller ones; remaining open items listed below |
| PE inspection (version 1.3.0.0) | ✅ |
| **Running the UI on Windows** | ❌ still not done by Claude — Krishna has installed 1.2.0 and runs it; Build 4 not yet started on Windows |

## Known limitations / to test first on Windows

- Tray icon + balloon notifications (Windows 10/11 show them as toasts; Focus Assist may hide them).
- Installer detection depends on WMI process events being delivered to a standard user (falls back to 3-second polling).
- Scheduled scan: created through `schtasks /XML`; verify once on a non-admin account that the task appears at the root of the Task Scheduler library.
- System Cleanup: Windows Update cache cleanup stops/starts `wuauserv`/`bits` (admin only). Orphaned MSI packages are *moved* to `%ProgramData%\Evict\InstallerCacheBackup\<date>` — delete that folder manually later.
- Parallel updates: winget itself may serialise some installs; MSI "busy" errors are retried up to 5× (20 s apart).
- Update check works only after the repository is public.
- Unsigned binaries → SmartScreen warning until a certificate is added (README → Code signing).

## Open items

1. Krishna: make the repo public; check the `v1.3.0` Actions run; install `Evict-Setup-1.3.0.exe` (the in-app updater from 1.2.0 will offer it once the repo is public).
2. First real QA session on the laptop (Claude can drive it with approval).
3. Decide on the GitHub token when the project is closed.
