"""Native reservation failures must never recycle tags or hide incomplete stable releases."""
import importlib.util
import io
import json
from pathlib import Path
import unittest

SCRIPT = Path(__file__).resolve().parents[1] / "prepare-release.py"
SPEC = importlib.util.spec_from_file_location("native_release_plan", SCRIPT)
PLAN = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(PLAN)


class NativeBetaReservationTests(unittest.TestCase):
    def test_first_compact_beta_keeps_three_digits(self):
        self.assertEqual(PLAN.reserve_beta_version("1.1.0", "1.1.0-beta001", [], []), "1.1.0-beta001")

    def test_failed_tag_and_draft_uploads_both_reserve_the_number(self):
        tags = ["mac-v1.1.0-beta001", "mac-v1.1.0-beta009", "win-v1.1.0-beta888", "mac-v1.2.0-beta999"]
        releases = [{"tag_name": "mac-v1.1.0-beta012", "draft": True, "prerelease": True}]
        self.assertEqual(PLAN.reserve_beta_version("1.1.0", "1.1.0-beta002", tags, releases), "1.1.0-beta013")

    def test_published_versions_and_existing_engine_floor_are_preserved(self):
        releases = [{"tag_name": "mac-v1.1.0-beta005", "draft": False, "prerelease": True}]
        self.assertEqual(PLAN.reserve_beta_version("1.1.0", "1.1.0-beta010", [], releases), "1.1.0-beta010")

    def test_legacy_betas_and_other_platforms_do_not_consume_compact_numbers(self):
        tags = ["mac-v1.1.0-beta.900.12.1", "win-v1.1.0-beta999", "mac-v2.0.0-beta999"]
        self.assertEqual(PLAN.reserve_beta_version("1.1.0", "1.1.0-beta001", tags, []), "1.1.0-beta001")

    def test_exhaustion_and_reserved_zero_fail_without_wrapping(self):
        for tags, releases in [(["mac-v1.1.0-beta999"], []),
                               ([], [{"tag_name": "mac-v1.1.0-beta999", "draft": True}]),
                               (["mac-v1.1.0-beta000"], [])]:
            with self.assertRaises(ValueError):
                PLAN.reserve_beta_version("1.1.0", "1.1.0-beta001", tags, releases)
        for proposed in ("1.1.0-beta1", "1.1.0-beta000", "1.2.0-beta001", "1.1.0-beta1000"):
            with self.assertRaises(ValueError):
                PLAN.reserve_beta_version("1.1.0", proposed, [], [])


class NativeStableRetryTests(unittest.TestCase):
    def setUp(self):
        self.plan = {"tag": "mac-v1.1.0", "core_version": "1.1.0", "publish": False}

    def complete_release(self):
        return {"tag_name": "mac-v1.1.0", "draft": False, "prerelease": False, "published_at": "2026-11-07T10:00:00Z",
                "assets": [{"name": f"Evict-1.1.0-macOS-universal.{extension}{suffix}", "state": "uploaded", "size": 100}
                           for extension in ("zip", "dmg") for suffix in ("", ".sha256")]}

    def test_complete_release_allows_a_normal_no_change_build(self):
        PLAN.require_safe_stable_retry(self.plan, ["mac-v1.1.0"], [self.complete_release()])

    def test_no_tag_is_a_fresh_release_not_a_retry(self):
        PLAN.require_safe_stable_retry(dict(self.plan, publish=True), [], [])

    def test_failed_stable_upload_does_not_silently_turn_into_no_publish(self):
        draft = dict(self.complete_release(), draft=True)
        for releases in ([], [draft]):
            with self.assertRaisesRegex(ValueError, "Re-run failed jobs"):
                PLAN.require_safe_stable_retry(self.plan, ["mac-v1.1.0"], releases)

    def test_missing_empty_duplicate_or_pending_assets_are_not_complete(self):
        for mutation in ("missing", "empty", "duplicate", "pending"):
            release = self.complete_release()
            if mutation == "missing":
                release["assets"].pop()
            elif mutation == "empty":
                release["assets"][0]["size"] = 0
            elif mutation == "duplicate":
                release["assets"].append(release["assets"][0].copy())
            else:
                release["assets"][0]["state"] = "new"
            with self.assertRaisesRegex(ValueError, "incomplete assets"):
                PLAN.require_safe_stable_retry(self.plan, ["mac-v1.1.0"], [release])

    def test_a_fresh_build_cannot_overwrite_an_existing_stable_tag(self):
        with self.assertRaisesRegex(ValueError, "do not move or overwrite"):
            PLAN.require_safe_stable_retry(dict(self.plan, publish=True), ["mac-v1.1.0"], [self.complete_release()])


class NativeReleasePaginationTests(unittest.TestCase):
    def test_drafts_are_kept_and_all_pages_read(self):
        calls = []

        def opener(request, timeout):
            calls.append(request.full_url)
            self.assertEqual(timeout, 30)
            values = ([{"tag_name": "mac-v1.1.0-beta001", "draft": False}] * 100 if len(calls) == 1
                      else [{"tag_name": "mac-v1.1.0-beta002", "draft": True}])
            return io.BytesIO(json.dumps(values).encode())

        releases = PLAN.native_release_metadata("krishnabhunia/evict-uninstaller", None, opener)
        self.assertEqual(len(calls), 2)
        self.assertTrue(releases[-1]["draft"])
        self.assertTrue(calls[-1].endswith("page=2"))

    def test_bad_response_or_unbounded_history_fails_closed(self):
        for payload in ({"error": "unexpected"}, ["not a release"]):
            with self.assertRaises(ValueError):
                PLAN.native_release_metadata("krishnabhunia/evict-uninstaller", None,
                                             lambda request, timeout: io.BytesIO(json.dumps(payload).encode()))
        with self.assertRaisesRegex(ValueError, "pagination window"):
            PLAN.native_release_metadata("krishnabhunia/evict-uninstaller", None,
                                         lambda request, timeout: io.BytesIO(json.dumps([{}] * 100).encode()))

    def test_missing_changelog_release_is_not_silently_substituted(self):
        text = "# Changes\n\n## 1.1.0 — today\n\n<!-- generated -->\n\n- Verified application recovery and staging.\n\n## 1.0.0\n\n- Old implementation.\n"
        self.assertEqual(PLAN.changelog_notes(text, "1.1.0"), "- Verified application recovery and staging.\n")
        with self.assertRaises(ValueError):
            PLAN.changelog_notes(text, "2.0.0")


if __name__ == "__main__":
    unittest.main()
