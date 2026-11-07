"""Execute the actual write-token freeze script with API mocks and Python-generated files.

The test compares two independent implementations: the normal version engine produces the
manifest; the workflow must reconstruct exactly those bytes without checking out PR code.
"""
from datetime import datetime, timezone
import importlib.util
import json
import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[3]
SPEC = importlib.util.spec_from_file_location("native_freeze_versioning", ROOT / "scripts/versioning.py")
VERSIONING = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = VERSIONING
SPEC.loader.exec_module(VERSIONING)

SOURCE = "a" * 40
MAIN = "b" * 40
COMMIT = "2" * 40
WORKFLOWS = "f" * 40
FILES = ("macos/Sources/EvictKit/Version.swift", "macos/CHANGELOG.md")

NODE_RUNNER = r"""
const fs = require('fs');
const input = JSON.parse(fs.readFileSync('fixture.json', 'utf8'));
const mutations = [], outputs = {}, reads = [];
let mainReads = 0;
const response = data => ({data});
const mainSHA = input.beta ? input.main : input.source;
const github = {rest: {
  git: {
    getRef: async args => {
      reads.push({method:'getRef', ...args});
      if (args.ref !== 'heads/main') throw new Error('Unexpected ref read: ' + args.ref);
      mainReads += 1;
      const advanced = input.advanceMain === 'first' || (input.advanceMain === 'second' && mainReads > 1);
      return response({object:{sha:advanced ? 'c'.repeat(40) : mainSHA}});
    },
    getCommit: async args => {
      reads.push({method:'getCommit', ...args});
      if (args.commit_sha === input.source) return response({tree:{sha:'d'.repeat(40)}});
      if (args.commit_sha === mainSHA) return response({tree:{sha:'e'.repeat(40)}});
      throw new Error('Unexpected commit read');
    },
    getTree: async args => {
      reads.push({method:'getTree', ...args});
      return response({truncated:Boolean(input.truncatedTree), tree:input.missingWorkflow ? [] : [
        {path:'.github/workflows', type:'tree', sha:'f'.repeat(40)},
        {path:'.github/workflows/windows.yml', type:'blob', sha:'9'.repeat(40)}
      ]});
    },
    createTree: async args => { mutations.push({method:'createTree', ...args}); return response({sha:'1'.repeat(40)}); },
    createCommit: async args => { mutations.push({method:'createCommit', ...args}); return response({sha:'2'.repeat(40)}); },
    createRef: async args => { mutations.push({method:'createRef', ...args}); return response({object:{sha:args.sha}}); },
    updateRef: async args => { mutations.push({method:'updateRef', ...args}); return response({object:{sha:args.sha}}); }
  },
  pulls: {get: async args => {
    reads.push({method:'pulls.get', ...args});
    return response({state:input.closedPR ? 'closed' : 'open', head:{sha:input.advancePR ? 'c'.repeat(40) : input.source}, base:{ref:'main'}});
  }},
  repos: {getContent: async args => {
    reads.push({method:'getContent', ...args});
    if (args.ref !== input.source || !(args.path in input.original)) throw new Error('Unexpected content read');
    return response({content:Buffer.from(input.original[args.path], 'utf8').toString('base64')});
  }}
}};
const context = {repo:{owner:'krishnabhunia', repo:'evict-uninstaller'}, ref:input.beta ? 'refs/pull/41/merge' : 'refs/heads/main', runId:600,
  payload:input.beta ? {pull_request:{number:41, head:{repo:{full_name:'krishnabhunia/evict-uninstaller'}}, user:{type:'User'}}, sender:{type:'User'}} : {}};
const core = {setOutput:(key, value) => {outputs[key] = value;}};
const onlyFileSystem = name => {if (name !== 'fs') throw new Error('Freeze may not execute repository code or load ' + name); return fs;};
const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
const script = new AsyncFunction('github', 'context', 'core', 'require', 'process', fs.readFileSync('freeze.js', 'utf8'));
(async () => {
  try {
    await script(github, context, core, onlyFileSystem, process);
    process.stdout.write(JSON.stringify({ok:true, mutations, outputs, reads}));
  } catch (error) {
    process.stdout.write(JSON.stringify({ok:false, error:error.message, mutations, outputs, reads}));
  }
})();
"""

