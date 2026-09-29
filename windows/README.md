# Evict Uninstaller — Windows

> Part of [evict-uninstaller](../README.md). Everything below lives in the `windows/` folder; run the commands from there.

A complete Windows uninstaller — written in C# / .NET 8 / WPF,
delivered as a single portable `Evict.exe` **or** an `Evict-Setup-x.y.z.exe` installer (Inno Setup).

| Module | What it does | Status |
|---|---|---|
| **Programs** | All installed programs (64-bit, 32-bit, per-user) with icons, size, install date, last-used; tabs for *All / Recently Installed / Large / Infrequently Used / Bundleware / Broken Entries*; search, sort, multi-select | ✅ |
| **Uninstall wizard** | Optional System Restore point → runs the program's own uninstaller (interactive or silent) → **Powerful Scan** for leftovers → review files & folders → **separate Registry step** → delete (Recycle Bin optional; registry backed up to .reg first) → summary with **Undo registry changes** | ✅ / 1.4 |
| **Powerful Scan** | Finds leftover folders/files (Program Files, ProgramData, AppData of every user), Start-menu / desktop shortcuts, registry keys & values (vendor keys, App Paths, Run entries, AppCompat, MuiCache…), services and scheduled tasks | ✅ |
| **Batch uninstall** | Queue several programs; one review step for all leftovers | ✅ |
| **Force Uninstall** | For broken/missing uninstallers: pick a program or point at a folder/exe, kill its processes, remove everything it owns | ✅ |
| **Windows Apps** | Store / UWP / MSIX packages incl. pre-installed bloatware; remove per user or all users, de-provision | ✅ |
| **Browser Extensions** | Chrome, Edge, Brave, Vivaldi, Opera (all profiles) + Firefox; flags broad permissions; removes with browser closed | ✅ |
| **Software Updater** | Outdated programs via `winget upgrade`; one-click update with live output; **updates run in parallel** (1–6 at a time, MSI conflicts retried) | ✅ / Build 4 |
| **Install Monitor** | Records files, folders and registry keys created by an installer (FileSystemWatcher + registry snapshot diff); later "Uninstall using this log", or **"Clean registry…"** – only the keys that installation created that still exist (after a failed install, or when the program was removed another way) | ✅ / 1.4 |
| **Tools** | File Shredder (1 / 3 / 7 passes), Windows Updates uninstall (wusa), Create Restore Point, shortcuts to Windows tools | ✅ |
| **Registry Cleaner** | Separate module (Tools). Ticked when found: broken uninstall entries, App Paths / startup / "Open with" entries of missing programs, SharedDLLs and MuiCache records of missing files, empty software keys. For review (unticked): settings keys of removed programs, file-type commands. Advanced (unticked): COM / ActiveX, type libraries, Windows Installer folder records. Privacy (unticked): recent documents, Run / typed-path / search history, Open/Save dialog history, UserAssist. An entry is listed only when its file is provably gone from a fixed drive (USB and network drives never count; System32 ↔ SysWOW64 both checked). | ✅ 1.4 |
| **Registry backups** | Every registry deletion anywhere in Evict (wizard, Force Uninstall, Residual Cleaner, Install Monitor, Registry Cleaner) is first exported to a regedit-compatible `.reg` file in `%LocalAppData%\Evict\registry-backups`; entries that cannot be backed up are not deleted. Restore / delete backups in the Registry Cleaner (`reg import`). | ✅ 1.4 |
| **History** | Every operation with leftovers found/removed and bytes reclaimed; CSV export; rescan leftovers | ✅ |
| **Settings** | Light/Dark theme, defaults for the wizard, thresholds for the tabs | ✅ |
| **Software Health** (home page) | Score + 14 categories, each with a one-click action; tick categories and press **Fix selected** for the safe fixes (setup files to the Recycle Bin, caches, leftovers and broken entries with registry backup, promotional notifications off; permissions and hibernation opt-in) | ✅ Build 2 / 1.5 |
| **Health: Installation files** | Setup packages (.msi/.msix/setup .exe/setup archives) in Downloads and on the Desktop – ticked when the program is installed or the file is > 30 days old; to the Recycle Bin | ✅ 1.5 |
| **Health: Software redundant files** | Cache, log, crash-report and temp folders of installed programs (AppData, LocalLow, ProgramData as admin); log folders holding anything but log files are review-only | ✅ 1.5 |
| **Health: Programs with uninstall issues** | Broken entry, uninstaller missing (files present), no uninstall command, or an earlier uninstall that failed – Programs tab *Uninstall issues* | ✅ 1.5 |
| **Health: Disturbing notifications** | Every app allowed to show notifications plus Windows' tips / welcome / "finish setting up" / Settings suggestions; security senders locked on, promotional ones recommended off | ✅ 1.5 |
| **Health: Software permissions** | Camera, microphone, location, contacts … per Store app (Allow/Deny) and per desktop program (last use; Windows' single desktop switch); sensitive permissions never used are recommended off | ✅ 1.5 |
| **Health: Software hibernation** | Third-party background services and scheduled tasks can sleep (demand start + stop / task disabled) and be woken; security, driver, VPN, audio, backup and sync components locked awake; updaters recommended | ✅ 1.5 |
| **Health: Malicious software & extensions** | Microsoft Defender status, definitions age, last quick scan and active threats (other antivirus recognised), quick scan on request; extensions from outside the web stores or forced by policy; unsigned programs starting from user-writable folders | ✅ 1.5 |
| **Easy Uninstall widget** | Floating always-on-top target: drag it onto any program window, or drop a shortcut/.exe on it | ✅ Build 2 |
| **Explorer context menu + command line** | "Uninstall with Evict" on .exe / .lnk files; `--uninstall-file`, `--uninstall`, `--scan`, `--widget`, `--page`; second launches forward to the running window | ✅ Build 2 |
| **Startup Apps** | Run/RunOnce keys + Startup folders with the Task-Manager enable/disable switch | ✅ Build 2 |
| **Residual Cleaner** | Leftovers of programs uninstalled earlier: from History, broken entries, and unmatched folders (heuristic, review-only) | ✅ Build 2 |
| **Known-bundleware list** | Name database on top of the timing heuristic; user-extensible via `%LocalAppData%\Evict\bundleware.json` | ✅ Build 2 |
| **Text size / zoom**, dark-theme polish | 80–300 %, default 120 % (Ctrl + / − / 0); the window scrolls at large sizes; themed ComboBox, ScrollBar, TabControl, menus, RadioButton, Expander | ✅ Build 2 / 4 |
| **Installer** | `Evict-Setup-x.y.z.exe` (Inno Setup): per-user (no UAC) or all-users, Start-menu shortcuts, optional desktop icon / Explorer context menu / *Send to*, clean uninstall | ✅ Build 3 |
| **Removing Evict itself** | The uninstaller runs `Evict.exe --self-cleanup ask`: always removes Evict's registry keys, autostart, scheduled scan, context / Send-to menus, `Evict.old.exe` and the unpacked libraries in `%TEMP%\.net\Evict`; a checklist offers settings & log (ticked), history + install logs + registry backups (unticked), the System Cleanup installer-package backup in `ProgramData\Evict` (unticked), `*.evict-backup` browser files (ticked) and Windows' records of Evict.exe (ticked). Portable copies: *Settings → Remove Evict from this PC…* | ✅ 1.4 |
| **Update check** | Start-up check against GitHub Releases (can be turned off); banner + dialog with release notes; verified download; installed copies run the new Setup silently, portable copies replace `Evict.exe` in place and restart | ✅ Build 3 |
| **Code signing** | Optional Authenticode signing of both files in CI when a certificate secret is present | ✅ Build 3 |
| **Notification-area icon** | Tray menu (open, scan, widget, record, exit); close/minimize to tray; start with Windows (`--tray`) | ✅ Build 4 |
| **Installer detection → automatic Install Monitor** | Recognises installers as they start (file name, Inno/NSIS stubs, `msiexec /i`, descriptions); notification or fully automatic recording; waits for the whole process tree | ✅ Build 4 |
| **Scheduled Health scan** | Daily / weekly Task Scheduler job (`--scheduled-scan`), result as a notification, missed runs caught up | ✅ Build 4 |
| **System Cleanup** | Orphaned Windows Installer packages (backed up, not deleted), removed Store apps' data, update & Delivery Optimization caches, temp files, error reports, crash dumps, Windows.old | ✅ Build 4 |

## Running it

Two editions come out of every build — pick one:

| Edition | File | Notes |
|---|---|---|
| Portable | `Evict.exe` (~66 MB, self-contained, no .NET install needed) | Put it anywhere (e.g. `C:\Tools\Evict\`) and double-click. Updates replace the file in place. |
| Installed | `Evict-Setup-x.y.z.exe` | Installs to `%LocalAppData%\Programs\Evict` for the current user (no UAC) or, if you choose *all users*, to `Program Files`. Adds Start-menu shortcuts, an *Apps & features* entry and, optionally, the Explorer context menu. Updates run the new Setup silently. |

Both start **without** a UAC prompt; use *Restart as administrator* (sidebar or the yellow banner) to unlock
machine-wide operations (Program Files leftovers, services, restore points, all-user Store apps, Windows updates).
The first launch of an unsigned build shows Windows SmartScreen — *More info → Run anyway* (see *Code signing*).

Settings, history, install logs, downloaded updates and the diagnostic log live in `%LocalAppData%\Evict`.

## Running in the background

With the notification-area icon on (default), Evict can keep running after you close the window (*Settings → Notification
area → Closing the window keeps Evict running*) or start hidden at sign-in (*Start Evict with Windows*, a per-user `Run`
entry). While it runs it watches for new installer processes (WMI process-creation events, polling fallback) and either
asks or automatically records the installation with Install Monitor. A daily/weekly Software Health scan can be scheduled
through Task Scheduler (task `Evict Software Health scan`, per user); the result is a notification.

## Updates

On start (Settings → *Updates*, on by default) Evict asks `api.github.com/repos/krishnabhunia/evict-uninstaller/releases/latest`
for the newest tag (only Windows releases are ever marked *latest*; `mac-v*` tags are ignored); if the API is rate-limited it falls back to the `releases/latest` redirect. A newer version shows a
blue banner → *Update now* opens a dialog with the release notes. The download is verified against the `.sha256`
asset published by CI. Installed copies start `Evict-Setup-x.y.z.exe /SILENT /CLOSEAPPLICATIONS /NORESTART /EVICTUPDATE=1`
(which relaunches Evict); portable copies rename the running `Evict.exe` to `Evict.old.exe`, move the new file in and
restart with `--updated`. The check needs the repository (or at least its releases) to be public — a private repository
answers 404 and the status reads "no published release".

## Building from source

Requirements: .NET 8 SDK (Windows, Linux or macOS — the project sets `EnableWindowsTargeting`).

```bash
dotnet test tests/Evict.Core.Tests            # 410 unit tests for the pure logic
dotnet publish src/Evict.App -c Release -o publish   # → publish/Evict.exe (single file, win-x64)
```

or run `build/publish.ps1` (Windows) / `build/publish.sh` (Linux/macOS).

## Command line

```
Evict.exe --uninstall-file "C:\Program Files\Foo\foo.exe"   # or a .lnk – opens the wizard for the owning program
Evict.exe --uninstall "Notepad++"                            # by (partial) name
Evict.exe --scan                                             # open Software Health and scan
Evict.exe --widget                                           # show the Easy Uninstall widget
Evict.exe --page tools                                       # health|programs|apps|extensions|updater|monitor|tools|history|settings
Evict.exe --updated                                          # (internal) first start after a self-update
Evict.exe --tray                                             # start hidden in the notification area (used by "Start with Windows")
Evict.exe --scheduled-scan                                   # run the Health scan silently and notify (used by Task Scheduler)
Evict.exe --self-cleanup ask                                 # "remove Evict's leftovers" checklist (used by the uninstaller)
Evict.exe --self-cleanup settings,history                    # silent: integration + the named parts (all | settings | history |
                                                             #   installercache | browser | traces | integration = nothing optional)
```
If Evict is already running, a second launch hands its arguments to the open window.

## Continuous integration

`.github/workflows/windows.yml` (repository root) builds on `windows-latest` for every push that touches `windows/`: unit tests → XAML checks → single-file publish →
(optional signing) → Inno Setup installer → SHA-256 files → artifact `Evict-<version>-<sha>` containing `Evict.exe`,
`Evict-Setup-<version>.exe` and their `.sha256`. Pushing a tag `win-v1.3.4` (`v1.x` before the repositories were merged) additionally creates a GitHub Release with the
same four files attached (auto-generated notes) — that release is what the in-app update check reads.

The installer script is `installer/Evict.iss` (Inno Setup 6.3+). Locally: `ISCC.exe /DMyAppVersion=1.2.0 /DSourceDir=..\publish installer\Evict.iss`.

## Code signing

Unsigned executables trigger SmartScreen and some antivirus heuristics. The workflow signs `Evict.exe` and the
installer automatically when two repository secrets exist — nothing else changes:

| Secret | Value |
|---|---|
| `SIGN_PFX_BASE64` | the code-signing certificate as a base64 string: `[Convert]::ToBase64String([IO.File]::ReadAllBytes("cert.pfx")) \| Set-Clipboard` |
| `SIGN_PFX_PASSWORD` | its password |

Ways to get a certificate:

| Option | Cost / effort | SmartScreen reputation |
|---|---|---|
| **Azure Trusted Signing** (Microsoft) | ~US$10/month, identity validation, no hardware token; sign with the `azure/trusted-signing-action` instead of the pfx steps | Immediate (trusted publisher) |
| OV certificate from a CA (Sectigo, DigiCert, SSL.com, Certum…) | ~US$70–300/year; since 2023 the key must live on a hardware token or cloud HSM, so signing runs through the CA's cloud signing tool rather than a plain pfx | Builds over time with downloads |
| EV certificate | ~US$250–500/year, stricter validation | Immediate |
| **SignPath.io** Foundation | Free for open-source projects, signing happens in their service via a GitHub Action | Immediate |
| Self-signed (`New-SelfSignedCertificate -Type CodeSigningCert`) | Free — fine for your own machines after importing the cert into *Trusted Publishers*; does **not** remove SmartScreen for others | None |

## Project layout

```
Evict.sln
Directory.Build.props        shared build settings, product/version info
src/Evict.Core/              platform logic, no UI (net8.0, Windows-only APIs)
  Models/                    InstalledProgram, LeftoverItem, AppxPackageInfo, …
  Services/                  InstalledProgramsService, LeftoverScanner, LeftoverCleaner, UninstallRunner,
                             UninstallOrchestrator, RestorePointService, AppxService, BrowserExtensionService,
                             WingetService, WindowsUpdatesService, InstallMonitorService, ForceUninstallService,
                             FileShredder, UserAssistReader, BundlewareDetector, StartupService, ResidualScanner,
                             UpdateService (GitHub Releases check, download, self-replace), InstallerDetector,
                             ScheduledScanService (schtasks), SystemCleanupService, SettingsStore, HistoryStore
  Util/                      NameNormalizer (matching heuristics), UninstallCommandParser, PathUtil, …
src/Evict.App/               WPF UI (net8.0-windows), MVVM with CommunityToolkit.Mvvm
  Themes/                    Light.xaml / Dark.xaml brush sets (same keys)
  Styles/                    Controls.xaml (buttons, text boxes, checkbox, progress…), DataGrid.xaml, Converters.xaml
  ViewModels/                one per page + wizard / force-uninstall / shredder / updates
  Views/                     XAML pages and dialog windows
tests/Evict.Core.Tests/      xunit tests (run on any OS)
build/                       publish scripts, xaml_check.py (static XAML sanity checks)
installer/Evict.iss          Inno Setup script (compiled by CI into Evict-Setup-<version>.exe)
../.github/workflows/windows.yml  CI: test → publish → sign (optional) → installer → artifact / release
```

## Safety design

* The leftover scanner never proposes protected locations (Windows, Program Files root, user profile roots,
  Documents/Pictures…), never a folder that *contains* the install folder, and rates every item
  **Safe / Likely / Review**. *Review* items are unchecked by default.
* Registry deletion refuses hive roots and well-known containers (`Microsoft`, `Classes`, `Uninstall`, …).
* Files go to the Recycle Bin by default; locked files can be scheduled for deletion at reboot (admin).
* Browser preference files are backed up (`*.evict-backup`) before an extension entry is removed.
