"""Source wiring regressions for the native startup splash; behavioral tests live in .NET."""
from pathlib import Path
import unittest
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]
APP = ROOT / "windows/src/Evict.App"


class StartupSplashWiringTests(unittest.TestCase):
    def test_splash_xaml_and_both_theme_resources_are_valid(self):
        window = ET.parse(APP / "Views/StartupSplashWindow.xaml")
        self.assertEqual(window.getroot().attrib["WindowStyle"], "None")
        for name in ("Light", "Dark"):
            ET.parse(APP / f"Themes/{name}.xaml")

    def test_three_approved_views_keep_original_icon_and_real_status(self):
        xaml = (APP / "Views/StartupSplashWindow.xaml").read_text(encoding="utf-8")
        for label in ("Compact", "Checklist", "Branded", "Show log", "Cancel"):
            self.assertIn(f'Content="{label}"', xaml)
        self.assertIn("Assets/app-256.png", xaml)
        self.assertIn("Assets/app.ico", xaml)
        self.assertIn('AutomationProperties.LiveSetting="Polite"', xaml)

    def test_splash_paints_before_service_loading(self):
        source = (APP / "App.xaml.cs").read_text(encoding="utf-8")
        self.assertLess(source.index("ShowAndWaitForFrameAsync(splash"), source.index("Services = new AppServices()"))
        self.assertIn("await Task.Run(() => Services.Settings.Load(), ct)", source)
        self.assertLess(source.index("await StartupSequence.RunAsync"), source.index("splash?.Dismiss()"))
        self.assertLess(source.index("splash?.Dismiss()"), source.index("await CompleteStartupAsync"))

    def test_cancellation_does_not_save_partial_preferences(self):
        source = (APP / "App.xaml.cs").read_text(encoding="utf-8")
        self.assertIn("if (_startupComplete) try { Services?.Settings.Save(); }", source)
        self.assertIn("SingleInstance.StartServer(ReceiveStartupArgs)", source)
        self.assertIn("_pendingStartupArgs.Enqueue(args)", source)

    def test_ready_gate_uses_render_event_and_not_timer_reveal(self):
        source = (APP / "Services/StartupWindowPresenter.cs").read_text(encoding="utf-8")
        self.assertLess(source.index("window.ContentRendered +="), source.index("window.Show();"))
        self.assertIn("await rendered.Task.WaitAsync", source)
        self.assertIn("window.Opacity = 0", source)
        self.assertIn("if (!ready) window.Hide();", source)
        self.assertNotIn("Task.Delay", source)

    def test_window_presenter_imports_the_diagnostic_logger_namespace(self):
        # Core-only .NET tests do not compile this WPF file. Catch the missing
        # import before the later application publish step (CS0103 in PR #32).
        source = (APP / "Services/StartupWindowPresenter.cs").read_text(encoding="utf-8")
        logger = (ROOT / "windows/src/Evict.Core/Services/Log.cs").read_text(encoding="utf-8")
        namespace_line = next(line.strip() for line in logger.splitlines() if line.startswith("namespace "))
        namespace = namespace_line.removeprefix("namespace ").removesuffix(";")
        self.assertIn(f"using {namespace};", source.splitlines())
        self.assertIn("Log.Warn(", source)

    def test_initial_inventory_does_not_wait_for_folder_sizes(self):
        source = (APP / "ViewModels/ProgramsViewModel.Startup.cs").read_text(encoding="utf-8")
        self.assertIn("EnsureLoadedAsync()", source)
        self.assertIn("if (!_loadedOnce)", source)
        self.assertNotIn("EnsureFullyLoadedAsync", source)


if __name__ == "__main__":
    unittest.main()
