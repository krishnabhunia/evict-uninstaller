# Changelog

## 1.12.1 — 2026-10-08

<!-- Evict automatic release; base: win-v1.12.0 -->

- fix(ci): keep one installation download and clean build artifacts

## 1.12.0 — 2026-10-08

<!-- Evict automatic release; base: win-v1.11.0 -->

- feat(updates): test beta restart updates and native packages

## 1.11.0 — 2026-10-07

<!-- Evict automatic release; base: win-v1.9.0 -->

- feat(updates): publish 1.10.0 for stable update verification

## 1.10.0 — 2026-10-07

- Publish an explicitly requested stable release from an unmerged test PR to verify the installed update flow.
- Verify installer and checksum selection, stable and beta channel behavior, corruption handling and cancelled downloads.
- Keep source, installer, runtime and release versions consistent; account for published stable test releases when calculating the next version.

## 1.9.0 — 2026-10-07

<!-- Evict automatic release; base: win-v1.8.0 -->

- Request running Evict to exit through its local pipe without extracting a temporary helper; stop silent installation if the process cannot close and write a setup log automatically.
- Upgrade GitHub Actions to supported Node.js 24 versions.
- Automatically calculate major, minor and patch release versions from PR and commit metadata; synchronize the source version, installer and changelog in GitHub Actions.
- Calculate PR betas from the same next stable version; retry failed publication without replacing published release files.

- Add an optional beta update channel, full beta version labels, release notes and explicit beta confirmation; stable updates remain the default.
- Publish tested Windows PR builds as GitHub prereleases with verified installer and portable assets, without merging the PR into main.

- Harden leftover ownership, protected paths and junction handling; keep external browser-extension projects and other installed versions safe.
- Make recycling, Undo, registry-view restoration and update checks fail safely, with truthful cleanup and history results.
- Correct force-removal prompts, installation-monitor review, health refresh, package-removal scope, updater errors and filtered selections.
- Restore update-service state after cancellation; preserve hibernation recovery records; isolate scheduled scans per user and improve antivirus and Windows Update diagnostics.
- Stop local packaging immediately when tests or publishing fail. Stable source versions stay unchanged; PR packages carry their own beta version.

- Every Windows release also comes as **`Evict-x.y.z.zip`** with exactly two folders: **installer** (`Evict-Setup-x.y.z.exe`,
  installs Evict) and **portable** (`Evict.exe`, runs from anywhere). The separate files stay on the release as before,
  so updates inside Evict work as they did. 1.8.0 gets its zip too.
- Windows release zip with only installer/ and portable/
- Merge pull request #10 from krishnabhunia/fix/audit-safety-and-reliability
- feat(release): automate semantic versions and fix installer shutdown

## 1.8.0 — close running programs, fallback and rollback when an uninstall fails (29 Sep 2026)

- **A running program is closed before it is uninstalled.** Its processes (install folder, main executable) are found;
  Evict asks it to close like clicking × (so it can offer to save your work), then force-closes only if needed.
  *Settings → Uninstalling → When the program is still running*: **Ask** (default: close / I closed it – check again /
  uninstall anyway / skip), **Close automatically**, or **Leave running**. The first wizard page warns when a selected
  program is running.
- **When the uninstaller fails, Evict asks what to do** instead of carrying on: *Try again*, *another way* (the
  uninstaller with its own window after a silent attempt, Windows Installer by product code, or the silent command),
  *Force uninstall* (remove its files, folders and registry entries – always reviewed first), or *Skip* (nothing is
  removed). In a batch, a skipped program asks whether to continue with the rest or stop.
- **Fixed: a failed or cancelled uninstall was still followed by the leftover scan**, which listed the still-installed
  program's own folder and entry as leftovers – with *Remove leftovers automatically* it could delete a program whose
  uninstall you had just cancelled. Leftovers are now only scanned after a successful uninstall or a chosen force
  uninstall; "exit code 0 but still installed" (a cancelled NSIS / Inno uninstaller) counts as a failure.
