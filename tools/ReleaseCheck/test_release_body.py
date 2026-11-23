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

NOTES = """Reviews are cheaper and better checked. A run can differ from 0.4.0, since the prompts changed.

### Added

You can stop a review. 🛑

You can start one through the API.

### Fixed

The methods no longer use double brackets.
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
    def test_splits_lead_and_kinds_in_order(self):
        lead, kinds = rb.split_section(NOTES)
        self.assertTrue(lead.startswith("Reviews are cheaper"))
        self.assertEqual(["Added", "Fixed"], [kind for kind, _ in kinds])

    def test_first_sentence_keeps_versions_whole(self):
        first, rest = rb.first_sentence("Runs differ from v0.4.0 now. The rest follows.")
        self.assertEqual("Runs differ from v0.4.0 now.", first)
        self.assertEqual("The rest follows.", rest)

    def test_counts_paragraphs_or_list_items(self):
        self.assertEqual(2, rb.paragraphs("One.\n\nTwo."))
        self.assertEqual(3, rb.paragraphs("- a\n- b\n- c"))

    def test_reads_only_the_top_level_authors(self):
        self.assertEqual([("Lau", "Philip S. P."), ("", "Nidhi")], rb.citation_authors(CITATION))


class BodyTests(unittest.TestCase):
    def setUp(self):
        self.body = compose()

    def test_opens_with_the_first_sentence_in_bold(self):
        self.assertTrue(self.body.startswith("**Reviews are cheaper and better checked.**\n"))

    def test_what_it_means_holds_the_rest_of_the_lead_and_a_table(self):
        part = self.body.split("## What this means for your review\n\n", 1)[1].split("\n## ", 1)[0]
        self.assertTrue(part.startswith("A run can differ from 0.4.0, since the prompts changed."))
        self.assertIn("| [Added](#added) | 2 | New things you can use. |", part)
        self.assertIn("| [Fixed](#fixed) | 1 | Wrong before, right now. |", part)
        self.assertNotIn("Changed", part)

    def test_kinds_keep_the_changelog_text_and_order(self):
        added = self.body.index("## Added\n\nYou can stop a review. 🛑\n\nYou can start one through the API.")
        self.assertLess(added, self.body.index("## Fixed\n\nThe methods no longer use double brackets."))

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
        body = compose(notes="### Fixed\n\nA fix. 🐛")
        self.assertTrue(body.startswith("## What this means for your review\n\n| Kind of change"))

    def test_the_body_adds_no_emoji_of_its_own(self):
        self.assertEqual(["🛑"], rc.find_emoji(compose(generated=None)))


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


if __name__ == "__main__":
    unittest.main()
