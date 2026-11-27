"""Tests for release_body.py, which composes the GitHub release body. Run from the repository root:

    python3 -m unittest discover -s tools/ReleaseCheck -v

The body must be built only from CHANGELOG.md, CITATION.cff, GitHub's generated notes and the two tags, so every
test feeds known text in and checks where each part lands. The last test composes the body of the repository's own
[Unreleased] section, so a section that would give a broken body is caught on the pull request.
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import release_body as rb  # noqa: E402
import release_check as rc  # noqa: E402

ROOT = Path(__file__).resolve().parents[2]
REPO = "lauPhilip/au-btech-literature-review-agent"

NOTES = """> **Dev note:** Reviews are cheaper and better checked. A run can differ from 0.4.0.
> \u2014 The TraceableAI team

### \U0001F31F Patch highlights

Reviews are cheaper. A new run can differ from 0.4.0, since the prompts changed.

### \U0001F52C Systematic review

#### Screening

> A criterion with "or" was read as needing every part.

- **Screening prompt**: screening-v7 \u21d2 screening-v8
- Fewer good records are dropped in code.

#### Themes

- Themes point back to earlier ones.

### \U0001F310 Site

You can stop a review.

You can start one through the API.

### \U0001F41B Bug fixes

- Fixed a bug where the methods used double brackets.
"""

CITATION = """cff-version: 1.2.0
title: "TraceableAI: a tool"
version: "0.5.0"
date-released: 2026-11-23
doi: 10.5281/zenodo.24379094
authors:
  - family-names: Lau
    given-names: "Philip S. P."
    affiliation: "Aarhus University"
  - given-names: Nidhi
license: Apache-2.0
preferred-citation:
  authors:
    - family-names: Someone
      given-names: Else
"""

GENERATED = """## What's Changed
* Stop a review by @lauPhilip in https://github.com/lauPhilip/au-btech-literature-review-agent/pull/1
* Fix brackets by @lauPhilip in https://github.com/lauPhilip/au-btech-literature-review-agent/pull/2

## New Contributors
* @someone made their first contribution in https://github.com/lauPhilip/au-btech-literature-review-agent/pull/2

