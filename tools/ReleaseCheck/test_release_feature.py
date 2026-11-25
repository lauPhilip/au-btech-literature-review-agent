"""Feature tests for release_check.py. Run from the repository root:

    python3 -m unittest discover -s tools/ReleaseCheck -v

Each test builds a small git repository with an annotated or a lightweight tag, runs the script as release.yml does,
and checks the title, the notes and the reasons it stops.
"""

from __future__ import annotations

import os
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path
from unittest import mock

sys.path.insert(0, str(Path(__file__).resolve().parent))

import release_check as rc  # noqa: E402
from test_release_check import CHANGELOG, HARD_NOTES, citation  # noqa: E402


class ReleaseFeatureTests(unittest.TestCase):
    """Runs the script on a real git repository, the way release.yml runs it."""

    TAG_DATE = "2026-11-20T10:00:00+01:00"

    def setUp(self):
        self._dir = tempfile.TemporaryDirectory()
        self.repo = Path(self._dir.name)
        self.env = {
            **os.environ,
            "GIT_AUTHOR_NAME": "Test", "GIT_AUTHOR_EMAIL": "test@example.org",
            "GIT_COMMITTER_NAME": "Test", "GIT_COMMITTER_EMAIL": "test@example.org",
            "GIT_COMMITTER_DATE": self.TAG_DATE, "GIT_AUTHOR_DATE": self.TAG_DATE,
            "GIT_CONFIG_GLOBAL": os.devnull, "GIT_CONFIG_NOSYSTEM": "1",
        }
        self.git("init", "-q")

    def tearDown(self):
        self._dir.cleanup()

    def git(self, *args: str):
        subprocess.run(["git", *args], cwd=self.repo, env=self.env, check=True, capture_output=True)

    def commit(self, changelog: str, cff: str):
        (self.repo / "CHANGELOG.md").write_text(changelog, encoding="utf-8")
        (self.repo / "CITATION.cff").write_text(cff, encoding="utf-8")
        self.git("add", ".")
        self.git("commit", "-q", "-m", "(docs): release")

    def run_check(self, tag: str):
        notes = self.repo / "notes.md"
        output = self.repo / "github-output.txt"
        output.write_text("", encoding="utf-8")
        with mock.patch.dict(os.environ, {"GITHUB_OUTPUT": str(output), "GITHUB_ACTIONS": "true"}), \
                mock.patch("sys.stderr") as stderr:
            code = rc.main([tag, "--repo", str(self.repo), "--notes-out", str(notes)])
        errors = "".join(call.args[0] for call in stderr.write.call_args_list)
        return code, notes, output.read_text(encoding="utf-8"), errors

    def test_annotated_tag_publishes_with_title_and_changelog_notes(self):
        self.commit(CHANGELOG, citation("0.1.0", "2026-11-20"))
        self.git("tag", "-a", "v0.1.0", "-m", "Research questions\n\nLonger notes stay in the tag.")

        code, notes, output, errors = self.run_check("v0.1.0")

        self.assertEqual(code, 0, errors)
        self.assertIn("title=v0.1.0 - Research questions\n", output)
        self.assertIn("prerelease=false\n", output)
        text = notes.read_text(encoding="utf-8")
        self.assertIn("- Research questions on the start form. 🎉", text)
        self.assertNotIn("A later fix", text)
        self.assertNotIn("Something not yet released", text)

    def test_pre_release_is_marked(self):
        changelog = "# Changelog\n\n## [0.2.0-rc.1] - 2026-11-20\n\n### Added\n\n- A trial run to try things out. 🧪\n"
        self.commit(changelog, citation("0.2.0-rc.1", "2026-11-20"))
        self.git("tag", "-a", "v0.2.0-rc.1", "-m", "Trial")

        code, _, output, errors = self.run_check("v0.2.0-rc.1")

        self.assertEqual(code, 0, errors)
        self.assertIn("prerelease=true\n", output)

    def test_body_out_writes_the_composed_body_as_release_yml_asks(self):
        self.commit(CHANGELOG, citation("0.1.0", "2026-11-20"))
        self.git("tag", "-a", "v0.1.0", "-m", "Research questions")
        generated = self.repo / "generated.md"
        generated.write_text("## What's Changed\n* One by @a in https://x/pull/1\n\n**Full Changelog**: x\n",
                             encoding="utf-8")
        body = self.repo / "body.md"

        with mock.patch("sys.stderr"), mock.patch("sys.stdout"):
            code = rc.main(["v0.1.0", "--repo", str(self.repo), "--body-out", str(body),
                            "--generated-notes", str(generated), "--previous-tag", "v0.0.9"])

        self.assertEqual(code, 0)
        text = body.read_text(encoding="utf-8")
        self.assertIn("## Added\n\n- Research questions on the start form. 🎉", text)
        self.assertIn("<summary>The 1 pull request in this version</summary>", text)
        self.assertIn("compare/v0.0.9...v0.1.0", text)

    def test_lightweight_tag_stops_with_the_reason(self):
        self.commit(CHANGELOG, citation("0.1.0", "2026-11-20"))
        self.git("tag", "v0.1.0")

        code, notes, output, errors = self.run_check("v0.1.0")

        self.assertEqual(code, 1)
        self.assertIn("lightweight tag", errors)
        self.assertIn("::error title=Release check::", errors)
        self.assertFalse(notes.exists())
        self.assertEqual(output, "")

    def test_citation_that_does_not_match_stops(self):
        self.commit(CHANGELOG, citation("0.0.9", "2026-11-19"))
        self.git("tag", "-a", "v0.1.0", "-m", "Research questions")

        code, _, _, errors = self.run_check("v0.1.0")

        self.assertEqual(code, 1)
        self.assertIn('CITATION.cff gives version "0.0.9"', errors)
        self.assertIn("the tag was made on 2026-11-20", errors)

    def test_version_without_changelog_section_stops(self):
        self.commit(CHANGELOG, citation("0.3.0", "2026-11-20"))
        self.git("tag", "-a", "v0.3.0", "-m", "Next")

        code, _, _, errors = self.run_check("v0.3.0")

        self.assertEqual(code, 1)
        self.assertIn('no section "## [0.3.0]"', errors)

    def test_hard_notes_with_two_emoji_stop_with_both_reasons(self):
        hard = CHANGELOG.replace("- Research questions on the start form. 🎉\n", HARD_NOTES.split("\n\n", 1)[1] + "- More. 🚀\n")
        self.commit(hard, citation("0.1.0", "2026-11-20"))
        self.git("tag", "-a", "v0.1.0", "-m", "Research questions")

        code, notes, output, errors = self.run_check("v0.1.0")

        self.assertEqual(code, 1)
        self.assertIn("are hard to read: Flesch Reading Ease", errors)
        self.assertIn("need exactly one emoji; they have 2", errors)
        self.assertIn("::error title=Release check::", errors)
        self.assertFalse(notes.exists())
        self.assertNotIn("title=", output)

    def test_unknown_tag_stops(self):
        self.commit(CHANGELOG, citation("0.1.0", "2026-11-20"))

        code, _, _, errors = self.run_check("v0.1.0")

        self.assertEqual(code, 1)
        self.assertIn("failed", errors)


if __name__ == "__main__":
    unittest.main()
