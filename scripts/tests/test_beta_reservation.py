"""Short beta number reservation (BT2): reuse, next number, races, limits and old long tags."""
from __future__ import annotations

import importlib.util
import io
import json
import re
import shutil
import subprocess
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


WORKFLOW = Path(__file__).resolve().parents[2] / ".github" / "workflows" / "windows.yml"

# Runs the inline reservation step of the reserve-beta job (trusted workflow code, no checkout) with
# a fake GitHub, so the write-token implementation and the Python one are tested on the same cases.
NODE_HARNESS = """
const state = JSON.parse(process.env.RESERVATION_FAKE_STATE);
const outputs = {};
function httpError(status) { const e = new Error('HTTP ' + status); e.status = status; return e; }
const git = {
  async listMatchingRefs({ref}) {
    const prefix = ref.replace(/^tags\\//, '');
    const data = Object.keys(state.tags).sort().filter(n => n.startsWith(prefix)).map(n => state.annotated[n]
      ? {ref: 'refs/tags/' + n, object: {type: 'tag', sha: state.annotated[n]}}
      : {ref: 'refs/tags/' + n, object: {type: 'commit', sha: state.tags[n]}});
    return {data};
  },
  async getTag({tag_sha}) {
    const name = Object.keys(state.annotated).find(n => state.annotated[n] === tag_sha);
    return {data: {object: {type: 'commit', sha: state.tags[name]}}};
  },
  async createRef({ref, sha}) {
    const name = ref.replace(/^refs\\/tags\\//, '');
    if (state.createStatus) throw httpError(state.createStatus);
    if (state.races.length) { const [raced, target] = state.races.shift(); state.tags[raced] = target; }
    if (name in state.tags) throw httpError(422);
    state.tags[name] = sha;
    return {data: {}};
  },
};
const github = {rest: {git}, paginate: async (fn, params) => (await fn(params)).data};
const summary = {addHeading() { return summary; }, addRaw() { return summary; }, async write() {}};
const core = {setOutput: (k, v) => { outputs[k] = v; }, notice() {}, summary};
const context = {repo: {owner: 'owner', repo: 'repo'}};
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
new AsyncFunction('github', 'context', 'core', 'process', state.script)(github, context, core, process)
  .then(() => console.log(JSON.stringify({outputs, tags: state.tags})))
  .catch(e => console.log(JSON.stringify({error: e.message})));
"""


def inline_reservation_script():
    text = WORKFLOW.read_text(encoding="utf-8")
    job = text[text.index("\n  reserve-beta:"):text.index("\n  freeze-beta:")]
    steps = job.split("\n    steps:\n", 1)[1]
    assert "actions/checkout" not in steps and not re.search(r"^\s+run:", steps, re.M), \
        "reserve-beta must not check out or run pull-request code"
    lines = []
    for line in job.split("script: |\n", 1)[1].splitlines():
        if line.strip() and not line.startswith(" " * 12):
            break
        lines.append(line[12:])
    return "\n".join(lines)


@unittest.skipUnless(shutil.which("node"), "node is needed to run the workflow's inline script")
class InlineReservationTests(unittest.TestCase):
    """The reserve-beta job's inline script gives the same answer as scripts/beta_reservation.py."""

    def run_inline(self, fake, core="4.2.0", sha=HEAD):
        state = {"tags": dict(fake.tags), "annotated": dict(fake.annotated), "races": [list(r) for r in fake.races],
                 "createStatus": fake.create_status, "script": inline_reservation_script()}
        env = {"CORE_VERSION": core, "PREPARED_SOURCE": sha, "RESERVATION_FAKE_STATE": json.dumps(state)}
        result = subprocess.run([shutil.which("node"), "-e", NODE_HARNESS],
                                capture_output=True, text=True, env=env, timeout=60, check=True)
        return json.loads(result.stdout.strip().splitlines()[-1])

    def assert_same(self, make_fake, core="4.2.0", sha=HEAD):
        inline = self.run_inline(make_fake(), core, sha)
        try:
            expected = reserve(make_fake(), core=core, sha=sha)
        except reservation.ReservationError as error:
            self.assertIn("error", inline)
            return error, inline
        self.assertNotIn("error", inline, inline)
        self.assertEqual({key: str(value).lower() if isinstance(value, bool) else str(value)
                          for key, value in expected.items()}, inline["outputs"])
        return expected, inline

    def test_same_results_as_the_python_implementation(self):
        cases = {
            "first": lambda: FakeGitHub(),
            "next across PRs": lambda: FakeGitHub({"win-v4.2.0-beta001": OTHER, "win-v4.2.0-beta002": THIRD}),
            "re-run reuses": lambda: FakeGitHub({"win-v4.2.0-beta001": OTHER, "win-v4.2.0-beta002": HEAD}),
            "annotated": lambda: FakeGitHub({"win-v4.2.0-beta001": HEAD}, annotated={"win-v4.2.0-beta001": "d" * 40}),
            "old long and other tags ignored": lambda: FakeGitHub({
                "win-v4.2.0-beta.18.29.1": OTHER, "win-v4.2.0-beta000": OTHER, "win-v4.2.0-beta1": OTHER,
                "win-v4.2.0-beta0001": OTHER, "win-v4.2.1-beta007": OTHER, "win-v4.2.0": OTHER}),
            "gap": lambda: FakeGitHub({"win-v4.2.0-beta001": OTHER, "win-v4.2.0-beta009": THIRD}),
            "lost race": lambda: FakeGitHub({"win-v4.2.0-beta001": OTHER}, races=[("win-v4.2.0-beta002", THIRD)]),
            "race won by same commit": lambda: FakeGitHub(races=[("win-v4.2.0-beta001", HEAD)]),
        }
        for name, make_fake in cases.items():
            with self.subTest(name):
                self.assert_same(make_fake)

    def test_same_failures(self):
        self.assertIn("Bump the version", self.run_inline(FakeGitHub({"win-v4.2.0-beta999": OTHER}))["error"])
        races = [(f"win-v4.2.0-beta{n:03d}", OTHER) for n in range(1, 30)]
        self.assertIn("after 10 attempts", self.run_inline(FakeGitHub(races=races))["error"])
        self.assertIn("HTTP 403", self.run_inline(FakeGitHub(create_status=403))["error"])
        for core, sha in (("4.2", HEAD), ("04.2.0", HEAD), ("4.2.0", "abc"), ("4.2.0", HEAD.upper())):
            with self.subTest(core=core, sha=sha):
                self.assertIn("error", self.run_inline(FakeGitHub(), core, sha))

    def test_dry_run_previews_without_creating_a_tag(self):
        fake = FakeGitHub({"win-v4.2.0-beta001": OTHER})
        result = reservation.reserve(reservation.GitHubRefs("owner/repo", "token", opener=fake), "4.2.0", "win-v", HEAD,
                                     dry_run=True)
        self.assertEqual(result["beta_number"], "002")
        self.assertNotIn("win-v4.2.0-beta002", fake.tags)
        self.assertFalse(any(method == "POST" for method, _ in fake.requests))


if __name__ == "__main__":
    unittest.main()
