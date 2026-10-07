# Audit fixes and validation

The October 2026 review covered Windows and macOS. This change keeps the existing release versions and addresses the findings below. Ambiguous ownership now requires review; an unavailable recovery or verification step reports a failure instead of silently continuing.

## Coverage

| Finding | Resulting behavior |
| --- | --- |
| W01 | Absolute paths are canonicalized before ownership/protected-root checks; ambiguous Windows path spellings fail closed. |
| W02 | Permanent cleanup and shredding do not traverse junctions or symbolic links into other folders. |
| W03 | Surviving installed versions and their parent folders are excluded; conflicting versions are rejected and name-only matches cannot trigger automatic cleanup. Process closure also requires fresh exclusive ownership or an explicit, unshared executable; unrelated child processes are never killed. |
| W04 | Removing an unpacked browser extension preserves its external source folder; only browser-owned extension data can be deleted. |
| W05 | Installation-monitor observations are explicitly unverified, have low confidence and start unchecked; personal document locations are excluded from cleanup conversion. |
| W06 | Service and task ownership uses executable path boundaries and actual task Exec actions; metadata text cannot establish ownership. |
| W07 | Recycling uses the recycle-only shell operation; errors and cancellation do not fall back to permanent deletion. Actual outcomes determine Undo eligibility. |
| W08 | Registry backups retain logical paths and originating views; Evict restores each view explicitly. Legacy unmarked backups remain supported. |
| W09 | An update requires a downloadable, valid, matching published SHA-256 checksum before it is accepted. |
| W10 | A program without an uninstaller requires an explicit force-removal choice and a scan/review. It cannot be reported successfully uninstalled without verified removal. |
| W11 | Choosing Skip in the running-program prompt stops force removal. |
| W12 | Failed file/registry restores stay pending for Undo retry; counters change only for confirmed restored items. |
| W13 | Update-cache cleanup restores services it attempted to stop in a finally block using a separate bounded recovery token. Originally stopped services stay stopped. |
| W14 | Antivirus protection uses Windows Security health; registrations alone cannot establish active protection. Unavailable health remains unknown. |
| W15 | Each health refresh requests a fresh program inventory and shares any active load. |
| W16 | Cleanup history uses stable operation IDs and actual per-job items; retries and partial Undo update the same record and its counters. |
| W17 | The unknown-version option is carried into individual winget upgrades. |
| W18 | winget parsing uses table geometry rather than English labels; errors or unrecognized output cannot produce an up-to-date status. |
| W19 | Windows-app removal snapshots its confirmed user/provisioning scope; scope controls and refresh are disabled during operations. |
| W20 | Per-package removal diagnostics survive refresh and remain visible in the last-results panel. Provisioning failures are reported. |
| W21 | Scheduled scans have SID-qualified names. Migration/removal of the old shared name requires proving current-user ownership. |
| W22 | Scheduled scans cannot terminate an interactive Evict instance after one hour. |
| W23 | Local packaging stops on failing native test or publish exit codes. |
| W24 | Windows-update removal uses interactive WUSA arguments and checks the post-operation state; cancellation and pending restart are distinguished. |
| W25 | Hibernation persists recovery state atomically before mutation, serializes transactions and retains recovery information when rollback fails. |
| W26 | Failed update/elevation handoff restores single-instance ownership and argument forwarding. |
| M01 | Shared vendor matches require manual selection, including with low-confidence preselection enabled. |
| M02 | The application bundle is deliberately selected; permission requirements are handled on the actual removal attempt. Partial removal is reported honestly. |
| M03 | History uses matching ISO 8601 codecs, reads legacy dates and preserves unreadable history instead of replacing it. |
| M04 | An active removal cannot be dismissed through Cancel or sheet dismissal; concurrent removal is guarded. |
| M05 | System-location scanning, low-confidence visibility, removal confirmation and low-confidence preselection settings affect their intended flows. |
| M06 | An Apple bundle identifier alone no longer makes a user-installed App Store app a protected system app. |
| M07 | Successful removal counts are based on verified Trash moves and cannot become negative. |
| M08 | Owned command-line links are validated separately, including dangling links, and are removed before their application bundle. |
| M09 | Overlapping application roots such as Setapp are deduplicated by standardized absolute bundle path. |
| UX1 | Installer-detection wording explains that recording starts on detection and can be discarded. |
| UX2 | Reset Defaults checks actual Windows integrations, removes the scheduled task regardless of saved settings and reports failures. |
| UX3 | Filtering recomputes the selection header and shows the number of selected programs hidden by the current filter. |
| UX4 | Data moved to Trash/Recycle Bin is distinguished from permanently deleted bytes. Windows history records actual removed bytes separately; macOS labels its existing size field as data moved to Trash. |

## Automated checks

Windows regression suites include `ScannerSafetyTests`, `CleanupSafetyTests`, `RegistryBackupTests`, `ProcessOwnershipTests`, `WorkflowStateTests`, `UpdateReliabilityTests`, `WingetReliabilityTests`, `PublishReliabilityTests` and `ToolsSafetyTests`. Destructive fixtures are confined to temporary test folders. Shell recycling, registry imports, installers, package removal and service changes use mocked dependencies in these tests.

Run from the repository root:

```powershell
dotnet test windows/tests/Evict.Core.Tests/Evict.Core.Tests.csproj -c Release
python windows/build/xaml_check.py
dotnet publish windows/src/Evict.App/Evict.App.csproj -c Release -o windows/Portable
```

macOS regressions cover history round trips/corrupt-file preservation, selection and settings policies, inventory/source classification, owned links, and verified removal outcomes. Run `swift test` inside `macos`; the macOS workflow also builds and verifies the universal app bundle.

## Interactive validation still needed

These changes were implemented on a Windows host. Automated tests and compilation do not establish end-to-end behavior of OS dialogs or real vendor uninstallers. Use disposable apps and a test account for these checks:

- Confirm force-uninstall Skip, failed-cleanup retry and partial Undo prompts match their final counters and history.
- Check an ordinary Recycle Bin operation, a non-recyclable target and cancellation; no permanent fallback should occur. Restore generated mixed-view registry backups through Evict, since manual `.reg` import uses the importing process's view.
- Cancel update-cache cleanup after services stop; confirm originally running services recover and originally stopped ones remain stopped.
- Exercise UAC/setup cancellation, interactive WUSA cancellation and hibernation persistence/rollback failures without touching production software.
- Check Windows-app scope controls/results, fresh health inventory, filtered selection summaries and integration-reset failures in the UI.
- On macOS, review bundle/vendor selection, writable versus restricted `/Applications` permissions, active-removal dismissal, all settings and Trash/history wording.

Installation-monitor events remain observations, not proof of which process wrote a file. Unverified items must be reviewed manually. The macOS app still has no privileged helper; restricted operations report their permission failure.
