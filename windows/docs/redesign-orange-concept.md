# Evict Windows UI/UX redesign — orange desktop concept

Approved design reference: interactive prototype shown in the ChatGPT conversation on 26 October 2026.

## Scope
- Native Windows WPF application only; no macOS changes.
- Deep-navy grouped navigation, coral/orange accent, Fluent-inspired rounded cards, controls and dark/light themes.
- Software Health hero with the real health score, summary and scan state, followed by the 14 actionable health checks.
- Installed Programs: retain search, filters, batch selection, details, issue labeling and safe uninstall review.
- Software Updater: available/history tabs, real counts, Select All/None, persistent exclusions, rolling seven-day history, retry gating on winget.
- Windows Apps, Browser Extensions, Install Monitor, Activity History, Tools and Settings: consistent visual language without changing their service/business logic.
- Top-right `Update to vX.Y.Z` is visible only when the existing GitHub update check finds an eligible release; installed version remains near the logo.
- Uninstall and cleanup workflows keep restore-point, file confidence, Recycle Bin and registry-backup safety controls.

## Guardrails
- No mocked program names, counts or operating-system actions in the shipped WPF UI.
- No changes to cleanup safety decisions, file deletion, update source selection or release mechanics.
- Do not touch the separate UI redesign PR #28 or merge either PR.
- Preserve keyboard controls, text zoom, accessibility names and Windows 10/11 support.
- Automated CI must build, validate XAML, run core tests, smoke test packaging and produce PR beta before review readiness.

## Review checklist
- [ ] Light and dark appearance; sidebar selection and disabled controls.
- [ ] 960px minimum size, maximized view, 120–300% text zoom, long strings and clipped content.
- [ ] Keyboard tab order, screen reader names, high contrast.
- [ ] All navigation pages, table selection and search, progress and empty states.
- [ ] Update button hidden/shown under real update-availability states.
- [ ] Software Updater with winget missing, exclusions, seven-day history, cancelled/interrupted results.
- [ ] Uninstall review, leftovers and registry backup, Recycle Bin success/failure.
- [ ] Native Windows runtime visual smoke test after CI compilation.
