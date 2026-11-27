"""Tests for how the workflows deploy the app. Run from the repository root:

    python3 -m unittest discover -s tools/ReleaseCheck -v

The deploy lives in one place, .github/workflows/deploy.yml, and two workflows call it: release.yml once a version's
GitHub release is published (when the tag is the newest version tag), and deploy-manual.yml, which is started by
hand for an urgent fix. A merge into master does not deploy (#707), so the server's build names the release (#144).
The unit tests read the files as text (the runner has no YAML library) and check that wiring. The version order
itself is tested in test_newest_tag.py.
"""

from __future__ import annotations

import os
import re
import stat
import subprocess
import tempfile
import textwrap
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
WORKFLOWS = ROOT / ".github" / "workflows"


def read(name: str) -> str:
    return (WORKFLOWS / name).read_text(encoding="utf-8")


def jobs(workflow: str) -> dict[str, str]:
    """Each job under 'jobs:' with its text, keyed by its id (the lines indented by two spaces)."""
    body = workflow.split("\njobs:\n", 1)[1]
    found: dict[str, str] = {}
    current = None
    for line in body.splitlines():
        match = re.match(r"^  ([A-Za-z0-9_-]+):\s*$", line)
        if match:
            current = match.group(1)
            found[current] = ""
        elif current is not None and (line.startswith("    ") or not line.strip()):
            found[current] += line + "\n"
        elif line.strip() and not line.startswith(" "):
            current = None
    return found


def triggers(workflow: str) -> str:
    """The text of the 'on:' block."""
    match = re.search(r"^on:\n(.*?)(?=^\S)", workflow, re.S | re.M)
    return match.group(1) if match else ""


def run_block(job: str) -> str:
    """The script of a job's first 'run: |' step, without its indentation."""
    match = re.search(r"run: \|\n((?:          .*\n|\s*\n)+)", job)
    if not match:
        raise AssertionError("the job has no 'run: |' step")
    return textwrap.dedent(match.group(1))


class JobsParserTests(unittest.TestCase):
    def test_splits_jobs_by_id(self):
        text = "name: X\n\njobs:\n  one:\n    runs-on: a\n\n  two:\n    uses: ./b.yml\n"
        found = jobs(text)
        self.assertEqual(["one", "two"], list(found))
        self.assertIn("runs-on: a", found["one"])
        self.assertIn("uses: ./b.yml", found["two"])

    def test_reads_the_on_block(self):
        self.assertIn("workflow_call", triggers("name: X\n\non:\n  workflow_call:\n\njobs:\n"))


class DeployWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.deploy = read("deploy.yml")

    def test_is_only_called_never_run_on_its_own(self):
        on = triggers(self.deploy)
        self.assertIn("workflow_call:", on)
        for event in ("push:", "pull_request:", "schedule:", "workflow_dispatch:"):
            self.assertNotIn(event, on)

    def test_needs_both_ftp_secrets(self):
        on = triggers(self.deploy)
        self.assertIn("SIMPLY_FTP_USER:", on)
        self.assertIn("SIMPLY_FTP_PASSWORD:", on)

    def test_never_runs_two_deploys_at_once(self):
        self.assertIn("group: deploy-simply", self.deploy)
        self.assertIn("cancel-in-progress: false", self.deploy)

    def test_checks_out_the_whole_history_so_minver_finds_the_tag(self):
        self.assertIn("fetch-depth: 0", self.deploy)

    def test_keeps_the_servers_own_files(self):
        self.assertIn("rm -f ./publish/web.config", self.deploy)
        for kept in ("web.config", "App_Data/", "WorkspaceStore/"):
            self.assertIn(f"--exclude-glob {kept}", self.deploy)

    def test_is_the_only_workflow_that_uploads(self):
        for path in WORKFLOWS.glob("*.yml"):
            if path.name != "deploy.yml":
                self.assertNotIn("lftp", path.read_text(encoding="utf-8"), path.name)


class CallersTests(unittest.TestCase):
    def test_ci_does_not_deploy(self):
        ci = read("ci.yml")
        self.assertNotIn("deploy.yml", ci)
        self.assertNotIn("DEPLOY_ENABLED", ci)
        self.assertNotIn("deploy", [name.lower() for name in jobs(ci)])
        self.assertIn("build-and-test", jobs(ci))

    def test_release_deploys_when_the_tag_is_newest(self):
        job = jobs(read("release.yml"))["deploy"]
        self.assertIn("uses: ./.github/workflows/deploy.yml", job)
        self.assertIn("needs: is-newest", job)
        self.assertIn("needs.is-newest.outputs.deploy == 'true'", job)
        self.assertIn("vars.DEPLOY_ENABLED == 'true'", job)

    def test_callers_pass_only_the_two_secrets(self):
        for name in ("release.yml",):
            job = jobs(read(name))["deploy"]
            self.assertNotIn("secrets: inherit", job, name)
            self.assertIn("SIMPLY_FTP_USER: ${{ secrets.SIMPLY_FTP_USER }}", job, name)
            self.assertIn("SIMPLY_FTP_PASSWORD: ${{ secrets.SIMPLY_FTP_PASSWORD }}", job, name)


class ReleaseBodyWiringTests(unittest.TestCase):
    """The release body comes from release_body.py, with GitHub's list fed in, not appended by gh itself."""

    def setUp(self):
        self.job = jobs(read("release.yml"))["github-release"]

    def test_fetches_githubs_list_since_the_version_before(self):
        self.assertIn("git describe --tags --abbrev=0 --match 'v[0-9]*' \"${TAG}^\"", self.job)
        self.assertIn('releases/generate-notes', self.job)
        self.assertIn('previous_tag_name="$prev"', self.job)

    def test_composes_the_body_and_publishes_only_that(self):
        self.assertIn("--body-out release-body.md", self.job)
        self.assertIn("--generated-notes generated-notes.md --previous-tag \"$PREVIOUS\"", self.job)
        self.assertIn("--notes-file release-body.md", self.job)
        self.assertNotIn("--generate-notes", self.job)


class IsNewestWiringTests(unittest.TestCase):
    def test_release_checks_the_tag_with_the_helper_on_the_whole_history(self):
        job = jobs(read("release.yml"))["is-newest"]
        self.assertIn("needs: github-release", job)
        self.assertIn("fetch-depth: 0", job)
        self.assertIn('python3 tools/ReleaseCheck/newest_tag.py "$TAG"', job)
        self.assertNotIn("commits/master", read("release.yml"))

    def test_the_comparison_is_not_inline_bash(self):
        self.assertNotIn("is-master-head", read("release.yml"))


if __name__ == "__main__":
    unittest.main()
