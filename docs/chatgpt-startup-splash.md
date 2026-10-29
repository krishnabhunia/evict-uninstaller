# ChatGPT startup splash

Krishna approved the splash designs and requested a separate pull request. This implementation starts from main and does not modify or reuse Claude's independent PR #31.

## Presentation

Compact is the default. The splash's View selector switches to Checklist or Branded for the current launch. These presentation choices are not persisted. All three use the existing Evict artwork, the saved Light/Dark theme and the assembly's real release version. The checklist displays completed startup operations, not invented time-based percentages.

The existing entry point still handles single-instance forwarding, self-cleanup, installer exit and elevation before WPF starts. The splash is therefore shown after an administrator prompt, not on top of it. It paints before composition-root initialization and the main settings/history reads. Tray and scheduled launches do not create a splash.

## Startup gate

1. Read preferences on a worker; apply theme/scale on the dispatcher.
2. Read local activity/update history on a worker.
3. Construct the workspace and UI-bound services on the dispatcher.
4. For interactive starts, await the first registry-based program inventory, not folder sizes or usage enrichment. The legacy load signal also completes on failure, so readiness additionally requires a successful initial population.
5. Construct the real main window and wait for ContentRendered while concealed. DWM cloaking plus zero opacity prevent exposing unfinished content. Reveal the prepared window before closing the splash.

Full health scans and inventory enrichment can continue asynchronously. Update checks and integration maintenance are not part of the gate. There is no artificial minimum display duration. Forwarded commands queue until the main window is ready, while --exit remains immediate.

## Slow / failed startup

After eight seconds the splash explains that startup is still in progress. It never opens an unfinished main window merely because a timer expired. Cancel/Escape exits; failure shows the failed stage, Show log and Exit. Restart the application to retry rather than duplicating partially initialized services. Inventory and first-frame waits are bounded at 60 and 15 seconds respectively. A cancelled/failed startup does not save partially initialized preferences on exit.

## Validation

Behavioral tests: required-stage ordering, progress truthfulness, slow-stage readiness, cancellation and failure. Python wiring tests: three presentations, icon preservation, XAML structure, first paint ordering, protected shutdown and nonblocking enrichment. Existing CI builds Windows, validates XAML, runs regression suites and installer checks. Actual results are in PR Checks; this document does not assert unobserved passes.

## Manual acceptance still required

- Cold/warm launch, Light/Dark, high DPI, maximized window, cancellation, slow local storage and error/log handling.
- Confirm taskbar/Alt+Tab identity and no unfinished-window flash on Windows 10/11.
- Tray/scheduled scan, second launch during loading, self-cleanup, installer --exit and declined/granted administrator prompt.
- Check initial program inventory and navigation, then confirm online checks and detailed scans continue without blocking the UI.

The reported green-screen cause is not reproduced. This feature addresses startup presentation; it is not proof of a graphics-driver or whole-monitor fix. No antivirus exclusions, security policies, signing secrets or release workflows are modified.
