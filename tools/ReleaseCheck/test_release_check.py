"""Tests for release_check.py. Run from the repository root:

    python3 -m unittest discover -s tools/ReleaseCheck -v

The unit tests check each rule on its own. The feature tests build a small git repository with an annotated or a
lightweight tag, run the script as release.yml does, and check the title, the notes and the reasons it stops.
The notes tests check the readability score and the emoji count against texts whose answer is known.
The last tests check the repository's own CHANGELOG.md and CITATION.cff, so a broken file is caught on the pull
request, not on the day of a release.
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

CHANGELOG = """# Changelog

All notable changes to TraceableAI are listed here.

## [Unreleased]

### Added

- Something not yet released.

## [0.2.0] - 2026-12-01

### Fixed

- A later fix that stops a crash. 🐛

## [0.1.0] - 2026-11-20

### Added

- Research questions on the start form. 🎉

### Changed

- Versions come from git tags.

[Unreleased]: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.2.0...HEAD
[0.1.0]: https://github.com/lauPhilip/au-btech-literature-review-agent/releases/tag/v0.1.0
"""


def citation(version: str | None, date: str | None) -> str:
    lines = ["cff-version: 1.2.0", 'title: "TraceableAI"', "type: software"]
    if version is not None:
        lines.append(f'version: "{version}"')
    if date is not None:
        lines.append(f"date-released: {date}")
    lines += ["preferred-citation:", "  type: conference-paper", "  version: 9.9.9", "  year: 2026"]
    return "\n".join(lines) + "\n"


class ParseVersionTests(unittest.TestCase):
    def test_release_version(self):
        self.assertEqual(rc.parse_version("v0.1.0"), ("0.1.0", False))

    def test_pre_release_version(self):
        self.assertEqual(rc.parse_version("v0.2.0-rc.1"), ("0.2.0-rc.1", True))

    def test_rejects_tags_that_are_not_versions(self):
        for tag in ["0.1.0", "v0.1", "v01.0.0", "v0.1.0.1", "v0.1.0-", "version-1", "v1.2.3-01"]:
            with self.subTest(tag=tag), self.assertRaises(rc.ReleaseError):
                rc.parse_version(tag)


class TitleTests(unittest.TestCase):
    def test_title_is_tag_and_message(self):
        self.assertEqual(rc.release_title("v0.1.0", "Research questions"), "v0.1.0 - Research questions")

    def test_tag_in_message_is_not_doubled(self):
        self.assertEqual(rc.release_title("v0.1.0", "v0.1.0 - Research questions"), "v0.1.0 - Research questions")

    def test_empty_message_stops(self):
        with self.assertRaisesRegex(rc.ReleaseError, "no title"):
            rc.release_title("v0.1.0", "   ")


class ChangelogTests(unittest.TestCase):
    def test_section_is_text_up_to_next_heading(self):
        section = rc.changelog_section(CHANGELOG, "0.2.0")
        self.assertEqual(section, "### Fixed\n\n- A later fix that stops a crash. 🐛")

    def test_last_section_leaves_out_link_definitions(self):
        section = rc.changelog_section(CHANGELOG, "0.1.0")
        self.assertIn("Research questions on the start form.", section)
        self.assertIn("### Changed", section)
        self.assertNotIn("releases/tag", section)

    def test_missing_section_stops(self):
        with self.assertRaisesRegex(rc.ReleaseError, r"no section \"## \[0.3.0\]\""):
            rc.changelog_section(CHANGELOG, "0.3.0")

    def test_version_is_matched_exactly(self):
        with self.assertRaises(rc.ReleaseError):
            rc.changelog_section(CHANGELOG, "0.1")

    def test_empty_section_stops(self):
        with self.assertRaisesRegex(rc.ReleaseError, "empty"):
            rc.changelog_section("## [0.1.0] - 2026-11-20\n\n## [0.0.9]\n- old\n", "0.1.0")


HARD_NOTES = """### Added

- Comprehensive methodological documentation describing the computational infrastructure, the configuration
  parameters, and the operational responsibilities associated with institutional deployment considerations. 🎉
