"""Short beta number reservation (BT2): reuse, next number, races, limits and old long tags."""
from __future__ import annotations

import importlib.util
import io
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch
from urllib.error import HTTPError

SPEC = importlib.util.spec_from_file_location(
    "beta_reservation", Path(__file__).resolve().parents[1] / "beta_reservation.py")
reservation = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(reservation)

HEAD = "a" * 40
OTHER = "b" * 40
THIRD = "c" * 40


class Response(io.BytesIO):
    def __init__(self, status, payload, link=""):
        super().__init__(json.dumps(payload).encode() if payload is not None else b"")
        self.status = status
        self.headers = {"Link": link}

    def __enter__(self):
        return self

    def __exit__(self, *args):
        self.close()


class FakeGitHub:
    """An in-memory tag store answering the three calls the reservation makes."""

    def __init__(self, tags=None, annotated=None, races=None, create_status=None):
        self.tags = dict(tags or {})          # tag name -> commit sha
        self.annotated = dict(annotated or {})  # tag name -> annotated tag object sha
        self.races = list(races or [])        # tags another build creates just before our POST
        self.create_status = create_status
        self.requests = []

    def __call__(self, request, timeout=None):
        url = request.full_url
        self.requests.append((request.get_method(), url))
        if request.get_method() == "GET" and "/git/matching-refs/tags/" in url:
            prefix = url.split("/git/matching-refs/tags/", 1)[1].split("?", 1)[0]
            refs = []
            for name, sha in sorted(self.tags.items()):
                if not name.startswith(prefix):
                    continue
                if name in self.annotated:
                    refs.append({"ref": "refs/tags/" + name, "object": {"type": "tag", "sha": self.annotated[name]}})
                else:
                    refs.append({"ref": "refs/tags/" + name, "object": {"type": "commit", "sha": sha}})
            return Response(200, refs)
        if request.get_method() == "GET" and "/git/tags/" in url:
            object_sha = url.rsplit("/", 1)[1]
            name = next(name for name, value in self.annotated.items() if value == object_sha)
            return Response(200, {"object": {"type": "commit", "sha": self.tags[name]}})
        if request.get_method() == "POST" and url.endswith("/git/refs"):
            body = json.loads(request.data)
            name = body["ref"].removeprefix("refs/tags/")
            if self.create_status:
                raise HTTPError(url, self.create_status, "error", {}, None)
            if self.races:
                raced = self.races.pop(0)
                self.tags[raced[0]] = raced[1]
            if name in self.tags:
                raise HTTPError(url, 422, "Reference already exists", {}, None)
            self.tags[name] = body["sha"]
            return Response(201, {"ref": body["ref"], "object": {"type": "commit", "sha": body["sha"]}})
        raise AssertionError("unexpected request " + url)


def reserve(fake, core="4.2.0", sha=HEAD, prefix="win-v"):
    return reservation.reserve(reservation.GitHubRefs("owner/repo", "token", opener=fake), core, prefix, sha)


