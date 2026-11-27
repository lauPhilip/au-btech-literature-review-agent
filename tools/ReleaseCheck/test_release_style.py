"""Tests for release_style.py, the patch-notes style that new release notes are held to. Run from the repository root:

    python3 -m unittest discover -s tools/ReleaseCheck -v

Each test changes one thing in notes that keep to the style and checks that exactly that fault is reported, with the
heading or line it is on.
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import release_style as style  # noqa: E402

GOOD = """> **Dev note:** This patch is about screening. We fixed a few things.
> — The TraceableAI team

### \U0001F31F Patch highlights

Screening reads "or" as a choice. A new run can differ from the last version.

### \U0001F52C Systematic review

#### Screening

> A criterion with "or" was read as needing every part.

- **Screening prompt**: screening-v7 ⇒ screening-v8
- Fewer good records are dropped in code.

### \U0001F41B Bug fixes

- Fixed a bug where a pilot run's title said "Pilot Pilot".
- Fixed a bug where a heading sat alone at the foot of a page.
"""


def problems(notes: str) -> list[str]:
    return style.check_style(notes)


def only(notes: str) -> str:
    found = problems(notes)
    assert len(found) == 1, found
    return found[0]


class GoodNotesTests(unittest.TestCase):
    def test_notes_in_the_style_have_no_problems(self):
        self.assertEqual(problems(GOOD), [])

    def test_a_section_without_fixes_or_other_areas_is_fine(self):
        short = GOOD.split("### \U0001F52C", 1)[0]
        self.assertEqual(problems(short), [])


class EmojiRuleTests(unittest.TestCase):
    def test_a_heading_without_an_emoji_is_named(self):
        self.assertIn('"### Patch highlights"', only(GOOD.replace("\U0001F31F ", "")))

    def test_a_heading_with_two_emoji_is_named(self):
        message = only(GOOD.replace("### \U0001F52C Systematic review", "### \U0001F52C\U0001F52C Systematic review"))
        self.assertIn("Systematic review", message)
        self.assertIn("2", message)

    def test_an_emoji_in_the_middle_of_a_heading_is_a_problem(self):
        self.assertIn("at its start", only(GOOD.replace("### \U0001F52C Systematic review",
                                                         "### Systematic \U0001F52C review")))

    def test_an_emoji_in_the_body_is_reported_with_its_line(self):
        message = only(GOOD.replace("- Fewer good records", "- \U0001F389 Fewer good records"))
        self.assertIn("Fewer good records", message)

    def test_an_emoji_in_the_dev_note_or_a_sub_heading_is_reported(self):
        self.assertIn("Dev note", only(GOOD.replace("about screening.", "about screening. \U0001F389")))
        self.assertIn("#### Screening", only(GOOD.replace("#### Screening", "#### Screening \U0001F50D")))


class ShapeRuleTests(unittest.TestCase):
    def test_the_lead_must_be_a_dev_note_blockquote(self):
        self.assertIn("Dev note", only(GOOD.replace("> **Dev note:** ", "> ")))
        self.assertIn("Dev note", only(GOOD.replace("> **Dev note:**", "**Dev note:**").replace("\n> —", "\n—")))
        self.assertIn("Dev note", only("### \U0001F31F Patch highlights\n\nText."))

    def test_patch_highlights_comes_first(self):
        notes = GOOD.replace("Patch highlights", "Site").replace("\U0001F31F", "\U0001F310")
        self.assertIn("Patch highlights", " ".join(problems(notes)))

    def test_areas_keep_their_order_and_their_emoji(self):
        swapped = GOOD.replace("### \U0001F41B Bug fixes", "### \U0001F310 Site\n\nThe tab shows progress.\n\n"
                               "### \U0001F52C Systematic review\n\nMore.\n\n### \U0001F41B Bug fixes")
        self.assertIn("out of order", " ".join(problems(swapped)))
        self.assertIn("needs the emoji", only(GOOD.replace("\U0001F52C Systematic", "\U0001F50D Systematic")))

    def test_an_unknown_area_is_named(self):
        self.assertIn("not an area", only(GOOD.replace("Systematic review", "Everything else")))

    def test_bug_fixes_come_last(self):
        notes = GOOD + "\n### \U0001F310 Site\n\nThe tab shows progress.\n"
        found = " ".join(problems(notes))
        self.assertIn("Bug fixes must be the last area", found)

    def test_bug_fixes_are_a_list_of_items_that_start_with_fixed(self):
        self.assertIn('must start with "Fixed"', only(GOOD.replace("- Fixed a bug where a heading", "- A heading")))
        self.assertIn("must be a list", only(GOOD.split("- Fixed a bug where a pilot")[0] + "A paragraph of fixes.\n"))


class StatLineTests(unittest.TestCase):
    def test_a_stat_line_needs_one_arrow_and_both_sides(self):
        bad = {
            "none after": "- **Screening prompt**: screening-v7 ⇒",
            "none before": "- **Screening prompt**: ⇒ screening-v8",
            "two arrows": "- **A**: a ⇒ b ⇒ c",
            "no label": "- screening-v7 ⇒ screening-v8",
            "not a bullet": "Screening prompt: v7 ⇒ v8",
        }
        for name, line in bad.items():
            with self.subTest(name):
                message = only(GOOD.replace("- **Screening prompt**: screening-v7 ⇒ screening-v8", line))
                self.assertIn(line.strip()[:20], message)

    def test_the_name_of_an_area_is_read_without_its_emoji(self):
        self.assertEqual(style.heading_name("\U0001F52C Systematic review"), "Systematic review")


class VersionCutoffTests(unittest.TestCase):
    def test_released_versions_keep_the_old_form(self):
        for version in ["0.6.0", "0.5.0", "0.1.0", "0.6.0-rc.1"]:
            with self.subTest(version=version):
                self.assertFalse(style.uses_patch_notes(version))

    def test_unreleased_and_later_versions_use_the_new_form(self):
        for version in ["Unreleased", "0.6.1", "0.7.0", "1.0.0"]:
            with self.subTest(version=version):
                self.assertTrue(style.uses_patch_notes(version))


if __name__ == "__main__":
    unittest.main()
