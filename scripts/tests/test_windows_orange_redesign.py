"""WPF UI contracts for Evict's orange redesign merged with the RD1 navigation.

These are source-level checks; GitHub Actions additionally builds and tests the native app.
"""
from __future__ import annotations

import unittest
import xml.etree.ElementTree as ET
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "windows" / "src" / "Evict.App"
THEMES = APP / "Themes"
VIEWS = APP / "Views"
XAML_NS = "{http://schemas.microsoft.com/winfx/2006/xaml}"


def read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


class WindowsOrangeRedesignTests(unittest.TestCase):
    def test_modified_xaml_is_well_formed(self) -> None:
        files = [
            APP / "MainWindow.xaml",
            THEMES / "Light.xaml", THEMES / "Dark.xaml",
            APP / "Styles" / "Controls.xaml", APP / "Styles" / "DataGrid.xaml",
            *VIEWS.glob("*.xaml"),
        ]
        for path in files:
            with self.subTest(path=path.name):
                ET.parse(path)

    def test_palette_and_rd1_resource_keys_survive_in_both_themes(self) -> None:
        definitions = []
        for theme, expected in (("Light", "#E66B48"), ("Dark", "#FF916C")):
            root = ET.parse(THEMES / f"{theme}.xaml").getroot()
            brushes = {
                node.attrib.get(XAML_NS + "Key"): node.attrib.get("Color")
                for node in root if node.tag.endswith("SolidColorBrush")
            }
            self.assertEqual(brushes.get("Brush.Accent"), expected)
            self.assertIn("Brush.Hero.Background", brushes)
            self.assertIn("Brush.Nav.GroupHeading", brushes)
            self.assertIn("Brush.Nav.SelectedText", brushes)
            self.assertIn("Brush.SelectionBar.Background", brushes)
            self.assertIn("Brush.Surface.Sunken", brushes)
            self.assertIn("Brush.Accent.Text", brushes)
            definitions.append(set(brushes))
        self.assertEqual(*definitions)

    def test_grouped_rail_preserves_rd1_tabbed_pages_and_search(self) -> None:
        vm = read(APP / "ViewModels" / "MainViewModel.cs")
        shell = read(APP / "MainWindow.xaml")
        for group in ("WORKSPACE", "MAINTENANCE", "PREFERENCES"):
            self.assertIn(f'Group = "{group}"', vm)
        for page in ("Health", "Programs", "WindowsApps", "BrowserExtensions",
                     "SoftwareUpdater", "InstallMonitor", "Tools", "History", "Settings"):
            self.assertIn(f"PageKey.{page}", vm)
        self.assertIn("CollectionViewSource.GetDefaultView(NavItems)", vm)
        self.assertIn("<ItemsControl.GroupStyle>", shell)
        self.assertIn("CurrentTabs", shell)
        self.assertIn("OpenSearchCommand", shell)
        self.assertIn("PREFERENCES", shell)

    def test_update_button_visibility_and_version_are_data_bound(self) -> None:
        shell = read(APP / "MainWindow.xaml")
        vm = read(APP / "ViewModels" / "MainViewModel.cs")
        self.assertIn('Visibility="{Binding HasAvailableUpdate, Converter={StaticResource BoolToVis}}"', shell)
        self.assertIn('AutomationProperties.Name="{Binding AvailableUpdateButtonText}"', shell)
        self.assertIn('Text="{Binding VersionText}"', shell)
        self.assertIn("public bool HasAvailableUpdate =>", vm)
        # Version picker (VP1): the button counts the newer versions and opens the picker; no one-click "Update to vX".
        self.assertIn('$"{n} updates available"', vm)
        self.assertNotIn("Update to v{release.DisplayVersion}", vm)

    def test_health_retains_real_data_and_new_home_layout(self) -> None:
        health = read(VIEWS / "HealthView.xaml")
        for binding in ("Score", "ScoreLabel", "Summary", "LastScanText",
                        "Tiles", "ScanCommand", "FixSelectedCommand", "WorthALook"):
            self.assertIn(binding, health)
        self.assertIn("Brush.Hero.Background", health)
        self.assertIn("HomeGrid", health)
        self.assertIn("All checks", health)

    def test_programs_and_updater_keep_metrics_and_history(self) -> None:
        programs = read(VIEWS / "ProgramsView.xaml")
        updater = read(VIEWS / "SoftwareUpdaterView.xaml")
        for binding in ("CountAll", "CountRecent", "CountLarge", "CountBroken",
                        "AllSelected", "SelectedCount", "UninstallSelectedCommand"):
            self.assertIn(binding, programs)
        for command in ("SelectAllCommand", "SelectNoneCommand", "ExcludeSelectedCommand",
                        "ManageExclusionsCommand", "UpdateSelectedCommand", "ShowHistoryCommand"):
            self.assertIn(command, updater)
        for binding in ("HistoryCount", "HistoryView", "HistoryFilter", "ExclusionsLabel"):
            self.assertIn(binding, updater)
        self.assertIn("HISTORY · 7 DAYS", updater)
        self.assertIn("winget is not available", updater)

    def test_uninstall_safeguards_rd1_stepper_and_activity_recovery(self) -> None:
        wizard = read(VIEWS / "UninstallWizardWindow.xaml")
        vm = read(APP / "ViewModels" / "UninstallWizardViewModel.cs")
        for binding in ("CreateRestorePoint", "SendToRecycleBin", "ScanLeftovers", "RegistryReview"):
            self.assertIn(binding, wizard)
        self.assertIn("StepBar", wizard)
        self.assertIn("NotifyPropertyChangedFor(nameof(StepBar))", vm)
        exclusions = read(VIEWS / "SoftwareUpdateExclusionsWindow.xaml")
        self.assertIn("Restore selected", exclusions)
        self.assertIn("Restore all", exclusions)
        history = read(VIEWS / "HistoryView.xaml")
        self.assertIn("Recycle Bin is emptied", history)
        self.assertIn("7 days", history)

    def test_every_page_keeps_global_header_and_rd1_settings(self) -> None:
        shell = read(APP / "MainWindow.xaml")
        self.assertIn('Text="{Binding CurrentTitle}"', shell)
        self.assertIn('Text="{Binding CurrentSubtitle}"', shell)
        settings = read(VIEWS / "SettingsView.xaml")
        self.assertIn("SelectedSection", settings)
        self.assertIn("Settings", settings)


if __name__ == "__main__":
    unittest.main()
