"""Offline release-policy and temporary-repository regression tests."""
from __future__ import annotations

from contextlib import redirect_stderr, redirect_stdout
from io import BytesIO, StringIO
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch
from urllib.error import HTTPError

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from versioning import (Bump, CoreVersion, Git, GitHub, VersionError, affects_platform,
                        build_plan, bundle_intent, conventional_intent, generated_release_commit,
                        highest_stable_tag, main, release_intent, retired_stable_reservation, source_version,
                        RETIRED_STABLE_RESERVATIONS, RETIRED_RESERVATION_MARKER,
                        sync_sources, update_changelog, write_outputs)


class FakeApi:
    def __init__(self, mapping=None, releases=None):
        self.mapping = mapping or {}
        self.releases = releases or []
        self.repository = "fixtures/repository"
        self.calls = []

    def merged_pull_requests(self, sha):
        self.calls.append(sha)
        return self.mapping.get(sha, [])

    def published_stable_releases(self, platform):
        return [release for release in self.releases
                if release.get("draft") is False and release.get("prerelease") is False
                and release.get("published_at")
                and highest_stable_tag([release.get("tag_name", "")], platform)]

    def retired_stable_reservations(self, platform):
        return [release for release in self.releases
                if retired_stable_reservation(release, self.repository, platform) is not None]


def published(version, body="", **overrides):
    release = {"tag_name": "win-v" + version, "body": body, "draft": False,
               "prerelease": False, "published_at": "2026-10-07T00:00:00Z"}
    release.update(overrides)
    return release


def pull(title, number=12, body="", labels=None):
    return {"title": title, "number": number, "body": body, "labels": labels or [],
            "merged_at": "2026-10-07T00:00:00Z",
            "base": {"ref": "main", "repo": {"full_name": "fixtures/repository"}}}


class RepositoryFixture:
    def __init__(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="evict-version-tests-")
        self.root = Path(self.temporary.name)
        self.git = Git(self.root)
        self.git.run("init", "-b", "main")
        self.git.run("config", "user.name", "Release test fixture")
        self.git.run("config", "user.email", "fixture@example.invalid")
        self.write("windows/Directory.Build.props", "<Project>\n<PropertyGroup>\n<Version>1.8.0</Version>\n<InvariantGlobalization>false</InvariantGlobalization>\n</PropertyGroup>\n</Project>\n")
        self.write("windows/installer/Evict.iss", '#ifndef MyAppVersion\n#define MyAppVersion "1.8.0"\n#endif\n#ifndef MyAppNumericVersion\n#define MyAppNumericVersion MyAppVersion\n#endif\n')
        self.write("windows/CHANGELOG.md", "# Changelog\n\n## 1.8.1 (not published)\n\n- Preserve all reviewed cleanup and beta notes.\n\n## 1.8.0 — previous release\n\nOld notes.\n")
        self.write("windows/src/app.cs", "original application\n")
        self.write("macos/Sources/EvictKit/Version.swift", 'public enum Version {\npublic static let current = "0.1.0"\npublic static let build = "1"\n}\n')
        self.write("macos/Sources/App.swift", "original application\n")
        self.write("macos/CHANGELOG.md", "# Changelog\n\n## Unreleased\n\n- Preserve reviewed Mac fixes.\n\n## 0.1.0 — Build 1\n\nOld notes.\n")
        self.write(".github/workflows/windows.yml", "name: default workflow\n")
        self.base = self.commit("feat: first applications")
        self.git.run("tag", "win-v1.8.0")
        self.git.run("tag", "mac-v0.1.0")
        self.git.run("update-ref", "refs/remotes/origin/main", self.base)

    def write(self, relative, content):
        target = self.root / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8")

    def commit(self, message):
        self.git.run("add", ".")
        self.git.run("commit", "-m", message)
        sha = self.git.resolve("HEAD")
        if self.git.run("branch", "--show-current") == "main":
            self.git.run("update-ref", "refs/remotes/origin/main", sha)
        return sha

    def plan(self, platform="windows", mode="stable", api=None, policy=None, **arguments):
        return build_plan(self.git, api or FakeApi(), policy or {}, platform, mode, **arguments)

    def close(self):
        self.temporary.cleanup()


class IntentTests(unittest.TestCase):
    def test_major_minor_patch_and_zero_major_reset(self):
        cases = [("1.8.9", Bump.PATCH, "1.8.10"), ("1.8.9", Bump.MINOR, "1.9.0"),
                 ("1.8.9", Bump.MAJOR, "2.0.0"), ("0.8.9", Bump.MAJOR, "1.0.0")]
        for old, intent, expected in cases:
            with self.subTest(old=old, intent=intent):
                self.assertEqual(str(CoreVersion.parse(old).bump(intent)), expected)

    def test_stable_tags_use_semantic_order_ignore_betas_and_other_platforms(self):
        tags = ["win-v1.9.0", "win-v1.10.0", "win-v2.0.0-beta.1", "mac-v9.0.0", "v1.3.0"]
        self.assertEqual(highest_stable_tag(tags, "windows"), ("win-v1.10.0", CoreVersion(1, 10, 0)))
        self.assertEqual(highest_stable_tag(tags, "macos"), ("mac-v9.0.0", CoreVersion(9, 0, 0)))

    def test_windows_platform_tag_wins_equal_legacy_tag(self):
        self.assertEqual(highest_stable_tag(["v1.8.0", "win-v1.8.0"], "windows")[0], "win-v1.8.0")

    def test_invalid_core_versions_rejected(self):
        for value in ("1.2", "01.2.3", "1.2.3-beta.1", "1.2.3.4", "-1.2.3"):
            with self.subTest(value=value), self.assertRaises(VersionError):
                CoreVersion.parse(value)

    def test_conventional_and_unknown_code_intent(self):
        cases = [("feat: settings option", Bump.MINOR), ("fix: crash", Bump.PATCH),
                 ("perf: improve scanner", Bump.PATCH), ("docs: clarify settings", Bump.NONE),
                 ("test: exercise scanner", Bump.NONE), ("Repair old registry scans", Bump.PATCH)]
        for message, expected in cases:
            with self.subTest(message=message):
                self.assertEqual(release_intent(message, None, "windows"), expected)

    def test_explicit_declarations_take_highest_intent(self):
        metadata = pull("fix: recovery", labels=["release:patch", "release:minor"],
                        body="Release-Type: major")
        self.assertEqual(release_intent("merge", metadata, "windows"), Bump.MAJOR)

    def test_platform_override_can_reduce_global_feature_without_reducing_breaking(self):
        metadata = pull("feat: cross-platform option", body="Release-Type-Windows: minor\nRelease-Type-Macos: patch")
        self.assertEqual(release_intent("merge", metadata, "windows"), Bump.MINOR)
        self.assertEqual(release_intent("merge", metadata, "macos"), Bump.PATCH)
        metadata["title"] = "feat!: incompatible option"
        self.assertEqual(release_intent("merge", metadata, "macos"), Bump.MAJOR)

    def test_breaking_footer_or_bang_cannot_be_lowered_by_patch(self):
        for title, body in (("feat!: remove API", ""), ("fix: scanner", "BREAKING CHANGE: old API removed")):
            with self.subTest(title=title):
                metadata = pull(title, body=body + "\nRelease-Type: patch", labels=["release:patch"])
                self.assertEqual(release_intent("merge", metadata, "windows"), Bump.MAJOR)

    def test_breaking_platform_scope_does_not_force_other_platform_major(self):
        metadata = pull("feat(macos)!: change Mac API", body="Release-Type-Windows: patch")
        self.assertEqual(release_intent("merge", metadata, "windows"), Bump.PATCH)
        self.assertEqual(release_intent("merge", metadata, "macos"), Bump.MAJOR)

    def test_mac_scope_normalization(self):
        self.assertEqual(conventional_intent("feat(mac): preference", "macos"), Bump.MINOR)
        self.assertEqual(conventional_intent("feat(macos): preference", "windows"), Bump.NONE)

    def test_docs_and_tests_only_paths_never_affect_product(self):
        paths = ["windows/README.md", "windows/tests/Evict.Core.Tests/test.cs", "scripts/tests/test_versioning.py"]
        self.assertFalse(affects_platform(paths, "windows"))
        self.assertFalse(affects_platform(paths, "macos"))

    def test_runtime_text_resources_are_not_mistaken_for_documentation(self):
        self.assertTrue(affects_platform(["windows/src/wordlist.txt"], "windows"))

    def test_real_changes_in_version_files_remain_applicable(self):
        self.assertTrue(affects_platform(["windows/Directory.Build.props"], "windows"))
        self.assertTrue(affects_platform(["macos/Sources/EvictKit/Version.swift"], "macos"))

    def test_bot_exclusion_requires_marker_and_exact_generated_allowlist(self):
        self.assertEqual(generated_release_commit("chore(release): windows 1.9.0", ["windows/Directory.Build.props"], "windows"), CoreVersion(1, 9, 0))
        self.assertIsNone(generated_release_commit("chore(release): windows 1.9.0", ["windows/src/app.cs"], "windows"))
        self.assertIsNone(generated_release_commit("feat: versions", ["windows/Directory.Build.props"], "windows"))


    def test_shared_delivery_recognizes_mac_runtime_without_releasing_mac_docs_or_tests(self):
        self.assertTrue(affects_platform(["macos/Sources/App.swift"], "windows"))
        self.assertTrue(affects_platform(["macos/Scripts/make-app.sh"], "windows"))
        self.assertTrue(affects_platform([".github/workflows/macos.yml"], "windows"))
        self.assertFalse(affects_platform(["macos/README.md", "macos/Tests/EvictKitTests/New.swift"], "windows"))

    def test_shared_delivery_uses_highest_applicable_platform_intent(self):
        metadata = pull("fix: application", body="Release-Type-Windows: none\nRelease-Type-Macos: minor")
        self.assertEqual(bundle_intent("merge", metadata, ["macos/Sources/App.swift"], "windows"), Bump.MINOR)
        metadata["body"] = "Release-Type-Windows: patch\nRelease-Type-Macos: major"
        self.assertEqual(bundle_intent("merge", metadata, ["macos/Sources/App.swift"], "windows"), Bump.MAJOR)
        self.assertEqual(bundle_intent("merge", metadata, ["windows/src/app.cs"], "windows"), Bump.PATCH)
        self.assertEqual(bundle_intent("merge", metadata, ["scripts/build.py"], "windows"), Bump.MAJOR)

    def test_mac_scoped_breaking_change_cannot_be_suppressed_in_shared_delivery(self):
        metadata = pull("feat(macos)!: replace API", body="Release-Type-Windows: none\nRelease-Type-Macos: patch")
        self.assertEqual(bundle_intent("merge", metadata, ["macos/Sources/App.swift"], "windows"), Bump.MAJOR)
        self.assertEqual(release_intent("merge", metadata, "windows"), Bump.NONE)


