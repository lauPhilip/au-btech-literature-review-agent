"""Tests for newest_tag.py: semver order, and the command in a real repository."""

import os
import subprocess
import tempfile
import unittest
from pathlib import Path

from newest_tag import is_newest, main, sort_key

SCRIPT = Path(__file__).resolve().parent / "newest_tag.py"


class OrderTests(unittest.TestCase):
    def test_orders_numbers_not_text(self):
        self.assertTrue(is_newest("v0.10.0", ["v0.9.0", "v0.10.0", "v0.2.0"]))
        self.assertFalse(is_newest("v0.9.0", ["v0.9.0", "v0.10.0"]))

    def test_a_pre_release_is_below_its_release(self):
        self.assertFalse(is_newest("v0.2.0-rc.1", ["v0.2.0-rc.1", "v0.2.0"]))
        self.assertTrue(is_newest("v0.2.0", ["v0.2.0-rc.1", "v0.2.0"]))

    def test_a_pre_release_of_a_newer_version_is_newer(self):
        self.assertTrue(is_newest("v0.3.0-rc.1", ["v0.2.0", "v0.3.0-rc.1"]))

    def test_the_semver_example_chain_is_in_order(self):
        chain = ["v1.0.0-alpha", "v1.0.0-alpha.1", "v1.0.0-alpha.beta", "v1.0.0-beta", "v1.0.0-beta.2",
                 "v1.0.0-beta.11", "v1.0.0-rc.1", "v1.0.0"]
        keys = [sort_key(tag) for tag in chain]
        self.assertEqual(keys, sorted(keys))
        self.assertEqual(len(set(keys)), len(keys))

    def test_build_metadata_is_ignored(self):
        self.assertTrue(is_newest("v1.0.0+a", ["v1.0.0+a", "v1.0.0+b"]))

    def test_the_only_tag_is_newest_and_a_non_version_is_not(self):
        self.assertTrue(is_newest("v0.1.0", ["v0.1.0"]))
        self.assertFalse(is_newest("latest", ["v0.1.0"]))

    def test_other_tags_are_ignored(self):
        self.assertTrue(is_newest("v0.1.0", ["v0.1.0", "nightly", "v1.0"]))


class CommandTests(unittest.TestCase):
    def run_in_repo(self, tags: list[str], tag: str) -> tuple[str, str]:
        with tempfile.TemporaryDirectory() as tmp:
            env = dict(os.environ, GIT_AUTHOR_NAME="t", GIT_AUTHOR_EMAIL="t@e", GIT_COMMITTER_NAME="t",
                       GIT_COMMITTER_EMAIL="t@e", GITHUB_OUTPUT=str(Path(tmp) / "out"))
            Path(env["GITHUB_OUTPUT"]).touch()
            git = lambda *a: subprocess.run(["git", "-C", tmp, *a], env=env, check=True, capture_output=True)  # noqa: E731
            git("init", "-q")
            git("commit", "-q", "--allow-empty", "-m", "x")
            for name in tags:
                git("tag", name)
            result = subprocess.run(["python3", str(SCRIPT), tag], cwd=tmp, env=env, capture_output=True,
                                    text=True, check=True)
            return Path(env["GITHUB_OUTPUT"]).read_text(encoding="utf-8"), result.stdout

    def test_deploys_the_newest(self):
        output, log = self.run_in_repo(["v0.2.0", "v0.2.1", "v0.1.0"], "v0.2.1")
        self.assertEqual("deploy=true\n", output)
        self.assertIn("deploying the release", log)

    def test_skips_an_older_one_with_a_notice(self):
        output, log = self.run_in_repo(["v0.2.0", "v0.2.1"], "v0.2.0")
        self.assertEqual("deploy=false\n", output)
        self.assertIn("Not deployed: v0.2.0 is older than v0.2.1", log)

    def test_needs_exactly_one_argument(self):
        self.assertEqual(2, main([]))


if __name__ == "__main__":
    unittest.main()
