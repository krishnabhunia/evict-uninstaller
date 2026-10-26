"""Contract tests for the approved Windows Evict orange UI redesign.

These tests intentionally inspect WPF markup without requiring Windows or a UI session.
The Windows GitHub Actions build and xaml_check.py validate the native application.
"""

from __future__ import annotations

import unittest
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "windows" / "src" / "Evict.App"
XAML = {
    "main": APP / "MainWindow.xaml",
    "health": APP / "Views" / "HealthView.xaml",
    "programs": APP / "Views" / "ProgramsView.xaml",
    "updater": APP / "Views" / "SoftwareUpdaterView.xaml",
    "exclusions": APP / "Views" / "SoftwareUpdateExclusionsWindow.xaml",
    "wizard": APP / "Views" / "UninstallWizardWindow.xaml",
    "history": APP / "Views" / "HistoryView.xaml",
    "tools": APP / "Views" / "ToolsView.xaml",
    "windows_apps": APP / "Views" / "WindowsAppsView.xaml",
    "extensions": APP / "Views" / "BrowserExtensionsView.xaml",
    "monitor": APP / "Views" / "InstallMonitorView.xaml",
    "settings": APP / "Views" / "SettingsView.xaml",
}
XAML_NS = "{http://schemas.microsoft.com/winfx/2006/xaml}"


class WindowsOrangeRedesignTests(unittest.TestCase):
    def test_modified_xaml_is_well_formed(self) -> None:
        files = list(XAML.values()) + [
            APP / "Themes" / "Light.xaml",
            APP / "Themes" / "Dark.xaml",
            APP / "Styles" / "Controls.xaml",
            APP / "Styles" / "DataGrid.xaml",
        ]
        for path in files:
            with self.subTest(path=str(path.relative_to(ROOT))):
                ET.parse(path)

    def test_palette_is_consistent_in_light_and_dark(self) -> None:
        for theme, expected in (("Light", "#E66B48"), ("Dark", "#FF916C")):
            with self.subTest(theme=theme):
                root = ET.parse(APP / "Themes" / f"{theme}.xaml").getroot()
                brushes = {
                    node.attrib.get(XAML_NS + "Key"): node.attrib.get("Color")
                    for node in root
                    if node.tag.endswith("SolidColorBrush")
                }
                self.assertEqual(brushes.get("Brush.Accent"), expected)
                self.assertEqual(brushes.get("Brush.Nav.SelectedText"), "#FFFFFF")
                self.assertIn("Brush.Hero.Background", brushes)
                self.assertIn("Brush.Nav.GroupHeading", brushes)
                self.assertIn("Brush.SelectionBar.Background", brushes)

    def test_navigation_is_grouped_without_changing_page_keys(self) -> None:
        vm = (APP / "ViewModels" / "MainViewModel.cs").read_text(encoding="utf-8")
        shell = XAML["main"].read_text(encoding="utf-8")
        for group in ("WORKSPACE", "MAINTENANCE", "PREFERENCES"):
            self.assertIn(f'Group = "{group}"', vm)
        for page in ("Health", "Programs", "WindowsApps", "BrowserExtensions",
                     "SoftwareUpdater", "InstallMonitor", "Tools", "History", "Settings"):
            self.assertIn(f"Key = PageKey.{page}", vm)
        self.assertIn("CollectionViewSource.GetDefaultView(NavItems)", vm)
        self.assertIn("<ItemsControl.GroupStyle>", shell)

    def test_self_update_button_remains_conditional(self) -> None:
        shell = XAML["main"].read_text(encoding="utf-8")
        vm = (APP / "ViewModels" / "MainViewModel.cs").read_text(encoding="utf-8")
        self.assertIn('Visibility="{Binding HasAvailableUpdate, Converter={StaticResource BoolToVis}}"', shell)
        self.assertIn('Content="{Binding AvailableUpdateButtonText}"', shell)
        self.assertIn('Text="{Binding VersionText}"', shell)
        self.assertIn('public bool HasAvailableUpdate =>', vm)
        self.assertIn('Update to v{release.DisplayVersion}', vm)

    def test_health_cards_use_live_viewmodel_bindings(self) -> None:
        health = XAML["health"].read_text(encoding="utf-8")
        for binding in ("Score", "ScoreLabel", "Summary", "LastScanText", "Tiles", "ScanCommand", "FixSelectedCommand"):
            self.assertIn(binding, health)
        self.assertIn("Brush.Hero.Background", health)
        self.assertIn("Health checks", health)

    def test_programs_and_updater_preserve_selection_and_history(self) -> None:
        programs = XAML["programs"].read_text(encoding="utf-8")
        updater = XAML["updater"].read_text(encoding="utf-8")
        for binding in ("CountAll", "CountRecent", "CountLarge", "CountBroken",
                        "AllSelected", "SelectedCount", "UninstallSelectedCommand"):
            self.assertIn(binding, programs)
        for command in ("SelectAllCommand", "SelectNoneCommand", "ExcludeSelectedCommand",
                        "ManageExclusionsCommand", "UpdateSelectedCommand", "ShowHistoryCommand"):
            self.assertIn(command, updater)
        for binding in ("HistoryCount", "HistoryView", "HistoryFilter", "ExclusionsLabel"):
            self.assertIn(binding, updater)
        self.assertIn("History · last 7 days", updater)
        self.assertIn("winget is not available", updater)

    def test_safety_review_and_restoration_remain_visible(self) -> None:
        wizard = XAML["wizard"].read_text(encoding="utf-8")
        vm = (APP / "ViewModels" / "UninstallWizardViewModel.cs").read_text(encoding="utf-8")
        for binding in ("CreateRestorePoint", "SendToRecycleBin", "ScanLeftovers", "RegistryReview"):
            self.assertIn(binding, wizard)
        self.assertIn("StepLabel", wizard)
        self.assertIn("OnStepChanged", vm)
        exclusions = XAML["exclusions"].read_text(encoding="utf-8")
        self.assertIn("Restore selected", exclusions)
        self.assertIn("Restore all", exclusions)
        history = XAML["history"].read_text(encoding="utf-8")
        self.assertIn("Recycle Bin is emptied", history)
        self.assertIn("Activity History", history)

    def test_all_primary_pages_have_visible_titles(self) -> None:
        for name in ("health", "programs", "updater", "history", "tools", "windows_apps",
                     "extensions", "monitor", "settings"):
            with self.subTest(page=name):
                page = XAML[name].read_text(encoding="utf-8")
                self.assertIn('Style="{StaticResource Text.Title}"', page)


if __name__ == "__main__":
    unittest.main()