class RepositoryPlanTests(unittest.TestCase):
    def setUp(self):
        self.fixture = RepositoryFixture()

    def tearDown(self):
        self.fixture.close()

    def test_mixed_unreleased_changes_bump_once_at_highest_intent(self):
        self.fixture.write("windows/src/app.cs", "bug fix\n")
        self.fixture.commit("fix: recovery")
        self.fixture.write("windows/src/new.cs", "new feature\n")
        self.fixture.commit("feat: optional update channel")
        plan = self.fixture.plan()
        self.assertEqual(plan["core_version"], "1.9.0")
        self.assertEqual(plan["bump"], "minor")
        self.assertTrue(plan["publish"])

    def test_docs_only_feature_title_has_no_release(self):
        self.fixture.write("windows/README.md", "new documentation\n")
        self.fixture.commit("feat: documentation guide")
        plan = self.fixture.plan()
        self.assertFalse(plan["publish"])
        self.assertEqual(plan["version"], "1.8.0")
        self.assertEqual(plan["tag_source_sha"], self.fixture.base)

    def test_tests_only_changes_do_not_release(self):
        self.fixture.write("windows/tests/Evict.Core.Tests/new.cs", "test case\n")
        self.fixture.commit("fix: test expectation")
        self.assertFalse(self.fixture.plan()["publish"])

    def test_mac_fix_advances_shared_delivery_and_independent_native_version(self):
        self.fixture.write("macos/Sources/App.swift", "fixed Mac behavior\n")
        self.fixture.commit("fix(macos): history")
        plan = self.fixture.plan()
        self.assertTrue(plan["publish"])
        self.assertEqual(plan["core_version"], "1.8.1")
        self.assertEqual(self.fixture.plan("macos")["core_version"], "0.1.1")


    def test_mac_only_feature_with_windows_none_publishes_shared_minor(self):
        self.fixture.write("macos/Sources/App.swift", "new Mac option\n")
        changed = self.fixture.commit("Merge Mac feature")
        metadata = pull("feat(macos): new preference",
                        body="Release-Type-Windows: none\nRelease-Type-Macos: minor")
        api = FakeApi({changed: [metadata]})
        delivery = self.fixture.plan(api=api)
        self.assertTrue(delivery["publish"])
        self.assertEqual(delivery["version"], "1.9.0")
        self.assertEqual(delivery["bump"], "minor")
        self.assertEqual(self.fixture.plan("macos", api=api)["version"], "0.2.0")

    def test_mac_scoped_breaking_commit_publishes_shared_major(self):
        self.fixture.write("macos/Sources/App.swift", "incompatible Mac API\n")
        self.fixture.commit("feat(macos)!: replace API")
        self.assertEqual(self.fixture.plan()["version"], "2.0.0")

    def test_generated_mac_sync_alone_does_not_create_shared_release(self):
        sync_sources(self.fixture.root, "macos", CoreVersion(0, 1, 1),
                     "mac-v0.1.0", ["reviewed native Mac version"], "2026-10-07")
        generated = self.fixture.commit("chore(release): macos 0.1.1")
        api = FakeApi({generated: [pull("feat!: metadata only")]})
        delivery = self.fixture.plan(api=api)
        self.assertFalse(delivery["publish"])
        self.assertEqual(delivery["version"], "1.8.0")
        self.assertNotIn(generated, api.calls)

    def test_mixed_mac_release_marker_and_runtime_change_is_not_skipped(self):
        self.fixture.write("macos/Sources/App.swift", "fixed application\n")
        self.fixture.commit("chore(release): macos 0.1.1\n\nRelease-Type-Macos: patch")
        self.assertTrue(self.fixture.plan()["publish"])
        self.assertEqual(self.fixture.plan()["version"], "1.8.1")

    def test_legacy_mac_only_intent_contributes_to_shared_delivery(self):
        self.fixture.write("macos/Sources/App.swift", "new native Mac feature\n")
        changed = self.fixture.commit("Merge historical Mac change")
        policy = {"legacy_pull_requests": {"20": {
            "merge_commit_sha": changed, "windows": "none", "macos": "minor"}}}
        self.assertEqual(self.fixture.plan(policy=policy)["version"], "1.9.0")

    def test_mac_only_beta_calculates_the_same_next_core_as_stable(self):
        self.fixture.git.run("checkout", "-b", "mac-preview")
        self.fixture.write("macos/Sources/App.swift", "new Mac option\n")
        changed = self.fixture.commit("neutral application snapshot")
        metadata = pull("feat(macos): preference",
                        body="Release-Type-Windows: none\nRelease-Type-Macos: minor")
        event = {"number": 12, "pull_request": dict(metadata,
                 base={"sha": self.fixture.base}, head={"sha": changed})}
        beta = self.fixture.plan(mode="beta", event=event, beta_sequence="42.1")
        self.assertTrue(beta["publish"])
        self.assertEqual(beta["version"], "1.9.0-beta.42.12.1")
        self.fixture.git.run("checkout", "main")
        self.fixture.git.run("merge", "--ff-only", "mac-preview")
        self.fixture.git.run("update-ref", "refs/remotes/origin/main", changed)
        stable = self.fixture.plan(api=FakeApi({changed: [metadata]}))
        self.assertEqual(stable["core_version"], beta["core_version"])

    def test_tagged_native_mac_version_allows_the_next_native_bump(self):
        self.fixture.write("macos/Sources/App.swift", "first native fix\n")
        self.fixture.commit("fix(macos): first repair")
        first = self.fixture.plan("macos")
        sync_sources(self.fixture.root, "macos", CoreVersion.parse(first["core_version"]),
                     first["base_tag"], first["notes"], "2026-10-07")
        released = self.fixture.commit("chore(release): macos 0.1.1")
        self.fixture.git.run("tag", "mac-v0.1.1", released)
        self.fixture.write("macos/Sources/App.swift", "second native fix\n")
        self.fixture.commit("fix(macos): second repair")
        self.assertEqual(self.fixture.plan("macos")["core_version"], "0.1.2")

    def test_later_rebased_commit_breaking_intent_is_not_lost_for_the_same_pr(self):
        self.fixture.write("windows/src/app.cs", "first fix\n")
        first = self.fixture.commit("fix: first recovery")
        self.fixture.write("windows/src/new.cs", "incompatible behavior\n")
        second = self.fixture.commit("feat!: incompatible behavior")
        metadata = pull("fix: recovery", number=12)
        api = FakeApi({first: [metadata], second: [metadata]})
        plan = self.fixture.plan(api=api)
        self.assertEqual(plan["core_version"], "2.0.0")
        self.assertEqual(plan["bump"], "major")

    def test_later_rebased_feature_commit_overrides_fix_title_for_same_pr(self):
        self.fixture.write("windows/src/app.cs", "first fix\n")
        first = self.fixture.commit("fix: first recovery")
        self.fixture.write("windows/src/new.cs", "new behavior\n")
        second = self.fixture.commit("feat: new behavior")
        metadata = pull("fix: recovery", number=12)
        api = FakeApi({first: [metadata], second: [metadata]})
        self.assertEqual(self.fixture.plan(api=api)["core_version"], "1.9.0")

    def test_metadata_error_is_not_downgraded_to_patch(self):
        self.fixture.write("windows/src/app.cs", "changed\n")
        self.fixture.commit("unknown code change")

        class BrokenApi:
            def merged_pull_requests(self, sha):
                raise VersionError("GitHub HTTP 403")

            def published_stable_releases(self, platform):
                return []

            def retired_stable_reservations(self, platform):
                return []

        with self.assertRaisesRegex(VersionError, "403"):
            self.fixture.plan(api=BrokenApi())

    def test_pending_sync_commit_reuses_version_after_failed_build(self):
        self.fixture.write("windows/src/new.cs", "new feature\n")
        self.fixture.commit("feat: optional update channel")
        first = self.fixture.plan()
        sync_sources(self.fixture.root, "windows", CoreVersion.parse(first["core_version"]),
                     first["base_tag"], first["notes"], today="2026-10-07")
        self.fixture.commit("chore(release): windows 1.9.0")
        second = self.fixture.plan()
        self.assertEqual(second["version"], first["version"])
        self.assertTrue(second["publish"])
        self.assertEqual(sync_sources(self.fixture.root, "windows", CoreVersion(1, 9, 0),
                                     second["base_tag"], second["notes"], today="2026-10-07"), [])

    def test_pending_reserved_version_does_not_drop_when_metadata_is_reduced(self):
        self.fixture.write("windows/src/new.cs", "new feature\n")
        changed = self.fixture.commit("nonconventional feature")
        api = FakeApi({changed: [pull("feat: new feature")]})
        plan = self.fixture.plan(api=api)
        sync_sources(self.fixture.root, "windows", CoreVersion.parse(plan["core_version"]),
                     plan["base_tag"], plan["notes"], today="2026-10-07")
        self.fixture.commit("chore(release): windows 1.9.0")
        lower_metadata = FakeApi({changed: [pull("fix: recovery", labels=["release:patch"])]})
        retry = self.fixture.plan(api=lower_metadata)
        self.assertEqual(retry["core_version"], "1.9.0")
        self.assertEqual(retry["bump"], "minor")

    def test_mac_fix_after_published_delivery_advances_shared_version(self):
        self.fixture.write("windows/src/app.cs", "fixed\n")
        self.fixture.commit("fix: recovery")
        plan = self.fixture.plan()
        sync_sources(self.fixture.root, "windows", CoreVersion(1, 8, 1), plan["base_tag"], plan["notes"], "2026-10-07")
        released = self.fixture.commit("chore(release): windows 1.8.1")
        self.fixture.git.run("tag", "win-v1.8.1")
        self.fixture.write("macos/Sources/App.swift", "fixed Mac behavior\n")
        self.fixture.commit("fix(macos): history")
        retry = self.fixture.plan()
        self.assertTrue(retry["publish"])
        self.assertEqual(retry["version"], "1.8.2")
        self.assertEqual(retry["tag_source_sha"], released)
        self.assertNotEqual(retry["source_sha"], released)

    def test_other_platform_source_sync_is_preserved_during_recompute(self):
        self.fixture.write("windows/src/app.cs", "new Windows behavior\n")
        self.fixture.commit("feat(windows): behavior")
        sync_sources(self.fixture.root, "macos", CoreVersion(0, 1, 1), "mac-v0.1.0", ["fix: Mac"], "2026-10-07")
        self.fixture.commit("chore(release): macos 0.1.1")
        mac_text = (self.fixture.root / "macos/Sources/EvictKit/Version.swift").read_text()
        plan = self.fixture.plan()
        sync_sources(self.fixture.root, "windows", CoreVersion.parse(plan["core_version"]), plan["base_tag"], plan["notes"], "2026-10-07")
        self.assertEqual((self.fixture.root / "macos/Sources/EvictKit/Version.swift").read_text(), mac_text)

    def test_manual_source_ahead_cannot_be_silently_downgraded(self):
        self.fixture.write("windows/Directory.Build.props", "<Project><Version>2.0.0</Version></Project>")
        self.fixture.commit("fix: manual test version")
        with self.assertRaisesRegex(VersionError, "ahead"):
            self.fixture.plan()

    def test_build_property_change_in_version_file_still_bumps(self):
        path = self.fixture.root / "windows/Directory.Build.props"
        path.write_text(path.read_text().replace("<InvariantGlobalization>false", "<InvariantGlobalization>true"))
        self.fixture.commit("fix(build): globalization")
        self.assertEqual(self.fixture.plan()["version"], "1.8.1")

    def test_exact_tag_mode_validates_source_and_never_increments(self):
        plan = self.fixture.plan(mode="tag", tag="win-v1.8.0")
        self.assertEqual(plan["version"], "1.8.0")
        self.assertEqual(plan["source_sha"], self.fixture.base)
        self.assertTrue(plan["publish"])
        self.fixture.write("windows/src/app.cs", "new changed code\n")
        self.fixture.commit("fix: changed")
        with self.assertRaisesRegex(VersionError, "exact source"):
            self.fixture.plan(mode="tag", tag="win-v1.8.0")

    def test_tag_source_core_mismatch_is_rejected(self):
        self.fixture.git.run("tag", "win-v2.0.0")
        with self.assertRaises(VersionError):
            self.fixture.plan(mode="tag", tag="win-v2.0.0")

    def test_semantically_higher_unreachable_tag_is_not_baseline(self):
        self.fixture.git.run("checkout", "-b", "future")
        self.fixture.write("windows/src/app.cs", "future code\n")
        self.fixture.commit("feat!: future code")
        self.fixture.git.run("tag", "win-v9.0.0")
        self.fixture.git.run("checkout", "main")
        self.fixture.write("windows/src/app.cs", "fix\n")
        self.fixture.commit("fix: recovery")
        self.assertEqual(self.fixture.plan()["version"], "1.8.1")

    def test_merge_of_already_tagged_feature_does_not_recount_its_changes(self):
        self.fixture.git.run("checkout", "-b", "tagged-feature")
        self.fixture.write("windows/src/new.cs", "feature\n")
        self.fixture.commit("feat: feature")
        sync_sources(self.fixture.root, "windows", CoreVersion(1, 9, 0), "win-v1.8.0", ["feature"], "2026-10-07")
        self.fixture.commit("chore(release): windows 1.9.0")
        self.fixture.git.run("tag", "win-v1.9.0")
        self.fixture.git.run("checkout", "main")
        self.fixture.git.run("merge", "--no-ff", "tagged-feature", "-m", "Merge feature")
        self.fixture.git.run("update-ref", "refs/remotes/origin/main", self.fixture.git.resolve("HEAD"))
        self.assertFalse(self.fixture.plan()["publish"])

    def test_legacy_pr10_metadata_seeds_correct_platform_intent(self):
        self.fixture.write("windows/src/app.cs", "beta option\n")
        self.fixture.write("macos/Sources/App.swift", "Mac fixes\n")
        merged = self.fixture.commit("Merge historical PR 10")
        policy = {"legacy_pull_requests": {"10": {"merge_commit_sha": merged, "windows": "minor", "macos": "patch"}}}
        self.assertEqual(self.fixture.plan(policy=policy)["version"], "1.9.0")
        self.assertEqual(self.fixture.plan("macos", policy=policy)["version"], "0.1.1")

    def test_beta_uses_same_next_core_as_stable_and_ignores_snapshot_message(self):
        self.fixture.write("windows/src/app.cs", "unreleased fix\n")
        main_head = self.fixture.commit("fix: recovery")
        self.fixture.git.run("update-ref", "refs/remotes/origin/main", main_head)
        self.fixture.git.run("checkout", "-b", "preview")
        self.fixture.write("windows/src/option.cs", "new option\n")
        pr_head = self.fixture.commit("neutral source snapshot")
        event = {"number": 12, "pull_request": {"title": "feat: new setting", "body": "",
                 "base": {"sha": main_head}, "head": {"sha": pr_head}}}
        beta = self.fixture.plan(mode="beta", event=event, beta_sequence="42.1")
        self.assertEqual(beta["core_version"], "1.9.0")
        self.assertEqual(beta["version"], "1.9.0-beta.42.12.1")
        self.assertEqual(beta["bump"], "minor")
        self.assertEqual(beta["source_version"], "1.8.0")
        stable = CoreVersion(1, 8, 0).bump(max(Bump.PATCH, Bump.MINOR))
        self.assertEqual(beta["core_version"], str(stable))

    def test_beta_docs_only_pr_behind_main_does_not_count_reversed_changes(self):
        self.fixture.git.run("checkout", "-b", "docs-preview")
        self.fixture.write("windows/README.md", "documentation\n")
        pr_head = self.fixture.commit("documentation")
        self.fixture.git.run("checkout", "main")
        self.fixture.write("windows/src/main-only.cs", "published main change\n")
        self.fixture.commit("fix: main code")
        plan = self.fixture.plan()
        sync_sources(self.fixture.root, "windows", CoreVersion(1, 8, 1), plan["base_tag"], plan["notes"], "2026-10-07")
        main_head = self.fixture.commit("chore(release): windows 1.8.1")
        self.fixture.git.run("tag", "win-v1.8.1")
        self.fixture.git.run("update-ref", "refs/remotes/origin/main", main_head)
        self.fixture.git.run("checkout", "docs-preview")
        event = {"number": 12, "pull_request": {"title": "documentation", "body": "",
                 "base": {"sha": main_head}, "head": {"sha": pr_head}}}
        beta = self.fixture.plan(mode="beta", event=event, beta_sequence="42.1")
        self.assertFalse(beta["publish"])
        self.assertEqual(beta["core_version"], "1.8.1")
        self.assertEqual(beta["version"], "1.8.1-beta.42.12.1")
        self.assertEqual(beta["tag"], "win-v1.8.1-beta.42.12.1")
        self.assertTrue(beta["prerelease"])

    def test_beta_docs_only_pr_does_not_publish_main_pending_feature(self):
        self.fixture.git.run("checkout", "-b", "docs-preview")
        self.fixture.write("windows/README.md", "updated help\n")
        pr_head = self.fixture.commit("feat: describe new option")
        self.fixture.git.run("checkout", "main")
        self.fixture.write("windows/src/main-only.cs", "unreleased main feature\n")
        main_head = self.fixture.commit("feat: main option")
        self.fixture.git.run("update-ref", "refs/remotes/origin/main", main_head)
        self.fixture.git.run("checkout", "docs-preview")
        event = {"number": 12, "pull_request": {"title": "feat: describe new option", "body": "",
                 "base": {"sha": main_head}, "head": {"sha": pr_head}}}
        beta = self.fixture.plan(mode="beta", event=event, beta_sequence="42.1")
        self.assertFalse(beta["publish"])
        self.assertFalse(beta["release"])
        self.assertEqual(beta["core_version"], "1.9.0")
        self.assertEqual(beta["version"], "1.9.0-beta.42.12.1")
        self.assertEqual(beta["tag"], "win-v1.9.0-beta.42.12.1")
        self.assertEqual(beta["base_tag"], "win-v1.8.0")
        self.assertTrue(beta["prerelease"])

    def test_beta_docs_only_without_pending_changes_keeps_prerelease_identity(self):
        self.fixture.git.run("update-ref", "refs/remotes/origin/main", self.fixture.base)
        self.fixture.git.run("checkout", "-b", "docs-preview")
        self.fixture.write("windows/README.md", "updated help\n")
        pr_head = self.fixture.commit("docs: help")
        event = {"number": 12, "pull_request": {"title": "docs: help", "body": "",
                 "base": {"sha": self.fixture.base}, "head": {"sha": pr_head}}}
        beta = self.fixture.plan(mode="beta", event=event, beta_sequence="42.1")
        self.assertFalse(beta["publish"])
        self.assertEqual(beta["version"], "1.8.0-beta.42.12.1")
        self.assertEqual(beta["tag"], "win-v1.8.0-beta.42.12.1")
        self.assertTrue(beta["prerelease"])

    def test_beta_requires_valid_sequence(self):
        for sequence in ("", "42", "42.0", "42.x", "042.1", "42.01", "42.1.2"):
            with self.subTest(sequence=sequence), self.assertRaises(VersionError):
                self.fixture.plan(mode="beta", event={"number": 12, "pull_request": {"title": "feat: option", "base": {"sha": self.fixture.base}, "head": {"sha": self.fixture.base}}}, beta_sequence=sequence)