CLEANUP_RUNNER = r"""
const fs = require('fs');
const input = JSON.parse(fs.readFileSync('cleanup-fixture.json', 'utf8'));
const reads = [], deletes = [], messages = [];
const notFound = () => Object.assign(new Error('Not found'), {status:404});
const github = {rest:{git:{
  getRef:async args => {reads.push(args); if(input.missing) throw notFound(); return {data:{object:{sha:input.sha}}};},
  deleteRef:async args => {deletes.push(args); if(input.missingOnDelete) throw notFound();}
}}};
const context = {repo:{owner:'krishnabhunia',repo:'evict-uninstaller'}, runId:600,payload:{pull_request:{number:41}}};
const core = {info:message => messages.push(message)};
const AsyncFunction = Object.getPrototypeOf(async function(){}).constructor;
const script = new AsyncFunction('github','context','core','process',fs.readFileSync('cleanup.js','utf8'));
(async () => {
  try {await script(github,context,core,process); process.stdout.write(JSON.stringify({ok:true,reads,deletes,messages}));}
  catch(error) {process.stdout.write(JSON.stringify({ok:false,error:error.message,reads,deletes,messages}));}
})();
"""


@unittest.skipUnless(shutil.which("node"), "Node is needed to execute the actual GitHub Actions freeze script")
class NativeFreezeTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.directory = Path(self.temp.name)
        workflow_path = ROOT / ".github/workflows/macos.yml"
        if not workflow_path.exists():
            # On the first PR introducing this workflow, the frozen beta source deliberately
            # restores main's older workflow tree. The plan job already ran these tests on
            # the PR source before that replacement; the build job must not restore PR CI code.
            subject = subprocess.check_output(["git", "log", "-1", "--format=%s"], cwd=ROOT, text=True).strip()
            if subject.startswith("macOS beta "):
                self.skipTest("Frozen beta source excludes the new workflow; the plan job already tested its freeze script")
        workflow = workflow_path.read_text(encoding="utf-8")
        self.workflow = workflow
        self.freeze_job = workflow.split("\n  freeze:\n", 1)[1].split("\n  build:\n", 1)[0]
        marker = "      - name: Validate and freeze native metadata\n"
        step = self.freeze_job.split(marker, 1)[1]
        source = step.split("          script: |\n", 1)[1]
        lines = []
        for line in source.splitlines():
            if line and not line.startswith("            "):
                break
            lines.append(line[12:] if line else "")
        self.freeze_script = "\n".join(lines)
        self.assertIn("core.setOutput('source_sha'", self.freeze_script)

    def fixture(self, *, beta=True, old_version="1.0.1", core="1.1.0", build=9, changelog=None):
        original = {
            FILES[0]: f'import Foundation\n\npublic enum Version {{\n    public static let current = "{old_version}"\n    public static let build = "{build}"\n}}\n',
            FILES[1]: changelog or "# Changelog\n\n## 1.0.1 — 2026-11-05\n\n- Previous implementation details stay intact.\n",
        }
        for path, content in original.items():
            target = self.directory / path
            target.parent.mkdir(parents=True, exist_ok=True)
            target.write_text(content, encoding="utf-8")
        notes = ["Stage verified native macOS updates and save recovery copies.",
                 "Keep the selected release version and its implementation details visible."]
        today = datetime.now(timezone.utc).date().isoformat()
        VERSIONING.sync_sources(self.directory, "macos", VERSIONING.CoreVersion.parse(core), "mac-v1.0.1", notes, today)
        version = core + "-beta007" if beta else core
        manifest = {"plan": {"source_sha": SOURCE, "core_version": core, "version": version,
                             "tag": "mac-v" + version, "channel": "beta" if beta else "stable", "prerelease": beta,
                             "base_tag": "mac-v1.0.1", "notes": notes, "sync_date": today},
                    "files": {path: (self.directory / path).read_text(encoding="utf-8") for path in FILES}}
        data = {"beta": beta, "source": SOURCE, "main": MAIN, "original": original}
        return manifest, data

    def run_freeze(self, manifest, data):
        native_plan = self.directory / "NativePlan"
        native_plan.mkdir(exist_ok=True)
        (native_plan / "manifest.json").write_text(json.dumps(manifest), encoding="utf-8")
        (self.directory / "fixture.json").write_text(json.dumps(data), encoding="utf-8")
        (self.directory / "freeze.js").write_text(self.freeze_script, encoding="utf-8")
        (self.directory / "runner.js").write_text(NODE_RUNNER, encoding="utf-8")
        env = {"PATH": os.environ.get("PATH", ""), "GITHUB_REPOSITORY": "krishnabhunia/evict-uninstaller",
               "GITHUB_RUN_ATTEMPT": "2", "EXPECTED_SOURCE": SOURCE,
               "EXPECTED_VERSION": manifest["plan"]["version"], "EXPECTED_CORE": manifest["plan"]["core_version"]}
        output = subprocess.run([shutil.which("node"), "runner.js"], cwd=self.directory, env=env,
                                capture_output=True, text=True, timeout=20, check=True)
        self.assertEqual(output.stderr, "")
        return json.loads(output.stdout)

    def assert_tree_matches_manifest(self, result, manifest, *, beta):
        self.assertTrue(result["ok"], result.get("error"))
        tree = next(item for item in result["mutations"] if item["method"] == "createTree")
        self.assertEqual(tree["base_tree"], "d" * 40)
        native = {entry["path"]: entry["content"] for entry in tree["tree"] if entry["type"] == "blob"}
        self.assertEqual(native, manifest["files"])
        workflows = [entry for entry in tree["tree"] if entry["path"] == ".github/workflows"]
        self.assertEqual(workflows, [{"path": ".github/workflows", "mode": "040000", "type": "tree", "sha": WORKFLOWS}] if beta else [])
        commit = next(item for item in result["mutations"] if item["method"] == "createCommit")
        self.assertEqual(commit["parents"], [MAIN if beta else SOURCE])
        self.assertEqual(result["outputs"]["source_sha"], COMMIT)

    def test_beta_uses_canonical_generated_files_and_only_reviewed_workflows(self):
        manifest, data = self.fixture(beta=True)
        result = self.run_freeze(manifest, data)
        self.assert_tree_matches_manifest(result, manifest, beta=True)
        self.assertIn('public static let build = "10"', manifest["files"][FILES[0]])
        reference = next(item for item in result["mutations"] if item["method"] == "createRef")
        self.assertEqual(reference["ref"], "refs/heads/beta-build/macos/pr-41/run-600-attempt-2")
        self.assertFalse(any(item["method"] == "updateRef" for item in result["mutations"]))

    def test_stable_freezes_exact_source_without_replacing_workflows(self):
        manifest, data = self.fixture(beta=False, core="10.20.30", build=99)
        result = self.run_freeze(manifest, data)
        self.assert_tree_matches_manifest(result, manifest, beta=False)
        self.assertIn('public static let build = "100"', manifest["files"][FILES[0]])
        reference = next(item for item in result["mutations"] if item["method"] == "updateRef")
        self.assertEqual(reference["ref"], "heads/main")
        self.assertFalse(reference["force"])
        self.assertEqual(reference["sha"], COMMIT)
        self.assertFalse(any(item["method"] == "createRef" for item in result["mutations"]))

    def test_changelog_reuse_and_same_core_build_match_version_engine(self):
        for heading, marker in [("Unreleased", ""), ("1.1.0 — earlier", "<!-- Evict automatic release; base: mac-v1.0.1 -->\n\n"),
                                ("1.1.0 — already reserved", "")]:
            with self.subTest(heading=heading):
                changelog = f"# Changelog\n\n## {heading}\n\n{marker}- Existing implementation details.\n\n## 1.0.0\n\n- Older change.\n"
                manifest, data = self.fixture(old_version="1.1.0", core="1.1.0", build=10, changelog=changelog)
                result = self.run_freeze(manifest, data)
                self.assert_tree_matches_manifest(result, manifest, beta=True)
                self.assertIn('public static let build = "10"', manifest["files"][FILES[0]])

    def test_manifest_cannot_inject_code_or_extra_files(self):
        for mutation in ("swift", "changelog", "other-file"):
            with self.subTest(mutation=mutation):
                manifest, data = self.fixture()
                if mutation == "other-file":
                    manifest["files"]["windows/Directory.Build.props"] = "injected"
                else:
                    manifest["files"][FILES[0] if mutation == "swift" else FILES[1]] += "\ninjected content\n"
                result = self.run_freeze(manifest, data)
                self.assertFalse(result["ok"])
                self.assertFalse(result["mutations"])

    def test_changed_or_closed_pull_request_cannot_create_release_objects(self):
        for field in ("advancePR", "closedPR"):
            manifest, data = self.fixture()
            data[field] = True
            result = self.run_freeze(manifest, data)
            self.assertFalse(result["ok"])
            self.assertIn("PR changed", result["error"])
            self.assertFalse(result["mutations"])

    def test_truncated_or_missing_reviewed_workflow_tree_fails_before_writes(self):
        for field in ("truncatedTree", "missingWorkflow"):
            manifest, data = self.fixture()
            data[field] = True
            result = self.run_freeze(manifest, data)
            self.assertFalse(result["ok"])
            self.assertFalse(result["mutations"])

    def test_main_advance_never_updates_the_main_ref(self):
        for moment in ("first", "second"):
            manifest, data = self.fixture(beta=False)
            data["advanceMain"] = moment
            result = self.run_freeze(manifest, data)
            self.assertFalse(result["ok"])
            self.assertIn("Main advanced", result["error"])
            self.assertFalse(any(item["method"] in ("updateRef", "createRef") for item in result["mutations"]))
            if moment == "first":
                self.assertFalse(result["mutations"])

    def test_write_token_job_does_not_checkout_or_execute_repository_code(self):
        self.assertNotIn("actions/checkout", self.freeze_job)
        self.assertNotRegex(self.freeze_job, r"(?m)^\s+run:")
        self.assertNotIn("child_process", self.freeze_script)
        self.assertNotIn("execSync", self.freeze_script)
        self.assertNotIn("spawnSync", self.freeze_script)

    def run_cleanup(self, ref, *, sha=SOURCE, missing=False, missing_on_delete=False, current_attempt=2):
        job = self.workflow.split("\n  cleanup-source:\n", 1)[1]
        source = job.split("          script: |\n", 1)[1]
        script = "\n".join(line[12:] if line else "" for line in source.splitlines())
        (self.directory / "cleanup.js").write_text(script, encoding="utf-8")
        (self.directory / "cleanup-runner.js").write_text(CLEANUP_RUNNER, encoding="utf-8")
        (self.directory / "cleanup-fixture.json").write_text(json.dumps({"sha":sha, "missing":missing, "missingOnDelete":missing_on_delete}), encoding="utf-8")
        env = {"PATH":os.environ.get("PATH", ""), "TEMP_REF":ref, "SOURCE_SHA":SOURCE, "GITHUB_RUN_ATTEMPT":str(current_attempt)}
        result = subprocess.run([shutil.which("node"), "cleanup-runner.js"], cwd=self.directory, env=env,
                                capture_output=True, text=True, timeout=20, check=True)
        self.assertEqual(result.stderr, "")
        return json.loads(result.stdout)

    def test_plan_artifact_identity_is_captured_for_failed_job_retries(self):
        plan_job = self.workflow.split("\n  plan:\n", 1)[1].split("\n  # API-only", 1)[0]
        build_job = self.workflow.split("\n  build:\n", 1)[1].split("\n  publish:\n", 1)[0]
        self.assertIn("artifact: ${{ steps.artifact.outputs.name }}", plan_job)
        for consumer in (self.freeze_job, build_job):
            self.assertIn("name: ${{ needs.plan.outputs.artifact }}", consumer)
            self.assertNotIn("name: macos-plan-${{ github.run_id }}-${{ github.run_attempt }}", consumer)

    def test_cleanup_accepts_original_attempt_after_failed_job_retry(self):
        ref = "heads/beta-build/macos/pr-41/run-600-attempt-1"
        result = self.run_cleanup(ref, current_attempt=2)
        self.assertTrue(result["ok"], result.get("error"))
        self.assertEqual([entry["ref"] for entry in result["deletes"]], [ref])

    def test_cleanup_is_idempotent_after_exact_identity_validation(self):
        ref = "heads/beta-build/macos/pr-41/run-600-attempt-1"
        result = self.run_cleanup(ref, missing=True)
        self.assertTrue(result["ok"])
        self.assertFalse(result["deletes"])
        self.assertTrue(result["messages"])
        self.assertTrue(self.run_cleanup(ref, missing_on_delete=True)["ok"])

    def test_cleanup_rejects_other_pr_run_future_attempt_or_changed_source(self):
        for ref in ("heads/beta-build/macos/pr-42/run-600-attempt-1", "heads/beta-build/macos/pr-41/run-601-attempt-1",
                    "heads/beta-build/macos/pr-41/run-600-attempt-3", "heads/beta-build/macos/pr-41/run-600-attempt-0", "heads/main"):
            with self.subTest(ref=ref):
                result = self.run_cleanup(ref, missing=True)
                self.assertFalse(result["ok"])
                self.assertFalse(result["reads"])
                self.assertFalse(result["deletes"])
        changed = self.run_cleanup("heads/beta-build/macos/pr-41/run-600-attempt-1", sha="c" * 40)
        self.assertFalse(changed["ok"])
        self.assertFalse(changed["deletes"])

    def test_failed_build_or_publication_retains_frozen_source(self):
        job = self.workflow.split("\n  cleanup-source:\n", 1)[1]
        condition = next(line for line in job.splitlines() if line.strip().startswith("if:"))
        self.assertIn("needs.build.result == 'success'", condition)
        self.assertIn("needs.publish.result == 'success'", condition)


if __name__ == "__main__":
    unittest.main()
