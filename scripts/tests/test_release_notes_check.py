"""Published releases must identify user-visible implementation details."""

import sys
from pathlib import Path
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from release_notes_check import meaningful_changes


class ReleaseNotesQualityTests(unittest.TestCase):
    def test_rejects_generic_title_only(self):
        self.assertEqual(meaningful_changes("## 4.2.0\n- feat: improve updates\n"), [])

    def test_accepts_specific_implementation(self):
        detailed = "- Version manager displays each release's complete, independently verified implementation list."
        self.assertEqual(meaningful_changes("## 4.2.0\n" + detailed), [detailed])


if __name__ == "__main__":
    unittest.main()