class ReleaseTestPlanTests(unittest.TestCase):
    def setUp(self):
        self.fixture = RepositoryFixture()

    def tearDown(self):
        self.fixture.close()

    def request(self, directive="beta", number=12, source=None, title="feat: update validation"):
        self.fixture.git.run("checkout", "-b", "preview")
        if source:
            path = self.fixture.root / "windows/Directory.Build.props"
            path.write_text(path.read_text().replace("1.8.0", source), encoding="utf-8")
        self.fixture.write("windows/src/test-option.cs", "visible update-validation option\n")
        sha = self.fixture.commit("neutral preview source")
        event = {"number": number, "repository": {"full_name": "fixtures/repository"},
                 "sender": {"login": "maintainer", "type": "User"},
                 "pull_request": {"number": number, "title": title,
                                  "body": f"Release-Test-Windows: {directive}" if directive else "",
                                  "user": {"login": "maintainer", "type": "User"},
                                  "base": {"sha": self.fixture.base, "ref": "main", "repo": {"full_name": "fixtures/repository"}},
                                  "head": {"sha": sha, "repo": {"full_name": "fixtures/repository"}}}}
        return event, sha

    def test_pr_release_is_beta_with_an_automatically_calculated_core(self):
        event, sha = self.request()
        plan = self.fixture.plan(mode="beta", event=event, beta_sequence="29.1",
                                 api=FakeApi(releases=[published("1.9.0")]))
        self.assertEqual(plan["version"], "1.10.0-beta.29.12.1")
        self.assertEqual(plan["tag"], "win-v1.10.0-beta.29.12.1")
        self.assertEqual(plan["numeric_version"], "1.10.0.0")
        self.assertEqual(plan["channel"], "beta")
        self.assertTrue(plan["release_test"])
        self.assertTrue(plan["prerelease"])
        self.assertTrue(plan["publish"])
        self.assertEqual(plan["release_source_sha"], sha)
        self.assertEqual(plan["source_version"], "1.8.0")

    def test_stable_directive_can_never_publish_a_stable_pr_release(self):
        event, _ = self.request("stable 1.10.0", source="1.10.0")
        with self.assertRaisesRegex(VersionError, "stable releases are published only from main"):
            self.fixture.plan(mode="beta", event=event, beta_sequence="29.1",
                              api=FakeApi(releases=[published("1.9.0")]))

    def test_legacy_stable_test_provenance_cannot_reenable_stable_pr_delivery(self):
        event, head = self.request("stable 1.10.0", source="1.10.0")
        self.fixture.git.run("tag", "win-v1.10.0")
        api = FakeApi(releases=[published("1.10.0", body=f"Original PR head: {head}\n")])
        with self.assertRaisesRegex(VersionError, "stable releases are published only from main"):
            self.fixture.plan(mode="beta", event=event, beta_sequence="29.2", api=api)

    def test_requested_beta_core_is_an_assertion_not_an_override(self):
        event, _ = self.request("beta 2.0.0")
        with self.assertRaisesRegex(VersionError, "automatically calculated"):
            self.fixture.plan(mode="beta", event=event, beta_sequence="29.1",
                              api=FakeApi(releases=[published("1.9.0")]))

    def test_beta_assertion_does_not_require_a_manually_committed_matching_core(self):
        event, _ = self.request("beta 1.10.0")
        plan = self.fixture.plan(mode="beta", event=event, beta_sequence="29.1",
                                 api=FakeApi(releases=[published("1.9.0")]))
        self.assertEqual(plan["core_version"], "1.10.0")
        self.assertEqual(plan["source_version"], "1.8.0")

    def test_old_manual_pr_core_does_not_override_the_automatic_beta_core(self):
        event, _ = self.request(source="9.99.0")
        plan = self.fixture.plan(mode="beta", event=event, beta_sequence="29.1",
                                 api=FakeApi(releases=[published("1.9.0")]))
        self.assertEqual(plan["core_version"], "1.10.0")
        self.assertEqual(plan["source_version"], "9.99.0")
        self.assertEqual(plan["channel"], "beta")

    def test_cli_beta_sync_updates_metadata_without_manual_version_edits(self):
        event, _ = self.request(source="1.11.0")
        self.fixture.write(".github/release-policy.json", json.dumps({"repository": "fixtures/repository"}))
        event_path = self.fixture.root / "event.json"
        event_path.write_text(json.dumps(event), encoding="utf-8")
        output = StringIO()
        with patch("versioning.GitHub", return_value=FakeApi(releases=[published("1.9.0")])), redirect_stdout(output):
            status = main(["plan", "--platform", "windows", "--mode", "beta",
                           "--repo-root", str(self.fixture.root), "--event-file", str(event_path),
                           "--beta-sequence", "29.1", "--sync"])
        self.assertEqual(status, 0)
        plan = json.loads(output.getvalue())
        self.assertEqual(plan["version"], "1.10.0-beta.29.12.1")
        self.assertEqual(plan["source_version"], "1.10.0")
        self.assertEqual(source_version(self.fixture.root, "windows"), "1.10.0")
        installer = (self.fixture.root / "windows/installer/Evict.iss").read_text()
        self.assertIn('#define MyAppVersion "1.10.0"', installer)
        self.assertIn('#define MyAppNumericVersion "1.10.0.0"', installer)
        self.assertEqual(set(plan["sync_files"]), {
            "windows/Directory.Build.props", "windows/installer/Evict.iss", "windows/CHANGELOG.md"})
        self.assertEqual(source_version(self.fixture.root, "macos"), "0.1.0")

    def test_malformed_or_ambiguous_test_directives_are_rejected(self):
        event, _ = self.request()
        for body in ("Release-Test-Windows: stable 1.10",
                     "Release-Test-Windows: production 1.10.0",
                     "Release-Test-Windows: beta 01.11.0",
                     "Release-Test-Windows: beta\nRelease-Test-Windows: beta 1.10.0"):
            event["pull_request"]["body"] = body
            with self.subTest(body=body), self.assertRaises(VersionError):
                self.fixture.plan(mode="beta", event=event, beta_sequence="29.1")

    def test_foreign_or_missing_repository_identity_is_rejected(self):
        event, _ = self.request()
        for location in ("head", "base", "event"):
            changed = json.loads(json.dumps(event))
            if location == "event":
                changed["repository"]["full_name"] = "foreign/repository"
            else:
                changed["pull_request"][location]["repo"]["full_name"] = "foreign/repository"
            with self.subTest(location=location), self.assertRaisesRegex(VersionError, "same-repository"):
                self.fixture.plan(mode="beta", event=changed, beta_sequence="29.1")

    def test_bot_author_or_sender_is_rejected(self):
        event, _ = self.request()
        for role in ("author", "sender"):
            old_author = dict(event["pull_request"]["user"])
            old_sender = dict(event["sender"])
            identity = event["pull_request"]["user"] if role == "author" else event["sender"]
            identity.update(login="dependabot[bot]", type="Bot")
            with self.subTest(role=role), self.assertRaisesRegex(VersionError, "non-bot"):
                self.fixture.plan(mode="beta", event=event, beta_sequence="29.1")
            event["pull_request"]["user"] = old_author
            event["sender"] = old_sender

    def test_ordinary_pr_remains_beta_after_published_floor(self):
        event, _ = self.request(directive="")
        plan = self.fixture.plan(mode="beta", event=event, beta_sequence="30.1",
                                 api=FakeApi(releases=[published("1.10.0")]))
        self.assertEqual(plan["version"], "1.11.0-beta.30.12.1")
        self.assertFalse(plan["release_test"])
        self.assertEqual(plan["channel"], "beta")

    def test_two_unmerged_minor_prs_share_next_core_but_have_unique_beta_identity(self):
        event, _ = self.request()
        api = FakeApi(releases=[published("1.9.0"), published("1.11.0-beta.13.32.1", prerelease=True)])
        first = self.fixture.plan(mode="beta", event=event, beta_sequence="33.1", api=api)
        event["number"] = event["pull_request"]["number"] = 13
        second = self.fixture.plan(mode="beta", event=event, beta_sequence="34.1", api=api)
        self.assertEqual(first["core_version"], "1.10.0")
        self.assertEqual(second["core_version"], first["core_version"])
        self.assertNotEqual(first["version"], second["version"])
        self.assertTrue(first["prerelease"] and second["prerelease"])

    def test_new_run_first_beta_orders_above_legacy_pr_first_published_beta(self):
        event, _ = self.request(directive="")
        plan = self.fixture.plan(mode="beta", event=event, beta_sequence="40.1",
                                 api=FakeApi(releases=[published("1.10.0")]))
        legacy = "1.11.0-beta.13.32.1"
        numeric_identifiers = lambda version: tuple(int(part) for part in version.split("-beta.", 1)[1].split("."))
        self.assertEqual(plan["version"], "1.11.0-beta.40.12.1")
        self.assertGreater(numeric_identifiers(plan["version"]), numeric_identifiers(legacy))

    def test_newer_build_of_older_pr_orders_above_previous_newer_pr_and_retry(self):
        event, _ = self.request(directive="", number=13)
        api = FakeApi(releases=[published("1.10.0")])
        previous = self.fixture.plan(mode="beta", event=event, beta_sequence="40.1", api=api)
        retry = self.fixture.plan(mode="beta", event=event, beta_sequence="40.2", api=api)
        event["number"] = event["pull_request"]["number"] = 12
        newer = self.fixture.plan(mode="beta", event=event, beta_sequence="41.1", api=api)
        numeric_identifiers = lambda version: tuple(int(part) for part in version.split("-beta.", 1)[1].split("."))
        self.assertGreater(numeric_identifiers(retry["version"]), numeric_identifiers(previous["version"]))
        self.assertGreater(numeric_identifiers(newer["version"]), numeric_identifiers(retry["version"]))
        self.assertEqual(newer["version"], "1.11.0-beta.41.12.1")

    def test_retired_pr_stable_is_excluded_from_the_published_stable_floor(self):
        event, _ = self.request()
        plan = self.fixture.plan(mode="beta", event=event, beta_sequence="33.1",
                                 api=FakeApi(releases=[published("1.9.0"), published("1.10.0", draft=True)]))
        self.assertEqual(plan["core_version"], "1.10.0")
        self.assertEqual(plan["published_floor"], "1.9.0")

    def test_pr_event_cannot_be_planned_as_stable_or_tag(self):
        event, _ = self.request()
        for mode in ("stable", "tag"):
            with self.subTest(mode=mode), self.assertRaisesRegex(VersionError, "only beta"):
                self.fixture.plan(mode=mode, event=event, tag="win-v1.8.0")

    def test_stable_plan_requires_current_main_even_without_pr_metadata(self):
        self.request()
        with self.assertRaisesRegex(VersionError, "exact current main"):
            self.fixture.plan()

    def test_stable_tag_cannot_point_to_an_unmerged_pr_commit(self):
        _, head = self.request(source="1.10.0")
        self.fixture.git.run("tag", "win-v1.10.0", head)
        with self.assertRaisesRegex(VersionError, "source from main"):
            self.fixture.plan(mode="tag", tag="win-v1.10.0")

    def test_caller_cannot_substitute_a_feature_branch_as_main(self):
        self.request()
        with self.assertRaisesRegex(VersionError, "repository main branch"):
            self.fixture.plan(main_ref="HEAD")

    def test_beta_tag_cannot_be_validated_as_a_stable_tag(self):
        self.fixture.git.run("tag", "win-v1.8.0-beta.12.29.1")
        with self.assertRaisesRegex(VersionError, "stable tag"):
            self.fixture.plan(mode="tag", tag="win-v1.8.0-beta.12.29.1")

    def test_main_release_stays_stable_and_never_consumes_a_pr_test_directive(self):
        self.fixture.write("windows/src/change.cs", "main feature\n")
        self.fixture.commit("feat: main change\n\nRelease-Test-Windows: beta 99.0.0")
        plan = self.fixture.plan(event={"ref": "refs/heads/main"})
        self.assertFalse(plan["release_test"])
        self.assertEqual(plan["version"], "1.9.0")
        self.assertEqual(plan["channel"], "stable")
        self.assertFalse(plan["prerelease"])

    def test_cli_tag_sync_cannot_modify_immutable_release_source(self):
        before = (self.fixture.root / "windows/Directory.Build.props").read_text()
        errors = StringIO()
        with patch("versioning.GitHub", return_value=FakeApi()), redirect_stderr(errors):
            status = main(["plan", "--platform", "windows", "--mode", "tag",
                           "--repo-root", str(self.fixture.root), "--tag", "win-v1.8.0", "--sync"])
        self.assertEqual(status, 1)
        self.assertIn("immutable source", errors.getvalue())
        self.assertEqual((self.fixture.root / "windows/Directory.Build.props").read_text(), before)