"""


class SyllableTests(unittest.TestCase):
    def test_known_words(self):
        expected = {"the": 1, "cat": 1, "plain": 1, "makes": 1, "changed": 1, "review": 2, "table": 2,
                    "screening": 2, "English": 2, "citation": 3, "readability": 5}
        for word, count in expected.items():
            with self.subTest(word=word):
                self.assertEqual(rc.count_syllables(word), count)

    def test_parts_joined_by_a_hyphen_are_added(self):
        self.assertEqual(rc.count_syllables("full-text"), 2)

    def test_numbers_count_as_one(self):
        self.assertEqual(rc.count_syllables("2026"), 1)


class EmojiTests(unittest.TestCase):
    def test_plain_text_has_none(self):
        self.assertEqual(rc.find_emoji("Plain words, a tick \u2713, a star \u2605 and a (c) sign."), [])

    def test_each_emoji_is_found(self):
        self.assertEqual(rc.find_emoji("Ready \U0001F389 set \u2728 go \u2705"), ["\U0001F389", "\u2728", "\u2705"])

    def test_a_sequence_that_shows_as_one_picture_counts_once(self):
        family = "\U0001F468\u200d\U0001F469\u200d\U0001F467"
        thumbs = "\U0001F44D\U0001F3FD"
        flag = "\U0001F1E9\U0001F1F0"
        keycap = "1\ufe0f\u20e3"
        heart = "\u2764\ufe0f"
        for emoji in [family, thumbs, flag, keycap, heart]:
            with self.subTest(emoji=emoji):
                self.assertEqual(rc.find_emoji(f"a {emoji} b"), [emoji])


class ReadingEaseTests(unittest.TestCase):
    def test_short_plain_sentences_score_high(self):
        score = rc.reading_ease("The cat sat on the mat. It was a good day.")
        self.assertGreater(score, 100)

    def test_long_formal_sentences_score_low(self):
        self.assertLess(rc.reading_ease(HARD_NOTES), 0)

    def test_score_follows_the_formula(self):
        # 2 sentences, 6 words, 7 syllables: 206.835 - 1.015 x 3 - 84.6 x 7/6.
        self.assertAlmostEqual(rc.reading_ease("Screening is fast. The cat sat."), 206.835 - 3.045 - 84.6 * 7 / 6)

    def test_headings_links_and_code_marks_are_not_read(self):
        with_markup = "### Added\n\n- The [Metrics](https://example.org/metrics/page) page is `new`."
        self.assertAlmostEqual(rc.reading_ease(with_markup), rc.reading_ease("The Metrics page is new."))

    def test_a_list_item_without_a_full_stop_ends_a_sentence(self):
        self.assertAlmostEqual(rc.reading_ease("- The cat sat\n- The dog ran"),
                               rc.reading_ease("The cat sat. The dog ran."))

    def test_version_numbers_do_not_end_a_sentence(self):
        self.assertAlmostEqual(rc.reading_ease("It is v0.1.0 now."), rc.reading_ease("It is v010 now."))

    def test_no_words_gives_none(self):
        self.assertIsNone(rc.reading_ease("### Added\n\n\U0001F389"))


class NotesTests(unittest.TestCase):
    def test_plain_notes_with_one_emoji_pass(self):
        self.assertEqual(rc.check_notes("You can now ask your own questions. The paper answers them. \U0001F389", "0.1.0"), [])

    def test_hard_notes_are_stopped_with_the_score(self):
        problems = rc.check_notes(HARD_NOTES, "0.1.0")
        self.assertEqual(len(problems), 1)
        self.assertIn("Flesch Reading Ease", problems[0])
        self.assertIn("at least 60", problems[0])

    def test_no_emoji_is_stopped(self):
        problems = rc.check_notes("You can now ask your own questions.", "0.1.0")
        self.assertEqual(problems, ["The notes for 0.1.0 need exactly one emoji; they have none."])

    def test_two_emoji_are_stopped_and_shown(self):
        problems = rc.check_notes("New questions \U0001F389 and a faster check \u26a1.", "0.1.0")
        self.assertEqual(problems, ["The notes for 0.1.0 need exactly one emoji; they have 2 (\U0001F389 \u26a1)."])


class CitationTests(unittest.TestCase):
    def test_matching_citation_has_no_problems(self):
        self.assertEqual(rc.check_citation(citation("0.1.0", "2026-11-20"), "0.1.0", "2026-11-20"), [])

    def test_only_top_level_keys_count(self):
        fields = rc.citation_fields(citation(None, None))
        self.assertNotIn("version", fields)

    def test_missing_fields_are_both_reported(self):
        problems = rc.check_citation(citation(None, None), "0.1.0", "2026-11-20")
        self.assertEqual(len(problems), 2)
        self.assertIn("version nothing", problems[0])
        self.assertIn("date-released: 2026-11-20", problems[1])

    def test_wrong_version_and_date_are_reported(self):
        problems = rc.check_citation(citation("0.0.9", "2026-11-19"), "0.1.0", "2026-11-20")
        self.assertIn('"0.0.9"', problems[0])
        self.assertIn('"2026-11-19"', problems[1])

    def test_quotes_and_comments_are_ignored(self):
        text = "version: '0.1.0'  # the release\ndate-released: \"2026-11-20\"\n"
        self.assertEqual(rc.check_citation(text, "0.1.0", "2026-11-20"), [])


class PrepareTests(unittest.TestCase):
    def test_every_reason_is_listed_at_once(self):
        tag = rc.Tag("v0.3.0", False, "", "")
        with self.assertRaises(rc.ReleaseError) as caught:
            rc.prepare(tag, CHANGELOG, citation("0.1.0", "2026-11-20"))
        message = str(caught.exception)
        self.assertIn("lightweight tag", message)
        self.assertIn("no section", message)

    def test_good_tag_gives_title_and_notes(self):
        tag = rc.Tag("v0.1.0", True, "Research questions", "2026-11-20")
        release = rc.prepare(tag, CHANGELOG, citation("0.1.0", "2026-11-20"))
        self.assertEqual(release.title, "v0.1.0 - Research questions")
        self.assertTrue(release.notes.startswith("### Added"))
        self.assertFalse(release.prerelease)

    def test_missing_files_are_named(self):
        tag = rc.Tag("v0.1.0", True, "Title", "2026-11-20")
        with self.assertRaises(rc.ReleaseError) as caught:
            rc.prepare(tag, None, None)
        self.assertIn("CHANGELOG.md is missing", str(caught.exception))
        self.assertIn("CITATION.cff is missing", str(caught.exception))


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


class RepositoryFilesTests(unittest.TestCase):
    """The repository's own files, so a mistake shows on the pull request."""

    ROOT = Path(__file__).resolve().parents[2]

    def test_changelog_has_unreleased_section_and_valid_versions(self):
        text = (self.ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
        versions = [m.group("version") for line in text.splitlines() if (m := rc._SECTION.match(line))]
        self.assertEqual(versions[0], "Unreleased", "the first section of CHANGELOG.md is [Unreleased]")
        for version in versions[1:]:
            with self.subTest(version=version):
                rc.parse_version("v" + version)
                self.assertEqual(rc.check_notes(rc.changelog_section(text, version), version), [])

    def test_unreleased_notes_would_pass_the_release_check(self):
        """[Unreleased] becomes the next version's notes, so it is held to the same rules on every pull request."""
        text = (self.ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
        try:
            notes = rc.changelog_section(text, "Unreleased")
        except rc.ReleaseError:
            self.skipTest("[Unreleased] is empty, as it is just after a release")
        self.assertEqual(rc.check_notes(notes, "Unreleased"), [])

    def test_citation_version_has_a_changelog_section(self):
        fields = rc.citation_fields((self.ROOT / "CITATION.cff").read_text(encoding="utf-8"))
        if "version" not in fields:
            self.assertNotIn("date-released", fields, "date-released is set only together with version")
            return
        changelog = (self.ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
        self.assertTrue(rc.changelog_section(changelog, fields["version"]))
        self.assertRegex(fields.get("date-released", ""), r"^\d{4}-\d{2}-\d{2}$")


if __name__ == "__main__":
    unittest.main()
