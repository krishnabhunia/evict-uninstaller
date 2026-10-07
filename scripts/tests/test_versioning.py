"""Offline release-policy and temporary-repository regression tests."""
from __future__ import annotations

from io import BytesIO
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from urllib.error import HTTPError

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from versioning import (Bump, CoreVersion, Git, GitHub, VersionError, affects_platform,
                        build_plan, conventional_intent, generated_release_commit,
                        highest_stable_tag, release_intent, source_version,
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
        return self.releases


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
        return self.git.resolve("HEAD")

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

    def test_platform_tracks_are_independent(self):
        self.fixture.write("macos/Sources/App.swift", "fixed Mac behavior\n")
        self.fixture.commit("fix(macos): history")
        self.assertFalse(self.fixture.plan()["publish"])
        self.assertEqual(self.fixture.plan("macos")["core_version"], "0.1.1")

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

    def test_fully_tagged_release_then_other_platform_commit_has_no_new_version(self):
        self.fixture.write("windows/src/app.cs", "fixed\n")
        self.fixture.commit("fix: recovery")
        plan = self.fixture.plan()
        sync_sources(self.fixture.root, "windows", CoreVersion(1, 8, 1), plan["base_tag"], plan["notes"], "2026-10-07")
        released = self.fixture.commit("chore(release): windows 1.8.1")
        self.fixture.git.run("tag", "win-v1.8.1")
        self.fixture.write("macos/Sources/App.swift", "fixed Mac behavior\n")
        self.fixture.commit("fix(macos): history")
        retry = self.fixture.plan()
        self.assertFalse(retry["publish"])
        self.assertEqual(retry["version"], "1.8.1")
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
        self.assertEqual(beta["version"], "1.9.0-beta.12.42.1")
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
        self.assertEqual(beta["version"], "1.8.1-beta.12.42.1")
        self.assertEqual(beta["tag"], "win-v1.8.1-beta.12.42.1")
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
        self.assertEqual(beta["version"], "1.9.0-beta.12.42.1")
        self.assertEqual(beta["tag"], "win-v1.9.0-beta.12.42.1")
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
        self.assertEqual(beta["version"], "1.8.0-beta.12.42.1")
        self.assertEqual(beta["tag"], "win-v1.8.0-beta.12.42.1")
        self.assertTrue(beta["prerelease"])

    def test_beta_requires_valid_sequence(self):
        for sequence in ("", "42.x", "042.1", "42.01"):
            with self.subTest(sequence=sequence), self.assertRaises(VersionError):
                self.fixture.plan(mode="beta", event={"number": 12, "pull_request": {"title": "feat: option", "base": {"sha": self.fixture.base}, "head": {"sha": self.fixture.base}}}, beta_sequence=sequence)



class ReleaseTestPlanTests(unittest.TestCase):
    def setUp(self):
        self.fixture = RepositoryFixture()

    def tearDown(self):
        self.fixture.close()

    def request(self, channel="stable", core="1.10.0", number=12, source=None, title="feat: update validation"):
        self.fixture.git.run("checkout", "-b", "preview")
        path = self.fixture.root / "windows/Directory.Build.props"
        path.write_text(path.read_text().replace("1.8.0", source or core), encoding="utf-8")
        self.fixture.write("windows/src/test-option.cs", "visible update-validation option\n")
        sha = self.fixture.commit("neutral preview source")
        event = {"number": number, "repository": {"full_name": "fixtures/repository"},
                 "sender": {"login": "maintainer", "type": "User"},
                 "pull_request": {"number": number, "title": title,
                                  "body": f"Release-Test-Windows: {channel} {core}",
                                  "user": {"login": "maintainer", "type": "User"},
                                  "base": {"sha": self.fixture.base, "ref": "main", "repo": {"full_name": "fixtures/repository"}},
                                  "head": {"sha": sha, "repo": {"full_name": "fixtures/repository"}}}}
        return event, sha

    def test_explicit_stable_test_uses_calculated_core_without_beta_suffix(self):
        event, sha = self.request()
        plan = self.fixture.plan(mode="beta", event=event, beta_sequence="29.1",
                                 api=FakeApi(releases=[published("1.9.0")]))
        self.assertEqual(plan["version"], "1.10.0")
        self.assertEqual(plan["tag"], "win-v1.10.0")
        self.assertEqual(plan["numeric_version"], "1.10.0.0")
        self.assertEqual(plan["channel"], "stable")
        self.assertTrue(plan["release_test"])
        self.assertFalse(plan["prerelease"])
        self.assertTrue(plan["publish"])
        self.assertEqual(plan["release_source_sha"], sha)
        self.assertEqual(plan["source_version"], "1.10.0")

    def test_explicit_beta_after_published_test_stable_uses_next_minor_core(self):
        event, _ = self.request("beta", "1.11.0", number=13)
        plan = self.fixture.plan(mode="beta", event=event, beta_sequence="30.2",
                                 api=FakeApi(releases=[published("1.10.0")]))
        self.assertEqual(plan["version"], "1.11.0-beta.13.30.2")
        self.assertEqual(plan["tag"], "win-v1.11.0-beta.13.30.2")
        self.assertEqual(plan["core_version"], "1.11.0")
        self.assertEqual(plan["channel"], "beta")
        self.assertTrue(plan["release_test"])
        self.assertTrue(plan["prerelease"])
        self.assertTrue(plan["publish"])
        self.assertEqual(plan["published_floor"], "1.10.0")
        self.assertEqual(plan["base_tag"], "win-v1.8.0")

    def test_requested_core_is_an_assertion_not_an_override(self):
        event, _ = self.request(core="2.0.0")
        with self.assertRaisesRegex(VersionError, "automatically calculated"):
            self.fixture.plan(mode="beta", event=event, beta_sequence="29.1",
                              api=FakeApi(releases=[published("1.9.0")]))

    def test_committed_test_core_must_match_the_calculated_request(self):
        event, _ = self.request(source="1.8.0")
        with self.assertRaisesRegex(VersionError, "source and requested core"):
            self.fixture.plan(mode="beta", event=event, beta_sequence="29.1",
                              api=FakeApi(releases=[published("1.9.0")]))

    def test_malformed_or_ambiguous_test_directives_are_rejected(self):
        event, _ = self.request()
        for body in ("Release-Test-Windows: stable 1.10",
                     "Release-Test-Windows: production 1.10.0",
                     "Release-Test-Windows: beta 01.11.0",
                     "Release-Test-Windows: stable 1.10.0\nRelease-Test-Windows: beta 1.11.0"):
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

    def test_test_directive_does_not_activate_on_non_pr_stable_plan(self):
        self.fixture.write("windows/src/change.cs", "main feature\n")
        self.fixture.commit("feat: main change")
        event = {"pull_request": {"body": "Release-Test-Windows: stable 2.0.0"}}
        plan = self.fixture.plan(event=event)
        self.assertFalse(plan["release_test"])
        self.assertEqual(plan["version"], "1.9.0")
        self.assertEqual(plan["channel"], "stable")

    def test_ordinary_pr_remains_beta_after_published_floor(self):
        event, _ = self.request(core="1.11.0")
        event["pull_request"]["body"] = ""
        plan = self.fixture.plan(mode="beta", event=event, beta_sequence="30.1",
                                 api=FakeApi(releases=[published("1.10.0")]))
        self.assertEqual(plan["version"], "1.11.0-beta.12.30.1")
        self.assertFalse(plan["release_test"])
        self.assertEqual(plan["channel"], "beta")

    def test_same_stable_test_rerun_reuses_published_version_and_immutable_source(self):
        event, head = self.request()
        self.fixture.git.run("tag", "win-v1.10.0")
        # A later normalized snapshot can have a new commit SHA with exactly the same tree.
        self.fixture.git.run("commit", "--allow-empty", "-m", "fresh normalized snapshot")
        snapshot = self.fixture.git.resolve("HEAD")
        api = FakeApi(releases=[published("1.10.0", body=f"Original PR head: {head}\n")])
        plan = self.fixture.plan(mode="beta", event=event, beta_sequence="29.2", api=api)
        self.assertEqual(plan["version"], "1.10.0")
        self.assertFalse(plan["publish"])
        self.assertFalse(plan["release"])
        self.assertTrue(plan["release_test"])
        self.assertEqual(plan["release_source_sha"], head)
        self.assertEqual(plan["source_sha"], snapshot)

    def test_published_test_version_with_foreign_or_missing_provenance_is_rejected(self):
        event, _ = self.request()
        for body in ("", "Original PR head: " + "f" * 40, "Original PR head: " + event["pull_request"]["head"]["sha"] + "extra"):
            with self.subTest(body=body), self.assertRaisesRegex(VersionError, "provenance"):
                self.fixture.plan(mode="beta", event=event, beta_sequence="29.2",
                                  api=FakeApi(releases=[published("1.10.0", body=body)]))

    def test_published_test_tag_source_version_must_match(self):
        event, head = self.request()
        self.fixture.git.run("tag", "win-v1.10.0", self.fixture.base)
        with self.assertRaisesRegex(VersionError, "immutable source"):
            self.fixture.plan(mode="beta", event=event, beta_sequence="29.2",
                              api=FakeApi(releases=[published("1.10.0", body=f"Original PR head: {head}")]))


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
