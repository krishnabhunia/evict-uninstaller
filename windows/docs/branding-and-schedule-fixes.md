# Branding, Windows icon, and scheduled scan fixes

Approved follow-up work, 27 October 2026. Deliver in a new pull request titled `ChatGPT-Evict Branding, Icon and Scheduled Scan Fixes`.

## Visible changes

- **Evict wordmark:** a dedicated white sidebar text resource keeps the name readable in Light and Dark themes while retaining the navy navigation rail.
- **Windows icon:** retain the original blue artwork and all nine sizes. Use conventional 32-bit bitmap frames for small icons and the original 256 pixel PNG for the largest frame. Application identity, pinned-shortcut grouping, and the Windows icon cache are not changed.
- **Scheduled Software Health scan:** choose Off / Every day / Once a week, time, and weekday, then select **Apply schedule**. Changing a dropdown or selecting **Cancel** does not create, replace, or remove a Windows task. Show saved configuration separately from pending edits.

## Scheduling integrity

Explicit Apply is one serialized transaction. Read the current owned task first, register or remove the requested schedule, verify the resulting task state, then persist preferences. A failed task operation or settings save preserves the previous configuration or reports that rollback could not be confirmed. Do not repeat the operation automatically after a block.

Task verification checks the intended executable, arguments, current Windows identity, interactive least-privilege principal, enabled state, schedule frequency, hour, and weekday. Reads distinguish a missing task from a denied or failed query. Opening Settings only inspects task state; it does not migrate or recreate tasks.

A newly applied schedule starts at the next future selected time. Keep the applied timestamp separate from the time of a completed scan.

Background `--scheduled-scan` and startup catch-up require an enabled saved schedule and a matching Windows task. Recheck configuration after asynchronous delays. Suppress initial Health-page automatic scanning on a headless launch until the schedule is verified; deliberate visible navigation retains manual/automatic Health-page behavior.

Settings are serialized and flushed to a unique file beside the destination before replacement. A failure before publication preserves the previous complete settings file; clean up only the temporary file created by that save operation.

## Validation

Automated coverage includes:

- Light/Dark wordmark binding and contrast.
- ICO frame bounds, dimensions, transparency, color-plane metadata, and unchanged 256 pixel artwork.
- Native Windows extraction of the published executable's small and large icons against the source icon.
- Schedule transaction, task ownership, verification failure, rollback, and settings-save failure.
- Deferred-scan validation, changed settings during the wait, and overlapping scheduled invocations.
- Atomic preferences publication, first-save failure, and preservation of unrelated files.

The repository's existing workflow runs the source checks, .NET unit tests, XAML checks, native compilation, and package smoke tests. No additional workflow is introduced.

## Bitdefender investigation and limits

The previous code immediately registered a Windows scheduled task on dropdown changes and saved the preference before Windows confirmed success. A scan could also start during headless initialization. The changes above address those confirmed application defects.

They do not prove that Bitdefender's quarantine is a false positive or guarantee antivirus compatibility. The exact threat name, affected file/process path, protection module, and event time are still needed. Do not disable protection, add an exclusion, restore quarantine, rename commands to evade detection, or submit a user's files to a third party automatically.

The existing signing setup is unchanged. A passing GitHub build is not a Bitdefender behavioral test.

## Manual acceptance

1. Switch Light -> Dark -> Light; the Evict name stays readable.
2. Check the new build's taskbar, Alt+Tab, tray, executable, and Start-menu icons in portable and installed modes at common display scales.
3. Change schedule fields without Apply and cancel them; Windows Task Scheduler and saved settings should remain unchanged.
4. Apply a schedule and inspect its saved status and next run time. Verify Off removes only the current account's Evict task.
5. In an isolated test environment, simulate registration or settings-save failure and verify the previous configuration remains in effect or an explicit recovery error is shown.
6. Verify missing/disabled/mismatched schedules do not run a headless health scan.
7. Review the actual Bitdefender detection event before declaring the quarantine problem resolved.