- Windows Installer busy with another installation (1618): retried automatically three times, 15 s apart.
- No restore point could be created: asks whether to continue without one or change nothing.
- **Rollback:** leftover items that could not be removed → *Retry* (closes the program's processes first), *Undo*, or
  keep. *Undo leftover removal* puts files and folders back from the Recycle Bin to their original place and registry
  entries back from their backup; *Open System Restore* (when a restore point was created) undoes what the program's own
  uninstaller changed. Force Uninstall has the same retry / undo.

## 1.7.0 — start as administrator (29 Sep 2026)

- **Evict starts as administrator by default.** New *Settings → Administrator rights → Start Evict as administrator*
  (on): Windows asks for permission when the window opens and Evict relaunches itself elevated, keeping its arguments
  (so *Uninstall with Evict* from Explorer still opens the right program). Declining the prompt keeps Evict running with
  your own rights. Standard (non-administrator) accounts are never prompted, because the prompt would run Evict under a
  different account. Starts hidden in the notification area (sign-in, scheduled scan) ask only when you open the window,
  and not while an installation is being recorded. `--no-elevate` skips the prompt once.
- A copy without administrator rights now reaches an Evict running as administrator: Explorer's *Uninstall with Evict*
  and a second start hand over to it instead of opening a second window.
- Setup and the uninstaller can close an Evict that runs as administrator (new `--exit` option), which they could not
  force-close before.

## 1.6.0 — Software Health: 14 categories and Fix selected (29 Sep 2026)

First public release since 1.3.3 – versions 1.3.4, 1.4.0 and 1.5.0 were built and tested but never published, so their
changes (listed below and under their own headings) all arrive with 1.6.0.

- Build output folders are now **`Portable`** (the stand-alone `Evict.exe`) and **`Installed`** (`Evict-Setup-x.y.z.exe`),
  in local builds and in the CI download; the files attached to a GitHub Release keep their names.

- **Installation files**: setup packages in Downloads and on the Desktop; ticked when the program is already installed or
  the file is older than 30 days; removed files go to the Recycle Bin. Also a System Cleanup category.
- **Software redundant files**: caches, logs, crash reports and temp folders that installed programs keep in AppData,
  LocalLow and (as administrator) ProgramData. A "Logs" folder that holds anything other than log files is listed for
  review only. Also a System Cleanup category.
- **Programs with uninstall issues** (was "Broken uninstall entries"): also programs whose uninstaller is missing, that
  have no uninstall command, or whose earlier uninstall failed. The Programs tab is now *Uninstall issues*.
- **Disturbing notifications**: every notification sender plus Windows' tip and suggestion prompts, with on/off switches;
  security and update senders stay on, promotional / bundled senders are recommended off.
- **Software permissions**: camera, microphone, location, contacts and more per app, with last use; Store apps switch one
  by one, desktop programs through Windows' single per-permission switch.
- **Software hibernation**: put third-party background services and scheduled tasks (mostly updaters) to sleep and wake
  them again; security, driver, VPN, audio, backup and sync components are never touched.
- **Malicious software & extensions**: Microsoft Defender protection, definitions and threats (quick scan on request),
  extensions from outside the web stores or forced by a policy, and unsigned startup programs in user folders.
- **Fix selected**: tick categories on the Software Health page and fix them in one go; each tile shows
  "N selected to fix, total M". Permissions and hibernation are opt-in.
- Setup detects a running Evict (installed or portable, in the notification area, or started as administrator) through
  its single-instance lock, offers to close it and waits until it has exited; silent installs and self-updates close it
  without asking (a self-update first gives the old version 10 s to exit on its own). The uninstaller asks the same.
- The version number is shown in the title bar next to the name (and in the window / taskbar title); before, it was only
  a small line at the bottom of the sidebar that scrolled out of view at larger text sizes.

## 1.4.0 (not published) — registry cleaning + clean self-removal (29 Sep 2026)

- **Registry Cleaner** (Tools): a separate module that finds registry entries pointing to programs, files and folders that
  no longer exist – broken uninstall entries, App Paths, Run / RunOnce, "Open with" applications, SharedDLLs, MuiCache,
  empty software keys (ticked); settings keys of removed programs and file-type commands (review); COM / ActiveX objects,
  type libraries and Windows Installer folder records (advanced); history lists such as recent documents, Run / search /
  typed-path / Open-Save history and UserAssist (privacy). Only entries whose target is provably gone from a fixed drive
  are listed – files on USB sticks or network drives never count, and System32 / SysWOW64 are both checked.
- **Registry backups everywhere**: before *any* registry key or value is deleted (wizard, Force Uninstall, Residual
  Cleaner, Install Monitor, Registry Cleaner) it is exported to a regedit-compatible `.reg` file; an entry that cannot be
  backed up is not deleted. Backups can be restored or deleted in the Registry Cleaner.
- **Uninstall wizard – separate Registry step**: leftover files & folders and leftover registry entries are reviewed on
  two pages; the summary offers **Undo registry changes**.
- **Install Monitor – "Clean registry…"**: removes only the registry keys a recorded installation created that still
  exist (for failed installs, or programs removed another way); warns if the program still looks installed.
- **Removing Evict itself leaves nothing behind**: the uninstaller now runs `Evict.exe --self-cleanup ask` – it always
  removes Evict's registry keys (HKCU and HKLM), autostart value, scheduled scan, context-menu entries registered from
  Settings, Send-to shortcut, `Evict.old.exe` files and the ~25 MB of native libraries .NET unpacks to `%TEMP%\.net\Evict`
  per version; a checklist offers settings/log, history + registry backups, the System Cleanup installer-package backup in
  `ProgramData\Evict` (which was never removed before), `*.evict-backup` browser files (never removed before) and Windows'
  own records of Evict.exe. Silent uninstalls remove only the integration. Portable copies: *Settings → Remove Evict from
  this PC…*.

## 1.3.4 (not published) — one repository for Windows and macOS (29 Sep 2026)

- The project moved to **github.com/krishnabhunia/evict-uninstaller** (renamed from `evict`; the old address redirects),
  with the Windows app in `windows/` and the macOS app in `macos/`.
- Update check: reads releases from the new repository and never offers a macOS release (`mac-v*` tags) as a Windows
  update. Windows releases are now tagged `win-vX.Y.Z`.
- Installer: support/update links point to the new repository (the installer's AppId is unchanged, so 1.3.4 upgrades
  existing installations in place).

## 1.3.3 — deeper registry cleanup, verified (17 Sep 2026)

- **Registry leftovers after an uninstall – wider search.** Besides SOFTWARE\<Program>, SOFTWARE\<Publisher>\<Program>,
  App Paths, Run keys and compatibility records, the Powerful Scan now also finds:
  program keys under *any* vendor key (SOFTWARE\<vendor>\<Program>), duplicate/orphaned Programs & Features entries,
  COM classes (CLSID) served from the program folder, file types / ProgIDs of the program, Explorer right-click
  commands and context-menu handlers, "Open with" entries on file extensions, Shared DLL records, Windows Firewall rules
  for the program's executables, and (as administrator) the program key of other signed-in users.
- **Every registry deletion is verified**: after deleting, Evict checks the key/value is really gone. The wizard's
  summary shows "Registry: N of M keys/values removed and verified gone" and names the items that need administrator
  rights (HKLM) instead of a raw "access denied".
- Missing spaces fixed in several "N item(s) …" texts (wizard, Force Uninstall, File Shredder, Install Monitor, Settings).

## 1.3.2 — second Windows QA pass (17 Sep 2026)

- **Install Monitor: detected installations were recorded empty** (e.g. "TwoButtonApp – 0 folders, 0 files"). In *Ask* mode
  recording only started after the click, when the installer had already finished, and the file watchers only started
  after the slow registry snapshot. Recording now starts the moment an installer is detected (the banner/notification
  offers "don't record" instead) and the file watchers start first.
- Residual Cleaner: broken entries of runtimes, redistributables, drivers and OEM tools (e.g. Microsoft Visual C++
  Redistributable, HP services) are marked **Review** and not pre-selected.
- System Cleanup: categories that need administrator rights are not pre-selected when Evict runs without them.
- Browser Extensions: profiles with the same name (e.g. two "Krishna" profiles) are listed separately instead of merged
  into one group with duplicate rows.
- Missing spaces in "Found … item(s)" / "of removable data" texts; disabled buttons are visible in the Light theme.

## 1.3.1 — hot-fix after first Windows QA (17 Sep 2026)

- **Fixed: every Tools dialog crashed on open** (System Cleanup, Startup Apps, File Shredder, Residual Cleaner, Windows
  Updates, Force Uninstall, uninstall wizard…) with "Something went wrong". Cause: the shared dialog style set
  `WindowStartupLocation`, which is not a dependency property. It is now set in code; the XAML check guards against it.
- Software Updater shows readable failure reasons ("installer hash does not match (0x8A150011)", "installer failed with
  1603"…) instead of raw negative exit codes.
- Text-size drop-down now follows Ctrl + / Ctrl − / Ctrl 0.
- Software Health score re-weighted: many flagged extensions/bloatware no longer drag the score to ~0.
- System Cleanup card description no longer cut off.
- Installer detection no longer reports installs started by the Software Updater, nor self-updaters of programs that are
  already installed (Program Files, AppData\Local\Programs).

## 1.3.0 — Build 4 (16 Sep 2026)

- **Text size 80–300 %** (default now **120 %**; existing settings that still had the old 100 % default are raised once).
  Ctrl + / − step 10 % up to 150 %, then 25 %; Ctrl 0 returns to 120 %. At large sizes the window scrolls instead of squeezing pages.
- **Notification-area (tray) icon** with menu: open, Health scan, widget, record an installation, detection on/off, exit.
  Settings: show icon, close to tray, minimize to tray, **start with Windows** (`Evict.exe --tray`).
- **Installer detection + automatic Install Monitor**: while Evict (or its tray icon) runs, new installer processes are
  recognised (setup/install file names, Inno Setup / NSIS temp stubs, `msiexec /i`, installer descriptions, Downloads folder);
  a notification offers to record the installation – or recording starts automatically (setting Off / Ask / Automatic).
  Recording waits for the installer's whole process tree and msiexec, then saves an Install Monitor log and notifies.
- **Scheduled Software Health scan**: daily or weekly through Task Scheduler (`Evict.exe --scheduled-scan`, per user, no
  admin); result arrives as a notification; missed runs (PC off) are caught up at the next start.
- **System Cleanup tool**: orphaned Windows Installer packages/patches (`C:\Windows\Installer`, cross-checked against every
  product's `LocalPackage`; moved to `ProgramData\Evict\InstallerCacheBackup` rather than deleted), data folders of removed
  Store apps, Windows Update download cache (services stopped/started), Delivery Optimization cache, Windows/user Temp
  (older than 24 h), Windows Error Reporting files, crash dumps, Windows.old (measured; opens Storage settings).
- **Software Updater runs updates in parallel** (1–6 at a time, default 3; overall progress bar). Windows Installer
  packages can only install one at a time, so "another installation is in progress" results are retried automatically.
- Installer: optional "Start Evict with Windows" task; uninstall removes the scheduled task and autostart entry.
- Documentation no longer references other products or companies.

## 1.2.0 — Build 3 (16 Sep 2026)

- **Installer**: `Evict-Setup-1.2.0.exe` built with Inno Setup on every CI run — per-user by default (no UAC) or
  all-users; optional desktop icon, Explorer context menu and *Send to* entry; Start-menu shortcuts for the app,
  the Easy Uninstall widget and a Software Health scan; clean uninstall (offers to delete `%LocalAppData%\Evict`)
- **Update check**: at start-up (setting) and via *Settings → Updates → Check now*, against GitHub Releases;
  blue banner + dialog with release notes; downloads the matching file, verifies its SHA-256, then either runs the
  new installer silently (installed copies) or swaps `Evict.exe` in place and restarts (portable copies);
  "Skip this version"; `--updated` shows a one-time "Evict was updated" notice
- **Code signing (optional)**: CI signs `Evict.exe` and the installer when the `SIGN_PFX_BASE64` /
  `SIGN_PFX_PASSWORD` secrets exist (see README → *Code signing*)
- Context-menu setting recognises an entry registered for all users by Setup
- Text size range is 90–140 %

## 1.1.0 — Build 2 (16 Sep 2026)

- Software Health dashboard (new home page) with one-click fixes
- Easy Uninstall floating widget (drag a target onto any window)
- Explorer right-click "Uninstall with Evict" + command line (`--uninstall-file`, `--uninstall`, `--scan`)
- Startup Apps manager
- Residual cleaner: leftovers of programs that were already uninstalled
- Known-bundleware database on top of the timing heuristic
- Text size / zoom setting
- Dark-theme polish: ComboBox, ScrollBar, TabControl, menus, RadioButton
- GitHub Actions CI: build, test, publish, release on tags

## 1.0.0 — Build 1 (16 Sep 2026)

- Programs list with All / Recently Installed / Large / Infrequently Used / Bundleware / Broken Entries tabs
- Uninstall wizard: restore point → uninstaller (interactive or silent) → Powerful Scan → review → clean → summary
- Batch uninstall, Force Uninstall
- Windows Apps (Appx) with bloatware flags and de-provisioning
- Browser Extensions (Chrome, Edge, Brave, Vivaldi, Opera, Firefox)
- Software Updater (winget)
- Install Monitor with "Uninstall using this log"
- Tools: File Shredder, Windows Updates, Restore Point
- History with CSV export, Settings with Light/Dark theme