class RetiredReservationTests(unittest.TestCase):
    def setUp(self):
        self.fixture = RepositoryFixture()
        self.fixture.git.run("checkout", "-b", "historical-pr")
        path = self.fixture.root / "windows/Directory.Build.props"
        path.write_text(path.read_text().replace("1.8.0", "1.10.0"), encoding="utf-8")
        self.fixture.write("windows/src/pr-option.cs", "previously shipped PR application\n")
        self.fixture.write(".github/workflows/windows.yml", "name: reviewed PR workflow\n")
        original = self.fixture.commit("reviewed historical PR application")
        # GitHub's workflow-normalized release source is a sibling, not a descendant, of the PR.
        self.fixture.git.run("checkout", "--detach", self.fixture.base)
        self.fixture.git.run("checkout", original, "--", "windows/Directory.Build.props", "windows/src/pr-option.cs")
        frozen = self.fixture.commit("workflow-normalized historical snapshot of " + original)
        self.fixture.git.run("tag", "win-v1.10.0", frozen)
        self.fixture.git.run("checkout", "main")
        self.descriptor = {"pull_number": 12, "original_pr_sha": original, "source_sha": frozen}
        self.pin = patch.dict(RETIRED_STABLE_RESERVATIONS, {
            "fixtures/repository": {"windows": {"win-v1.10.0": self.descriptor}}})
        self.pin.start()
        self.release = published("1.10.0", draft=True, body="\n".join([
            "Explicit installed-update release test of [PR #12](https://github.com/fixtures/repository/pull/12).",
            "Release channel: stable",
            "Release-test request: true",
            "Original PR head: " + original,
            "Built and tested release source: " + frozen,
            RETIRED_RESERVATION_MARKER + "windows 1.10.0",
        ]))
        self.api = FakeApi(releases=[published("1.9.0"), self.release])

    def tearDown(self):
        self.pin.stop()
        self.fixture.close()

    def preview(self):
        self.fixture.git.run("checkout", "-b", "new-preview")
        self.fixture.write("windows/src/new-option.cs", "new update option\n")
        after = self.fixture.commit("new PR feature source")
        return {"number": 13, "pull_request": {
            "number": 13, "title": "feat: restart update check", "body": "",
            "base": {"sha": self.fixture.base}, "head": {"sha": after}}}

    def test_pinned_retired_release_reserves_next_beta_core_without_becoming_stable(self):
        event = self.preview()
        plan = self.fixture.plan(mode="beta", event=event, beta_sequence="40.1", api=self.api)
        self.assertEqual(plan["core_version"], "1.11.0")
        self.assertEqual(plan["version"], "1.11.0-beta.40.13.1")
        self.assertEqual(plan["published_floor"], "1.9.0")
        self.assertEqual(plan["release_floor"], "1.10.0")
        self.assertEqual(plan["release_floor_tag"], "win-v1.10.0")
        self.assertEqual(plan["retired_reservations"], ["win-v1.10.0"])
        self.assertTrue(plan["prerelease"])
        self.assertEqual(plan["channel"], "beta")

    def test_read_only_hidden_draft_still_reserves_exact_pinned_shipped_core(self):
        event = self.preview()
        plan = self.fixture.plan(mode="beta", event=event, beta_sequence="40.1",
                                 api=FakeApi(releases=[published("1.9.0")]))
        self.assertEqual(plan["core_version"], "1.11.0")
        self.assertEqual(plan["published_floor"], "1.9.0")
        self.assertEqual(plan["release_floor"], "1.10.0")

    def test_main_feature_skips_preserved_historical_tag_and_publishes_stable(self):
        self.fixture.write("windows/src/new-option.cs", "merged feature\n")
        self.fixture.commit("feat: restart update check")
        plan = self.fixture.plan(api=self.api)
        self.assertEqual(plan["version"], "1.11.0")
        self.assertTrue(plan["publish"])
        self.assertFalse(plan["prerelease"])
        self.assertEqual(plan["channel"], "stable")

    def test_main_fix_respects_historical_floor_without_claiming_a_minor_change(self):
        self.fixture.write("windows/src/app.cs", "merged fix\n")
        self.fixture.commit("fix: updater download")
        plan = self.fixture.plan(api=self.api)
        self.assertEqual(plan["version"], "1.10.1")
        self.assertEqual(plan["bump"], "patch")

    def test_pinned_retired_tag_never_publishes_docs_only_changes(self):
        self.fixture.write("windows/README.md", "release documentation\n")
        self.fixture.commit("feat: document updates")
        plan = self.fixture.plan(api=self.api)
        self.assertFalse(plan["publish"])

    def test_higher_unreachable_unpublished_tag_does_not_expand_the_pinned_floor(self):
        self.fixture.git.run("checkout", "historical-pr")
        self.fixture.write("windows/src/future.cs", "unreleased future source\n")
        self.fixture.commit("feat!: future change")
        self.fixture.git.run("tag", "win-v9.0.0")
        self.fixture.git.run("checkout", "main")
        self.fixture.write("windows/src/app.cs", "merged fix\n")
        self.fixture.commit("fix: updater download")
        plan = self.fixture.plan(api=self.api)
        self.assertEqual(plan["version"], "1.10.1")
        self.assertEqual(plan["release_floor"], "1.10.0")

    def test_retired_reservation_marker_cannot_authorize_an_arbitrary_core(self):
        release = published("9.0.0", draft=True, body=RETIRED_RESERVATION_MARKER + "windows 9.0.0")
        with self.assertRaisesRegex(VersionError, "Untrusted"):
            retired_stable_reservation(release, "fixtures/repository", "windows")

    def test_retired_marker_and_provenance_must_be_exact_unique_and_withdrawn(self):
        bodies = [self.release["body"] + "\n" + RETIRED_RESERVATION_MARKER + "windows 1.10.0",
                  self.release["body"].replace("Original PR head: ", "Original PR head: " + "f" * 40),
                  self.release["body"].replace("Release channel: stable", "Release channel: beta"),
                  self.release["body"].replace("/pull/12", "/pull/13"),
                  self.release["body"].replace("Built and tested release source: ", "Built and tested release source: " + "f" * 40),
                  self.release["body"] + "\nOriginal PR head: " + self.descriptor["original_pr_sha"]]
        for body in bodies:
            with self.subTest(body=body), self.assertRaises(VersionError):
                retired_stable_reservation(dict(self.release, body=body), "fixtures/repository", "windows")
        for flags in ({"draft": False}, {"prerelease": True}):
            with self.subTest(flags=flags), self.assertRaises(VersionError):
                retired_stable_reservation(dict(self.release, **flags), "fixtures/repository", "windows")

    def test_retired_pin_does_not_apply_to_other_repositories(self):
        self.assertIsNone(retired_stable_reservation(self.release, "foreign/repository", "macos"))
        with self.assertRaisesRegex(VersionError, "Untrusted"):
            retired_stable_reservation(self.release, "foreign/repository", "windows")

    def test_pinned_tag_mismatch_fails_closed_even_when_draft_metadata_is_hidden(self):
        self.fixture.git.run("tag", "-f", "win-v1.10.0", self.fixture.base)
        with self.assertRaisesRegex(VersionError, "pinned immutable reviewed source"):
            self.fixture.plan(api=FakeApi(releases=[published("1.9.0")]))

    def test_missing_pinned_tag_cannot_fall_back_to_the_lower_published_floor(self):
        self.fixture.git.run("tag", "-d", "win-v1.10.0")
        with self.assertRaisesRegex(VersionError, "not fetched"):
            self.fixture.plan(api=FakeApi(releases=[published("1.9.0")]))

    def test_retired_pin_requires_exact_reviewed_nonworkflow_leaf_tree(self):
        self.descriptor["original_pr_sha"] = self.fixture.base
        # Visible draft metadata must still match the exact original reviewed identity.
        with self.assertRaisesRegex(VersionError, "provenance"):
            self.fixture.plan(api=self.api)
        self.fixture.write("windows/src/unrelated.cs", "different reviewed source\n")
        unrelated = self.fixture.commit("fix: separate main source")
        self.descriptor["original_pr_sha"] = unrelated
        with self.assertRaisesRegex(VersionError, "pinned immutable reviewed source"):
            self.fixture.plan(api=FakeApi(releases=[published("1.9.0")]))

    def test_sibling_normalized_snapshot_with_different_workflows_preserves_review_identity(self):
        original = self.descriptor["original_pr_sha"]
        frozen = self.descriptor["source_sha"]
        self.assertEqual(self.fixture.git.merge_base(original, frozen), self.fixture.base)
        self.assertNotEqual(original, self.fixture.git.merge_base(original, frozen))
        self.assertEqual(self.fixture.git.application_entries(original), self.fixture.git.application_entries(frozen))
        self.assertNotEqual(self.fixture.git.run("show", original + ":.github/workflows/windows.yml"),
                            self.fixture.git.run("show", frozen + ":.github/workflows/windows.yml"))
        self.assertEqual(self.fixture.plan(api=self.api)["release_floor"], "1.10.0")

    def test_pinned_source_with_a_changed_application_leaf_is_rejected(self):
        self.fixture.git.run("checkout", "--detach", self.descriptor["source_sha"])
        self.fixture.write("windows/src/pr-option.cs", "different unreviewed application\n")
        changed = self.fixture.commit("changed frozen application")
        self.fixture.git.run("tag", "-f", "win-v1.10.0", changed)
        self.descriptor["source_sha"] = changed
        self.fixture.git.run("checkout", "main")
        with self.assertRaisesRegex(VersionError, "pinned immutable reviewed source"):
            self.fixture.plan(api=FakeApi(releases=[published("1.9.0")]))

    def test_retired_floor_is_isolated_from_macos_versions(self):
        self.fixture.write("macos/Sources/App.swift", "Mac fix\n")
        self.fixture.commit("fix(macos): startup")
        plan = self.fixture.plan(platform="macos", api=self.api)
        self.assertEqual(plan["version"], "0.1.1")
        self.assertEqual(plan["retired_reservations"], [])

    def test_draft_retirement_metadata_reuses_authenticated_complete_release_pagination(self):
        calls = []

        def opener(request, timeout):
            calls.append(request.full_url)
            return Response([published("1.9.0"), self.release])

        api = GitHub("fixtures/repository", token="test-read-token", opener=opener)
        self.assertEqual(api.published_stable_releases("windows")[0]["tag_name"], "win-v1.9.0")
        self.assertEqual(api.retired_stable_reservations("windows")[0]["tag_name"], "win-v1.10.0")
        self.assertEqual(len(calls), 1)