**Full Changelog**: https://github.com/lauPhilip/au-btech-literature-review-agent/compare/v0.4.0...v0.5.0
"""


def compose(notes: str = NOTES, generated: str | None = GENERATED, previous: str | None = "v0.4.0") -> str:
    return rb.compose_body("v0.5.0", "0.5.0", notes, CITATION, REPO, previous, generated)


class PartsTests(unittest.TestCase):
    def test_splits_lead_and_areas_in_order(self):
        lead, areas = rb.split_section(NOTES)
        self.assertTrue(lead.startswith("> **Dev note:**"))
        self.assertEqual(["\U0001F31F Patch highlights", "\U0001F52C Systematic review", "\U0001F310 Site",
                          "\U0001F41B Bug fixes"], [heading for heading, _ in areas])

    def test_sub_headings_do_not_split_an_area(self):
        _, areas = rb.split_section(NOTES)
        self.assertIn("#### Themes", areas[1][1])

    def test_counts_list_items_or_paragraphs_and_not_headings_or_quotes(self):
        self.assertEqual(2, rb.paragraphs("One.\n\nTwo."))
        self.assertEqual(3, rb.paragraphs("- a\n- b\n- c"))
        self.assertEqual(2, rb.paragraphs("#### Screening\n\n> Why.\n\n- a\n- b"))
        self.assertEqual(1, rb.paragraphs("#### Screening\n\n> Why.\n\nOne change."))

    def test_reads_only_the_top_level_authors(self):
        self.assertEqual([("Lau", "Philip S. P."), ("", "Nidhi")], rb.citation_authors(CITATION))


class BodyTests(unittest.TestCase):
    def setUp(self):
        self.body = compose()

    def test_opens_with_the_dev_note_as_written(self):
        self.assertTrue(self.body.startswith(
            "> **Dev note:** Reviews are cheaper and better checked. A run can differ from 0.4.0.\n"
            "> \u2014 The TraceableAI team\n\n## In this patch\n"))

    def test_in_this_patch_has_one_row_per_area_with_its_count(self):
        part = self.body.split("## In this patch\n\n", 1)[1].split("\n\n", 1)[0]
        self.assertEqual([
            "| Area | Changes |", "|---|---|",
            "| Patch highlights | 1 |", "| Systematic review | 3 |", "| Site | 2 |", "| Bug fixes | 1 |",
        ], part.splitlines())

    def test_areas_keep_their_text_and_order_one_heading_level_up(self):
        first = self.body.index("## \U0001F31F Patch highlights\n\nReviews are cheaper.")
        second = self.body.index("## \U0001F52C Systematic review\n\n### Screening\n\n> A criterion")
        site = self.body.index("## \U0001F310 Site\n\nYou can stop a review.\n\nYou can start one through the API.")
        fixes = self.body.index("## \U0001F41B Bug fixes\n\n- Fixed a bug where the methods used double brackets.")
        self.assertLess(first, second)
        self.assertLess(second, site)
        self.assertLess(site, fixes)
        self.assertIn("- **Screening prompt**: screening-v7 \u21d2 screening-v8", self.body)

    def test_the_old_what_this_means_part_is_gone(self):
        self.assertNotIn("What this means for your review", self.body)
        self.assertNotIn("New things you can use", self.body)

    def test_cites_the_concept_doi_and_says_the_version_doi_follows(self):
        part = self.body.split("## Cite this version", 1)[1]
        self.assertIn("own version DOI. It follows a few minutes after the release", part)
        self.assertIn("> Lau, P. S. P., & Nidhi. (2026). *TraceableAI: a tool* (Version 0.5.0) [Computer software]. "
                      "Zenodo. https://doi.org/10.5281/zenodo.24379094", part)
        self.assertIn("  author  = {Lau, Philip S. P. and Nidhi},", part)
        self.assertIn("@software{traceableai_0_5_0,", part)
        self.assertNotIn("Someone", part)

    def test_pull_requests_are_folded_without_githubs_own_link(self):
        part = self.body.split("<details>", 1)[1].split("</details>", 1)[0]
        self.assertIn("<summary>The 3 pull requests in this version</summary>", part)
        self.assertIn("**New Contributors**", part)
        self.assertNotIn("What's Changed", part)
        self.assertNotIn("Full Changelog", self.body)

    def test_ends_with_the_comparison_link(self):
        self.assertTrue(self.body.endswith(
            f"[v0.4.0...v0.5.0](https://github.com/{REPO}/compare/v0.4.0...v0.5.0)\n"))

    def test_the_same_input_gives_the_same_body(self):
        self.assertEqual(self.body, compose())

    def test_parts_without_input_are_left_out(self):
        body = compose(generated=None, previous=None)
        self.assertNotIn("<details>", body)
        self.assertNotIn("compare", body)
        self.assertIn("## Cite this version", body)

    def test_a_version_without_a_lead_starts_with_its_table(self):
        body = compose(notes="### \U0001F41B Bug fixes\n\n- Fixed a bug where a fix was missing.")
        self.assertTrue(body.startswith("## In this patch\n\n| Area | Changes |"))

    def test_the_body_adds_no_emoji_of_its_own(self):
        self.assertEqual(rc.find_emoji(NOTES), rc.find_emoji(compose(generated=None)))


class RepositoryBodyTests(unittest.TestCase):
    def test_newest_section_and_citation_compose_a_body(self):
        """[Unreleased], or the newest version just after a release, when [Unreleased] is empty."""
        changelog = (ROOT / "CHANGELOG.md").read_text(encoding="utf-8")
        versions = [m.group("version") for line in changelog.splitlines() if (m := rc._SECTION.match(line))]
        try:
            notes = rc.changelog_section(changelog, "Unreleased")
        except rc.ReleaseError:
            notes = rc.changelog_section(changelog, versions[1])
        citation = (ROOT / "CITATION.cff").read_text(encoding="utf-8")
        body = rb.compose_body("v9.9.9", "9.9.9", notes, citation, REPO, "v0.0.1", None)
        self.assertIn("## Cite this version", body)
        self.assertIn("https://doi.org/10.5281/zenodo.24379094", body)
        self.assertRegex(body, r"> Lau, P\. .*\(Version 9\.9\.9\)")
        self.assertIn("## In this patch\n\n| Area | Changes |", body)


if __name__ == "__main__":
    unittest.main()
