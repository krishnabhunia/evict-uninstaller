# Evict Windows UI/UX redesign — coral/navy, integrated with RD1

Approved design reference: interactive preview from 26 October 2026. This PR was rebased by a two-parent merge onto the updated main, which already contains the independently merged RD1 redesign.

## What the conflict resolution preserves

- Deep-navy sidebar and light/dark coral accents, shared brush palette and rounded controls.
- Group labels **WORKSPACE** and **MAINTENANCE**, with **PREFERENCES** for Settings.
- **RD1 navigation wins structurally:** Home, Apps, Updates, Clean and History stay top-level; Apps and Clean retain their sub-tabs. Settings remains pinned at the bottom.
- Ctrl+K search window and search rail button (newer main) replace the earlier in-titlebar search box.
- Software Health's responsive Home two-column layout, scan state, score and checks survive; the score card uses the new hero palette.
- Installed Programs adds four live count cards above RD1 filter chips.
- Software Updater adds four live counters while keeping SH1 7-day update history, interruptions, Select All/None and persistent exclusions.
- Unified uninstall/update Activity timeline, Settings section navigator and RD1 uninstall stepper remain intact.
- Installed version stays beside Evict; top-right **Update to vX.Y.Z** appears only when an eligible release exists.

## Safety and release guards

- Windows WPF only; no macOS UI changes.
- No fake or hard-coded program counts, no new uninstall/update behavior.
- Preserve restore-point, Recycle Bin, registry-backup and leftover-review operations.
- Preserve RD1 keyboard search, high-DPI zoom, accessibility labels, and wizard code-behind.
- Preserve the released v3.0.0 main baseline; do not roll back its automatic versioning.

## Manual acceptance before merge

- [ ] Compare light and dark appearance on a real Windows build.
- [ ] Verify 960px minimum size, maximized view and 120–300% text zoom.
- [ ] Test grouped rail, Apps/Clean sub-tabs, Ctrl+K, Settings and activity timeline.
- [ ] Test filter selection, updater exclusions, missing winget, 7-day history and interrupted results.
- [ ] Verify conditional update button, update flow and installed version.
- [ ] Check uninstall stepper, leftovers, restoration, registry and error states.

Automated Python/WPF/XAML/unit tests and native installer smoke tests run in GitHub Actions. Actual visual acceptance still needs a Windows session.