class PublishedFloorTests(unittest.TestCase):
    def setUp(self):
        self.fixture = RepositoryFixture()

    def tearDown(self):
        self.fixture.close()

    def test_unreachable_published_version_bounds_next_main_patch(self):
        self.fixture.write("windows/src/app.cs", "main fix\n")
        self.fixture.commit("fix: repair")
        plan = self.fixture.plan(api=FakeApi(releases=[published("1.10.0")]))
        self.assertEqual(plan["version"], "1.10.1")
        self.assertEqual(plan["base_version"], "1.8.0")
        self.assertEqual(plan["published_floor_tag"], "win-v1.10.0")

    def test_floor_alone_does_not_create_a_docs_only_release(self):
        self.fixture.write("windows/README.md", "help\n")
        self.fixture.commit("feat: new documentation")
        plan = self.fixture.plan(api=FakeApi(releases=[published("1.10.0")]))
        self.assertFalse(plan["publish"])
        self.assertEqual(plan["version"], "1.8.0")

    def test_floor_pending_source_sync_is_reused_after_failure(self):
        self.fixture.write("windows/src/app.cs", "main feature\n")
        self.fixture.commit("feat: update option")
        api = FakeApi(releases=[published("1.10.0")])
        first = self.fixture.plan(api=api)
        sync_sources(self.fixture.root, "windows", CoreVersion.parse(first["core_version"]),
                     first["base_tag"], first["notes"], "2026-10-07")
        self.fixture.commit("chore(release): windows 1.11.0")
        retry = self.fixture.plan(api=api)
        self.assertEqual(first["version"], "1.11.0")
        self.assertEqual(retry["version"], first["version"])

    def test_suppressed_pending_core_keeps_version_and_tag_coherent(self):
        sync_sources(self.fixture.root, "windows", CoreVersion(1, 10, 0), "win-v1.8.0", ["pending"], "2026-10-07")
        self.fixture.commit("chore(release): windows 1.10.0")
        plan = self.fixture.plan(api=FakeApi(releases=[published("1.10.0")]))
        self.assertFalse(plan["publish"])
        self.assertEqual(plan["version"], "1.10.0")
        self.assertEqual(plan["tag"], "win-v1.10.0")

    def test_published_floor_api_failure_cannot_fall_back_to_reachable_tags(self):
        class BrokenApi(FakeApi):
            def published_stable_releases(self, platform):
                raise VersionError("release API HTTP 403")
        with self.assertRaisesRegex(VersionError, "403"):
            self.fixture.plan(api=BrokenApi())