class ReservationTests(unittest.TestCase):
    def test_first_beta_of_a_core_is_001(self):
        fake = FakeGitHub()
        result = reserve(fake)
        self.assertEqual(result["beta_number"], "001")
        self.assertEqual(result["version"], "4.2.0-beta001")
        self.assertEqual(result["tag"], "win-v4.2.0-beta001")
        self.assertFalse(result["reused"])
        self.assertEqual(fake.tags["win-v4.2.0-beta001"], HEAD)

    def test_next_head_commit_gets_the_next_number_across_prs(self):
        fake = FakeGitHub({"win-v4.2.0-beta001": OTHER, "win-v4.2.0-beta002": THIRD})
        self.assertEqual(reserve(fake)["beta_number"], "003")

    def test_rerun_of_the_same_commit_reuses_its_number(self):
        fake = FakeGitHub({"win-v4.2.0-beta001": OTHER, "win-v4.2.0-beta002": HEAD, "win-v4.2.0-beta003": THIRD})
        result = reserve(fake)
        self.assertEqual(result["beta_number"], "002")
        self.assertTrue(result["reused"])
        self.assertFalse(any(method == "POST" for method, _ in fake.requests))

    def test_annotated_tags_are_peeled_to_their_commit(self):
        fake = FakeGitHub({"win-v4.2.0-beta001": HEAD}, annotated={"win-v4.2.0-beta001": "d" * 40})
        result = reserve(fake)
        self.assertEqual(result["beta_number"], "001")
        self.assertTrue(result["reused"])

    def test_old_long_tags_other_cores_and_unreserved_numbers_are_ignored(self):
        fake = FakeGitHub({
            "win-v4.2.0-beta.18.29.1": OTHER, "win-v4.2.0-beta.40.12.2": THIRD,
            "win-v4.2.0-beta000": OTHER, "win-v4.2.0-beta1": OTHER, "win-v4.2.0-beta0001": OTHER,
            "win-v4.2.0-beta001x": OTHER, "win-v4.2.1-beta007": OTHER, "win-v4.2.0": OTHER,
        })
        self.assertEqual(reserve(fake)["beta_number"], "001")

    def test_gaps_continue_after_the_highest_number(self):
        fake = FakeGitHub({"win-v4.2.0-beta001": OTHER, "win-v4.2.0-beta009": THIRD})
        self.assertEqual(reserve(fake)["tag"], "win-v4.2.0-beta010")

    def test_lost_race_reads_again_and_takes_the_next_number(self):
        fake = FakeGitHub({"win-v4.2.0-beta001": OTHER}, races=[("win-v4.2.0-beta002", THIRD)])
        result = reserve(fake)
        self.assertEqual(result["beta_number"], "003")
        self.assertEqual(fake.tags["win-v4.2.0-beta003"], HEAD)

    def test_race_won_by_another_workflow_of_the_same_commit_is_reused(self):
        fake = FakeGitHub(races=[("win-v4.2.0-beta001", HEAD)])
        result = reserve(fake)
        self.assertEqual(result["beta_number"], "001")
        self.assertTrue(result["reused"])

    def test_gives_up_after_the_attempt_limit(self):
        races = [(f"win-v4.2.0-beta{n:03d}", OTHER) for n in range(1, 30)]
        with self.assertRaisesRegex(reservation.ReservationError, "after 10 attempts"):
            reserve(FakeGitHub(races=races))

    def test_number_1000_asks_for_a_version_bump(self):
        fake = FakeGitHub({"win-v4.2.0-beta999": OTHER})
        with self.assertRaisesRegex(reservation.ReservationError, "Bump the version"):
            reserve(fake)
        self.assertEqual(reservation.choose({998: OTHER}, HEAD), (999, False))

    def test_permission_errors_stop_with_the_status(self):
        with self.assertRaisesRegex(reservation.ReservationError, "HTTP 403"):
            reserve(FakeGitHub(create_status=403))

    def test_invalid_inputs_are_rejected_before_any_request(self):
        for core, sha, prefix in (("4.2", HEAD, "win-v"), ("04.2.0", HEAD, "win-v"), ("4.2.0", "abc", "win-v"),
                                  ("4.2.0", HEAD.upper(), "win-v"), ("4.2.0", HEAD, "../"), ("4.2.0", HEAD, "")):
            fake = FakeGitHub()
            with self.subTest(core=core, sha=sha, prefix=prefix), self.assertRaises(reservation.ReservationError):
                reserve(fake, core=core, sha=sha, prefix=prefix)
            self.assertEqual(fake.requests, [])

    def test_untrusted_pagination_address_is_refused(self):
        def opener(request, timeout=None):
            return Response(200, [], link='<https://evil.example/repos/owner/repo/git/x>; rel="next"')
        refs = reservation.GitHubRefs("owner/repo", None, opener=opener)
        with self.assertRaisesRegex(reservation.ReservationError, "unexpected GitHub API address"):
            refs.matching_tags("win-v4.2.0-beta")

    def test_short_tag_format_and_parsing(self):
        self.assertEqual(reservation.short_tag("v", "15.0.0", 7), "v15.0.0-beta007")
        self.assertEqual(reservation.short_number("v15.0.0-beta007", "v", "15.0.0"), 7)
        self.assertIsNone(reservation.short_number("v15.0.0-beta.7", "v", "15.0.0"))
        self.assertIsNone(reservation.short_number("win-v15.0.0-beta007", "v", "15.0.0"))

    def test_cli_writes_github_outputs(self):
        fake = FakeGitHub({"win-v4.2.0-beta001": OTHER})
        with tempfile.TemporaryDirectory() as folder, \
                patch.object(reservation, "urlopen", fake), \
                patch.object(reservation.GitHubRefs.__init__, "__defaults__", (fake, "https://api.github.com")), \
                patch.dict("os.environ", {"GH_TOKEN": "t", "GITHUB_API_URL": "https://api.github.com"}), \
                patch("sys.stdout", new_callable=io.StringIO):
            output = Path(folder) / "out.txt"
            status = reservation.main(["--repository", "owner/repo", "--core", "4.2.0", "--prefix", "win-v",
                                       "--sha", HEAD, "--github-output", str(output)])
            self.assertEqual(status, 0)
            values = dict(line.split("=", 1) for line in output.read_text().splitlines())
        self.assertEqual(values["beta_number"], "002")
        self.assertEqual(values["version"], "4.2.0-beta002")
        self.assertEqual(values["tag"], "win-v4.2.0-beta002")
        self.assertEqual(values["reused"], "false")


if __name__ == "__main__":
    unittest.main()
