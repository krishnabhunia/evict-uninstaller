Everything Evict can do, one row per feature. "Since" is the first Evict release (the suite version in windows/CHANGELOG.md) that had it; the Windows and Mac columns say which app has it. The app's Info window (ⓘ next to the version, or F1) shows this table.

| SrNo. | Area | What it does | Since | Windows | Mac |
|---|---|---|---|---|---|
| 1 | Programs | Every installed program (64-bit, 32-bit and per-user) with its icon, size, install date and last use; filter chips, search, sort and selecting several at once. On the Mac: every app in a sortable table. | 1.0.0 | Yes | Yes |
| 2 | Uninstall wizard | Optional restore point, closes the program if it is running, runs its own uninstaller, offers another way when that fails, then finds the leftovers for you to review before anything is removed. | 1.0.0 | Yes | Yes |
| 3 | Powerful Scan | Finds leftover folders, files, shortcuts, registry keys, services and scheduled tasks after an uninstall (on the Mac: support files, caches, containers and launch agents). | 1.0.0 | Yes | Yes |
| 4 | Batch uninstall | Removes several programs in one go, with one review of all their leftovers. | 1.0.0 | Yes | Yes |
| 5 | Force Uninstall | For broken or missing uninstallers: pick a program, a folder or an .exe and remove everything it owns. | 1.0.0 | Yes | Yes |
| 6 | Windows Apps | Microsoft Store apps and the ones Windows comes with; remove them for you or for all users. | 1.0.0 | Yes | Not yet |
| 7 | Browser Extensions | Extensions in Chrome, Edge, Brave, Vivaldi, Opera and Firefox, with risky permissions flagged; removed while the browser is closed. | 1.0.0 | Yes | Yes |
| 8 | Software Updater | Lists outdated programs from winget and updates them, several at a time; a History tab keeps the last 7 days of updates. | 1.0.0 | Yes | Yes |
| 9 | Install Monitor | Records what an installer adds (files, folders, registry keys) so it can be removed completely later. | 1.0.0 | Yes | Yes |
| 10 | Clean tools | File Shredder, removing Windows updates, creating a restore point, and shortcuts to Windows tools. | 1.0.0 | Yes | Not yet |
| 11 | History | One timeline of every uninstall and program update, with CSV export. | 1.0.0 | Yes | Yes |
| 12 | Settings | Light or dark theme, defaults for uninstalling, and the limits used by the lists. | 1.0.0 | Yes | Yes |
| 13 | Home (Software Health) | A health score with one-click fixes and a "Tidy N safe items" button. | 1.1.0 | Yes | Yes |
| 14 | Easy Uninstall widget | A floating target: drag it onto any program window, or drop a shortcut or .exe on it, to uninstall that program. | 1.1.0 | Yes | Not yet |
| 15 | Explorer menu and command line | "Uninstall with Evict" on .exe and shortcut files, and command-line options such as --uninstall and --scan. | 1.1.0 | Yes | Not yet |
| 16 | Startup Apps | Programs that start with the computer, with an on/off switch (on the Mac: launch agents and daemons, orphans flagged). | 1.1.0 | Yes | Yes |
| 17 | Residual Cleaner | Leftovers of programs that were removed earlier, found from History, broken entries and unmatched folders. | 1.1.0 | Yes | Not yet |
| 18 | Known-bundleware list | A list of programs that often come bundled with others, which you can add to. | 1.1.0 | Yes | Not yet |
| 19 | Text size | Zoom the whole window from 80 to 300 % (Ctrl + / Ctrl − / Ctrl 0). | 1.1.0 | Yes | Not yet |
| 20 | Installer | Windows: Setup for one user (no administrator prompt) or all users, with Start-menu shortcuts and a clean uninstall. Mac: a disk image to drag into Applications. | 1.2.0 | Yes | Yes |
| 21 | Update check | Checks GitHub Releases, shows the release notes and installs a verified download you choose (on the Mac: Evict Versions). | 1.2.0 | Yes | Yes |
| 22 | Code signing | Signs the downloads in GitHub Actions once certificate secrets are added (Authenticode on Windows, Developer ID and notarization on the Mac). | 1.2.0 | Yes | Yes |
| 23 | Notification-area icon | Tray menu, close or minimise to the tray, and start with Windows (on the Mac: menu-bar actions). | 1.3.0 | Yes | Yes |
| 24 | Installer detection | Notices installers as they start and records them with Install Monitor, after asking or automatically. | 1.3.0 | Yes | Not yet |
| 25 | Scheduled Health scan | A daily or weekly scan through Task Scheduler, with the result as a notification. | 1.3.0 | Yes | Yes |
| 26 | System Cleanup | Orphaned Windows Installer packages (backed up first), update caches, temp files, error reports, crash dumps and Windows.old. | 1.3.0 | Yes | Not yet |
| 27 | Registry Cleaner | Registry entries that point to programs and files that no longer exist, sorted into safe, review, advanced and privacy groups. | 1.4.0 | Yes | Not yet |
| 28 | Registry backups | Every registry deletion is first saved to a .reg file that can be restored; anything that cannot be saved is not deleted. | 1.4.0 | Yes | Not yet |
| 29 | Removing Evict itself | Uninstalling Evict also removes its own registry entries, start-up entries and other traces, and asks about settings and history. | 1.4.0 | Yes | Not yet |
| 30 | Health: Installation files | Setup files in Downloads and on the Desktop that are no longer needed, moved to the Recycle Bin. | 1.6.0 | Yes | Not yet |
| 31 | Health: Software redundant files | Caches, logs, crash reports and temp folders that installed programs leave behind. | 1.6.0 | Yes | Not yet |
| 32 | Health: Programs with uninstall issues | Programs whose uninstall entry is broken or whose uninstaller is missing or failed before. | 1.6.0 | Yes | Not yet |
| 33 | Health: Disturbing notifications | Apps and Windows tips allowed to show notifications, with promotional ones recommended off. | 1.6.0 | Yes | Not yet |
| 34 | Health: Software permissions | Camera, microphone, location and other permissions per app, with when they were last used. | 1.6.0 | Yes | Not yet |
| 35 | Health: Software hibernation | Lets background services and scheduled tasks of other programs sleep, and wakes them again. | 1.6.0 | Yes | Not yet |
| 36 | Health: Malicious software & extensions | Microsoft Defender status, extensions from outside the web stores, and unsigned programs starting from user folders. | 1.6.0 | Yes | Not yet |
| 37 | Start as administrator | Asks Windows for administrator rights when the window opens, so machine-wide programs and leftovers can be removed. | 1.7.0 | Yes | Not yet |
| 38 | Beta updates | An optional channel that offers tested pull-request builds before they are released. | 1.9.0 | Yes | Yes |
| 39 | Start-up splash | A small splash screen while settings, history and programs load. | 4.1.0 | Yes | Yes |
| 40 | Info window | The ⓘ button next to the version (or F1) shows this build, what is implemented, what is new and what the app is made of, and copies the build details. | 4.2.0 | Yes | Yes |
