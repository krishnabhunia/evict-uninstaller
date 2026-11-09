"""docs/FEATURES.md format and the "a feature PR updates the feature list" rule."""
from __future__ import annotations

import importlib.util
import io
import tempfile
import unittest
from contextlib import redirect_stderr, redirect_stdout
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
SPEC = importlib.util.spec_from_file_location("features_check", ROOT / "scripts" / "features_check.py")
features = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(features)

HEADER = "| SrNo. | Area | What it does | Since | Windows | Mac |\n|---|---|---|---|---|---|\n"


def document(*rows, intro="Everything Evict can do.\n\n"):
    return intro + HEADER + "".join(rows)


def row(number, since="1.0.0", area="Programs", what="Lists programs.", windows="Yes", mac="Not yet"):
    return f"| {number} | {area} | {what} | {since} | {windows} | {mac} |\n"


class FeaturesFormatTests(unittest.TestCase):
    def test_repository_feature_list_is_valid(self):
        text = (ROOT / "docs" / "FEATURES.md").read_text(encoding="utf-8")
        rows = features.parse(text)
        self.assertGreaterEqual(len(rows), 40)
        self.assertEqual([r["SrNo."] for r in rows], [str(n) for n in range(1, len(rows) + 1)])
        self.assertIn("Info window", [r["Area"] for r in rows])

    def test_valid_document_parses_every_column(self):
        rows = features.check(document(row(1), row(2, "4.2.0", "Info window", "Shows build details.", "Yes", "Yes")), "4.2.0")
        self.assertEqual(rows[1], {"SrNo.": "2", "Area": "Info window", "What it does": "Shows build details.",
                                   "Since": "4.2.0", "Windows": "Yes", "Mac": "Yes"})

    def test_rejects_wrong_or_missing_parts(self):
        cases = {
            "no intro": document(row(1), intro=""),
            "header": "Intro.\n\n| No. | Area | What it does | Since | Windows | Mac |\n|---|---|---|---|---|---|\n" + row(1),
            "no separator": "Intro.\n\n| SrNo. | Area | What it does | Since | Windows | Mac |\n" + row(1),
            "numbering gap": document(row(1), row(3)),
            "numbering order": document(row(2), row(1)),
            "short since": document(row(1, "1.0")),
            "word since": document(row(1, "Build 2")),
            "leading zero": document(row(1, "01.0.0")),
            "availability": document(row(1, windows="yes")),
            "empty area": document(row(1, area="")),
            "missing cell": document("| 1 | Programs | Lists programs. | 1.0.0 | Yes |\n"),
            "two tables": document(row(1)) + "\nMore.\n\n" + HEADER + row(1),
            "no rows": document(),
        }
        for name, text in cases.items():
            with self.subTest(name), self.assertRaises(features.FeaturesError):
                features.check(text, "4.2.0")

    def test_since_may_not_be_newer_than_the_version_being_built(self):
        text = document(row(1), row(2, "4.2.0"))
        features.check(text, "4.2.0")
        features.check(text, "4.10.0")
        with self.assertRaisesRegex(features.FeaturesError, "newer than the version being built"):
            features.check(text, "4.1.1")


class FeatureChangeRuleTests(unittest.TestCase):
    TEXT = document(row(1))

    def test_feature_or_major_pr_must_change_the_feature_list(self):
        for bump in ("minor", "major"):
            with self.subTest(bump=bump), self.assertRaisesRegex(features.FeaturesError, "must also add or update"):
                features.check(self.TEXT, "4.2.0", bump, ["windows/src/Evict.App/MainWindow.xaml"])
            features.check(self.TEXT, "4.2.0", bump, ["windows/src/x.cs", "docs/FEATURES.md"])

    def test_fix_and_documentation_prs_do_not_need_a_feature_row(self):
        for bump in ("patch", "none"):
            features.check(self.TEXT, "4.1.2", bump, ["windows/src/x.cs"])

    def test_cli_reports_plain_errors(self):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "FEATURES.md"
            path.write_text(self.TEXT, encoding="utf-8")
            changed = Path(folder) / "changed.txt"
            changed.write_text("windows/src/x.cs\n", encoding="utf-8")
            out, err = io.StringIO(), io.StringIO()
            with redirect_stdout(out), redirect_stderr(err):
                self.assertEqual(features.main(["--file", str(path), "--version", "4.2.0"]), 0)
                self.assertEqual(features.main(["--file", str(path), "--version", "4.2.0", "--bump", "minor",
                                                "--changed-files", str(changed)]), 1)
                self.assertEqual(features.main(["--file", str(path), "--version", "4.2.0", "--bump", "minor"]), 1)
            self.assertIn("1 features", out.getvalue())
            self.assertIn("docs/FEATURES.md", err.getvalue())


if __name__ == "__main__":
    unittest.main()
