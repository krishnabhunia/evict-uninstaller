# ChatGPT startup splash

Krishna approved the splash designs and requested a separate pull request. This work starts from main and does not modify or reuse the independent Claude splash PR.

## Approved behavior

- Compact splash by default, with expandable loading checklist and branded presentation.
- Follow the saved light/dark theme and preserve the existing Evict artwork.
- Paint the splash before loading essential local settings/history and constructing the main window.
- Keep the main window concealed until its first layout/render is ready. Reveal it before retiring the splash.
- Do not block opening on online update checks, full health scans, folder-size measurement, or other optional work.
- Display real startup stages rather than invented percentage completion.
- Keep tray/scheduled scan, self-cleanup, installer exit and forwarded-instance launches silent.
- Allow cancellation; report slow/failed startup without bypassing required initialization.
- Keep existing elevation and single-instance handling. Never change antivirus exclusions or policies.

## Acceptance

- First and subsequent launches, light/dark, high DPI, normal/minimized startup.
- Slow local load, initialization failure, cancellation and log access.
- Main window shown only after resources and first frame are ready.
- Command-line page/uninstall requests handled once after initialization.
- Tray/scheduled scan and second-instance paths do not show a redundant splash.
- Existing Python/.NET/XAML checks and Windows build/installer checks pass.

## Caveats

A splash addresses startup presentation. The reported green screen is not yet reproduced and must not be described as fixed without testing on the affected computer. There is no artificial minimum splash duration and no requirement for internet access.

Implementation and validation status are recorded in the pull request; this initial document is not a claim that application changes are complete.