class SyncTests(unittest.TestCase):
    def setUp(self):
        self.fixture = RepositoryFixture()

    def tearDown(self):
        self.fixture.close()

    def test_windows_sources_and_curated_pending_changelog_are_synced_together(self):
        changed = sync_sources(self.fixture.root, "windows", CoreVersion(1, 9, 0), "win-v1.8.0",
                               ["feat: automatic releases"], "2026-10-07")
        self.assertEqual(set(changed), {"windows/Directory.Build.props", "windows/installer/Evict.iss", "windows/CHANGELOG.md"})
        self.assertEqual(source_version(self.fixture.root, "windows"), "1.9.0")
        installer = (self.fixture.root / "windows/installer/Evict.iss").read_text()
        self.assertIn('#define MyAppVersion "1.9.0"', installer)
        self.assertIn('#define MyAppNumericVersion "1.9.0.0"', installer)
        changelog = (self.fixture.root / "windows/CHANGELOG.md").read_text()
        self.assertIn("## 1.9.0 — 2026-10-07", changelog)
        self.assertIn("Preserve all reviewed cleanup and beta notes.", changelog)
        self.assertNotIn("## 1.8.1 (not published)", changelog)
        self.assertIn("## 1.8.0", changelog)

    def test_mac_sources_build_and_unreleased_notes_are_synced_once(self):
        sync_sources(self.fixture.root, "macos", CoreVersion(0, 2, 0), "mac-v0.1.0", ["feat: release automation"], "2026-10-07")
        text = (self.fixture.root / "macos/Sources/EvictKit/Version.swift").read_text()
        self.assertIn('current = "0.2.0"', text)
        self.assertIn('build = "2"', text)
        changelog = (self.fixture.root / "macos/CHANGELOG.md").read_text()
        self.assertIn("Preserve reviewed Mac fixes.", changelog)
        self.assertEqual(sync_sources(self.fixture.root, "macos", CoreVersion(0, 2, 0), "mac-v0.1.0", ["feat: release automation"], "2026-10-07"), [])

    def test_pending_changelog_upgrade_retains_notes_and_avoids_duplicate_summaries(self):
        text = "# Changelog\n\n## Unreleased\n\n- Curated note.\n"
        minor = update_changelog(text, "1.9.0", "win-v1.8.0", ["feat: new"], "2026-10-07")
        major = update_changelog(minor, "2.0.0", "win-v1.8.0", ["feat!: incompatible"], "2026-10-07")
        self.assertIn("- Curated note.", major)
        self.assertIn("- feat: new", major)
        self.assertIn("## 2.0.0", major)
        self.assertEqual(update_changelog(major, "2.0.0", "win-v1.8.0", ["feat!: incompatible"], "2026-10-07"), major)

    def test_github_outputs_include_boolean_and_json_single_lines(self):
        path = self.fixture.root / "outputs.txt"
        write_outputs({"publish": True, "version": "1.9.0", "sync_files": ["windows/CHANGELOG.md"], "notes": ["ignored"]}, path)
        self.assertEqual(path.read_text(), 'publish=true\nversion=1.9.0\nsync_files=["windows/CHANGELOG.md"]\n')


