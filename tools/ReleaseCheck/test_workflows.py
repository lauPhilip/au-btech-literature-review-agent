"""Tests for how the workflows deploy the app. Run from the repository root:

    python3 -m unittest discover -s tools/ReleaseCheck -v

The deploy lives in one place, .github/workflows/deploy.yml, and two workflows call it: ci.yml on every push to
master, and release.yml once a version's GitHub release is published, so the server's build names the release and
not a pre-release (#144). The unit tests read the three files as text (the runner has no YAML library) and check
that wiring. The feature tests run release.yml's "Is the tag master's newest commit?" step with a stand-in for the
gh command, and check that a tag on master's newest commit is deployed and a tag on an older commit is not.
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
    def test_ci_deploys_after_the_checks_on_a_push_to_master(self):
        job = jobs(read("ci.yml"))["deploy"]
        self.assertIn("uses: ./.github/workflows/deploy.yml", job)
        self.assertIn("needs: [ build-and-test, css-up-to-date, accessibility ]", job)
        self.assertIn("github.event_name == 'push'", job)
        self.assertIn("github.ref == 'refs/heads/master'", job)
        self.assertIn("vars.DEPLOY_ENABLED == 'true'", job)

    def test_release_deploys_after_the_release_is_published(self):
        found = jobs(read("release.yml"))
        self.assertIn("needs: github-release", found["is-master-head"])
        job = found["deploy"]
        self.assertIn("uses: ./.github/workflows/deploy.yml", job)
        self.assertIn("needs: is-master-head", job)
        self.assertIn("needs.is-master-head.outputs.deploy == 'true'", job)
        self.assertIn("vars.DEPLOY_ENABLED == 'true'", job)

    def test_callers_pass_only_the_two_secrets(self):
        for name in ("ci.yml", "release.yml"):
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


class IsMasterHeadFeatureTests(unittest.TestCase):
    """Runs the comparison step with a stand-in gh that answers for the tag and for master."""

    TAG_SHA = "8a9e23a2a248ab8269e94e2d16186b90e0a0e60c"

    def run_step(self, master_sha: str) -> tuple[str, str]:
        script = run_block(jobs(read("release.yml"))["is-master-head"])
        with tempfile.TemporaryDirectory() as tmp:
            fake_gh = Path(tmp) / "gh"
            fake_gh.write_text(
                "#!/bin/sh\n"
                'case "$2" in\n'
                f'  */commits/master) echo "{master_sha}" ;;\n'
                f'  */commits/v0.2.1) echo "{self.TAG_SHA}" ;;\n'
                '  *) echo "unexpected: $*" >&2; exit 1 ;;\n'
                "esac\n",
                encoding="utf-8",
            )
            fake_gh.chmod(fake_gh.stat().st_mode | stat.S_IEXEC)
            output = Path(tmp) / "output"
            output.touch()
            env = dict(os.environ, PATH=f"{tmp}{os.pathsep}{os.environ['PATH']}", GITHUB_OUTPUT=str(output),
                       GH_TOKEN="x", REPO="lauPhilip/au-btech-literature-review-agent", TAG="v0.2.1")
            result = subprocess.run(["bash", "-e", "-c", script], env=env, capture_output=True, text=True,
                                    check=True)
            return output.read_text(encoding="utf-8"), result.stdout

    def test_a_tag_on_masters_newest_commit_is_deployed(self):
        output, log = self.run_step(master_sha=self.TAG_SHA)
        self.assertEqual("deploy=true\n", output)
        self.assertIn("deploying the release", log)

    def test_a_tag_on_an_older_commit_is_not_deployed(self):
        output, log = self.run_step(master_sha="1111111111111111111111111111111111111111")
        self.assertEqual("deploy=false\n", output)
        self.assertIn("Not deployed: v0.2.1 is on", log)


if __name__ == "__main__":
    unittest.main()
