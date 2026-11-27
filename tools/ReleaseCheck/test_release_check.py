"""Tests for release_check.py. Run from the repository root:

    python3 -m unittest discover -s tools/ReleaseCheck -v

The unit tests check each rule on its own; the feature tests, which run the script on a small git repository, are in
test_release_feature.py.
The readability score and the emoji count are tested on their own in test_release_notes.py.
The last tests check the repository's own CHANGELOG.md and CITATION.cff, so a broken file is caught on the pull
request, not on the day of a release.
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import release_check as rc  # noqa: E402
from test_release_style import GOOD  # noqa: E402

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


class PatchNotesTests(unittest.TestCase):
    """Versions after 0.6.0 and [Unreleased] are held to the patch-notes style; older versions keep the old form."""

    def test_new_versions_are_held_to_the_style_not_to_one_emoji(self):
        problems = rc.check_notes(GOOD, "0.7.0")
        self.assertEqual(problems, [])
        self.assertEqual(rc.check_notes(GOOD, "Unreleased"), [])

    def test_a_style_fault_is_reported_with_the_version(self):
        problems = rc.check_notes(GOOD.replace("\U0001F31F ", ""), "0.7.0")
        self.assertEqual(len(problems), 1)
        self.assertIn("The notes for 0.7.0:", problems[0])
        self.assertIn("### Patch highlights", problems[0])

    def test_released_versions_keep_the_one_emoji_rule(self):
        self.assertEqual(rc.check_notes("### Added\n\n- You can stop a run. \U0001F389", "0.6.0"), [])
        self.assertIn("exactly one emoji", " ".join(rc.check_notes("### Added\n\n- You can stop a run.", "0.6.0")))
        self.assertTrue(rc.check_notes(GOOD, "0.6.0"), "the new style has many emoji, which 0.6.0 may not have")

    def test_notes_in_the_style_still_need_to_be_readable(self):
        hard = GOOD.replace("Screening reads", HARD_NOTES + " Screening reads")
        self.assertIn("hard to read", " ".join(rc.check_notes(hard, "0.7.0")))


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


    def test_citation_doi_is_the_concept_doi_in_the_readme_badge(self):
        """The concept DOI always resolves to the newest version, so the badge and the citation name the same one."""
        doi = rc.citation_fields((self.ROOT / "CITATION.cff").read_text(encoding="utf-8")).get("doi", "")
        self.assertRegex(doi, r"^10\.5281/zenodo\.\d+$", "CITATION.cff gives Zenodo's concept DOI as doi")
        readme = (self.ROOT / "README.md").read_text(encoding="utf-8")
        self.assertIn(f"](https://doi.org/{doi})", readme, "the README's DOI badge links to the same DOI")


if __name__ == "__main__":
    unittest.main()
