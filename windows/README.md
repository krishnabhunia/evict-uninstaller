# Evict Uninstaller

A complete Windows uninstaller — written in C# / .NET 8 / WPF,
delivered as a single portable `Evict.exe` **or** an `Evict-Setup-x.y.z.exe` installer (Inno Setup).

| Module | What it does | Status |
|---|---|---|
| **Programs** | All installed programs (64-bit, 32-bit, per-user) with icons, size, install date, last-used; tabs for *All / Recently Installed / Large / Infrequently Used / Bundleware / Broken Entries*; search, sort, multi-select | ✅ |
| **Uninstall wizard** | Optional System Restore point → runs the program's own uninstaller (interactive or silent) → **Powerful Scan** for leftovers → review with confidence rating → delete (Recycle Bin optional) → summary | ✅ |
| **Powerful Scan** | Finds leftover folders/files (Program Files, ProgramData, AppData of every user), Start-menu / desktop shortcuts, registry keys & values (vendor keys, App Paths, Run entries, AppCompat, MuiCache…), services and scheduled tasks | ✅ |
| **Batch uninstall** | Queue several programs; one review step for all leftovers | ✅ |
| **Force Uninstall** | For broken/missing uninstallers: pick a program or point at a folder/exe, kill its processes, remove everything it owns | ✅ |
| **Windows Apps** | Store / UWP / MSIX packages incl. pre-installed bloatware; remove per user or all users, de-provision | ✅ |
| **Browser Extensions** | Chrome, Edge, Brave, Vivaldi, Opera (all profiles) + Firefox; flags broad permissions; removes with browser closed | ✅ |
| **Software Updater** | Outdated programs via `winget upgrade`; one-click update with live output; **updates run in parallel** (1–6 at a time, MSI conflicts retried) | ✅ / Build 4 |
| **Install Monitor** | Records files, folders and registry keys created by an installer (FileSystemWatcher + registry snapshot diff); later "Uninstall using this log" | ✅ |
| **Tools** | File Shredder (1 / 3 / 7 passes), Windows Updates uninstall (wusa), Create Restore Point, shortcuts to Windows tools | ✅ |
| **History** | Every operation with leftovers found/removed and bytes reclaimed; CSV export; rescan leftovers | ✅ |
| **Settings** | Light/Dark theme, defaults for the wizard, thresholds for the tabs | ✅ |
| **Software Health** (home page) | Score + tiles for outdated programs, leftovers, broken entries, bundleware, risky extensions, unused programs, bloatware, startup items – each with a one-click action | ✅ Build 2 |
| **Easy Uninstall widget** | Floating always-on-top target: drag it onto any program window, or drop a shortcut/.exe on it | ✅ Build 2 |
| **Explorer context menu + command line** | "Uninstall with Evict" on .exe / .lnk files; `--uninstall-file`, `--uninstall`, `--scan`, `--widget`, `--page`; second launches forward to the running window | ✅ Build 2 |
| **Startup Apps** | Run/RunOnce keys + Startup folders with the Task-Manager enable/disable switch | ✅ Build 2 |
| **Residual Cleaner** | Leftovers of programs uninstalled earlier: from History, broken entries, and unmatched folders (heuristic, review-only) | ✅ Build 2 |
| **Known-bundleware list** | Name database on top of the timing heuristic; user-extensible via `%LocalAppData%\Evict\bundleware.json` | ✅ Build 2 |
| **Text size / zoom**, dark-theme polish | 80–300 %, default 120 % (Ctrl + / − / 0); the window scrolls at large sizes; themed ComboBox, ScrollBar, TabControl, menus, RadioButton, Expander | ✅ Build 2 / 4 |
| **Installer** | `Evict-Setup-x.y.z.exe` (Inno Setup): per-user (no UAC) or all-users, Start-menu shortcuts, optional desktop icon / Explorer context menu / *Send to*, clean uninstall | ✅ Build 3 |
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

On start (Settings → *Updates*, on by default) Evict asks `api.github.com/repos/krishnabhunia/evict/releases/latest`
for the newest tag; if the API is rate-limited it falls back to the `releases/latest` redirect. A newer version shows a
blue banner → *Update now* opens a dialog with the release notes. The download is verified against the `.sha256`
asset published by CI. Installed copies start `Evict-Setup-x.y.z.exe /SILENT /CLOSEAPPLICATIONS /NORESTART /EVICTUPDATE=1`
(which relaunches Evict); portable copies rename the running `Evict.exe` to `Evict.old.exe`, move the new file in and
restart with `--updated`. The check needs the repository (or at least its releases) to be public — a private repository
answers 404 and the status reads "no published release".

## Building from source

Requirements: .NET 8 SDK (Windows, Linux or macOS — the project sets `EnableWindowsTargeting`).

```bash
dotnet test tests/Evict.Core.Tests            # 195 unit tests for the pure logic
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
```
If Evict is already running, a second launch hands its arguments to the open window.

## Continuous integration

`.github/workflows/build.yml` builds on `windows-latest` for every push: unit tests → XAML checks → single-file publish →
(optional signing) → Inno Setup installer → SHA-256 files → artifact `Evict-<version>-<sha>` containing `Evict.exe`,
`Evict-Setup-<version>.exe` and their `.sha256`. Pushing a tag `v1.2.0` additionally creates a GitHub Release with the
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
.github/workflows/build.yml  CI: test → publish → sign (optional) → installer → artifact / release
```

## Safety design

* The leftover scanner never proposes protected locations (Windows, Program Files root, user profile roots,
  Documents/Pictures…), never a folder that *contains* the install folder, and rates every item
  **Safe / Likely / Review**. *Review* items are unchecked by default.
* Registry deletion refuses hive roots and well-known containers (`Microsoft`, `Classes`, `Uninstall`, …).
* Files go to the Recycle Bin by default; locked files can be scheduled for deletion at reboot (admin).
* Browser preference files are backed up (`*.evict-backup`) before an extension entry is removed.
