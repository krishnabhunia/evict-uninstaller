# Windows redesign RD1 (Evict 3.0)

Approved design canvas: <https://claude.ai/artifact/XF5CwtkK14EGVF3mReXVMs>. Choices taken: **N1** menu, **U1** uninstall side panel, **blue** accent.

## What changed

| Area | Before | Now |
|---|---|---|
| Look | Grey-blue palette, 236px sidebar with nine entries, title bar with page name | Quiet off-white / near-black palette (light and dark, same brush keys), 232px menu: Home · Apps · Updates · Clean · History, Settings at the bottom, version pill next to the logo, page header with title + subtitle, *Update to vX* pill top-right only when an update exists |
| Navigation | One page per entry | Entries with several pages show tabs: Apps (Programs · Store apps · Browser extensions), Clean (Tools · Install Monitor). Old `--page` names still work |
| Search | Per-page search boxes | **Ctrl+K** (or the box in the menu): programs (once Apps has loaded), pages, tools, settings sections |
| Home | Score + 14 tiles | Score, **Tidy N safe items** (= Fix selected), *Worth a look* (up to six areas with findings), *All checks* (all 14 with their ticks), *Recently*, Install Monitor switch |
| Programs | Tabs, seven columns, red bottom button | Filter chips, two-line rows with *Bundleware* / *Problem* tags, size bar, last used in warning colour after 3 months, red-text *Uninstall* per row, 370px drawer with *What Evict does*, dark selection tray |
| Uninstall | 900×640 dialog | Full-height panel at the right edge of the main window (or of the screen when maximized), step bar Confirm · Uninstall · Leftovers · Done; several programs run as one queue with one leftover review at the end (unchanged logic) |
| Leftover list | Wide grid (Type / Location / Size / Confidence) | One fluid row per item (tick, icon, path, kind, size, Safe/Likely/Review tag, open) — also in Force Uninstall and the Residual Cleaner |
| Clean | 12 equal cards | 9 tool tiles with one action each; Easy Uninstall widget, Programs and Features, Storage Settings and Evict's data folder as small buttons |
| History | Uninstalls only, table | Uninstalls + program updates in one timeline grouped by day, chips All / Uninstalls / Updates |
| Settings | One long page | Section list with search: General, Uninstalling, Updates, Background & tray, Appearance, Advanced, About |

## Where it lives

| Part | Files |
|---|---|
| Palette | `windows/src/Evict.App/Themes/Light.xaml`, `Dark.xaml` (new keys: `Brush.Surface.Sunken`, `Brush.Accent.Text`, `Brush.Danger.Text`, `Brush.Tray.*`) |
| New styles | `Styles/Controls.xaml` → `Button.RailSearch`, `Button.Update`, `Button.DangerText`, `Button.Tile`, `CheckBox.Switch`, `Tag`, `Text.Eyebrow` |
| Menu, tabs, header | `MainWindow.xaml`, `ViewModels/MainViewModel.cs` (`NavItemViewModel.Pages`, `PageTabViewModel`, `PageInfo`) |
| Ctrl+K | `Views/SearchWindow.xaml(.cs)`, `ViewModels/SearchViewModel.cs`, ranking in `Evict.Core/Util/QuickSearch.cs` |
| History timeline | `Evict.Core/Services/ActivityTimeline.cs`, `ViewModels/HistoryViewModel.cs`, `Views/HistoryView.xaml` |
| Uninstall panel | `Views/UninstallWizardWindow.xaml(.cs)` (`DockToOwner`), `UninstallWizardViewModel.StepBar` |
| Tests | `windows/tests/Evict.Core.Tests/RedesignRd1Tests.cs` |

## Not verified

Built, unit-tested and XAML-checked on Linux; the Windows CI job builds, tests and publishes the beta. Nobody has looked at the running app yet — check the list in the PR before merging.