class Response(BytesIO):
    def __init__(self, value, link=""):
        super().__init__(json.dumps(value).encode())
        self.headers = {"Link": link}


class MetadataTests(unittest.TestCase):
    def test_paginated_metadata_filters_merged_main_and_deduplicates(self):
        sha = "a" * 40
        calls = []
        valid = pull("feat: feature", number=12)
        opened = dict(valid, number=13, merged_at=None)
        other_branch = dict(valid, number=14, base={"ref": "develop", "repo": {"full_name": "fixtures/repository"}})

        def opener(request, timeout):
            calls.append(request.full_url)
            if len(calls) == 1:
                return Response([valid, opened], f'<https://api.github.com/repositories/123/commits/{sha}/pulls?page=2>; rel="next"')
            return Response([valid, other_branch])

        api = GitHub("fixtures/repository", 123, opener=opener)
        self.assertEqual([item["number"] for item in api.merged_pull_requests(sha)], [12])
        self.assertEqual(len(calls), 2)

    def test_metadata_permission_failure_fails_closed_without_token_in_error(self):
        secret = "fixture-token-must-not-appear"

        def opener(request, timeout):
            raise HTTPError(request.full_url, 403, secret, {}, None)

        api = GitHub("fixtures/repository", 123, token=secret, opener=opener)
        with self.assertRaises(VersionError) as caught:
            api.merged_pull_requests("a" * 40)
        self.assertIn("403", str(caught.exception))
        self.assertNotIn(secret, str(caught.exception))

    def test_foreign_pagination_is_not_followed(self):
        calls = []

        def opener(request, timeout):
            calls.append(request.full_url)
            return Response([], '<https://fixtures.invalid/pulls?page=2>; rel="next"')

        with self.assertRaises(VersionError):
            GitHub("fixtures/repository", 123, opener=opener).merged_pull_requests("a" * 40)
        self.assertEqual(len(calls), 1)

    def test_valid_empty_association_allows_direct_commit_classification(self):
        api = GitHub("fixtures/repository", opener=lambda request, timeout: Response([]))
        self.assertEqual(api.merged_pull_requests("a" * 40), [])


    def test_published_floor_paginates_and_filters_platform_prereleases_and_drafts(self):
        calls = []
        def opener(request, timeout):
            calls.append(request.full_url)
            if len(calls) == 1:
                return Response([published("1.9.0"), published("2.0.0-beta.1", prerelease=True)],
                                '<https://api.github.com/repositories/123/releases?page=2>; rel="next"')
            return Response([published("1.10.0"), published("9.0.0", draft=True),
                             published("8.0.0", published_at=None),
                             published("3.0.0", tag_name="mac-v3.0.0")])
        api = GitHub("fixtures/repository", 123, opener=opener)
        records = api.published_stable_releases("windows")
        self.assertEqual([record["tag_name"] for record in records], ["win-v1.9.0", "win-v1.10.0"])
        self.assertEqual(highest_stable_tag([record["tag_name"] for record in records], "windows")[1],
                         CoreVersion(1, 10, 0))
        api.published_stable_releases("windows")
        self.assertEqual(len(calls), 2)

    def test_release_floor_rejects_foreign_numeric_repository_pagination(self):
        calls = []
        def opener(request, timeout):
            calls.append(request.full_url)
            return Response([], '<https://api.github.com/repositories/999/releases?page=2>; rel="next"')
        with self.assertRaises(VersionError):
            GitHub("fixtures/repository", 123, opener=opener).published_stable_releases("windows")
        self.assertEqual(len(calls), 1)

    def test_release_floor_permission_failure_redacts_token(self):
        secret = "fixture-token-must-not-appear"
        def opener(request, timeout):
            raise HTTPError(request.full_url, 403, secret, {}, None)
        with self.assertRaises(VersionError) as caught:
            GitHub("fixtures/repository", 123, token=secret, opener=opener).published_stable_releases("windows")
        self.assertIn("403", str(caught.exception))
        self.assertNotIn(secret, str(caught.exception))


if __name__ == "__main__":
    unittest.main()
