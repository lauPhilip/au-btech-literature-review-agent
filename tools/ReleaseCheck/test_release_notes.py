"""Tests for release_notes.py, the emoji count and reading-ease score that release_check.py applies to the notes.
Run from the repository root:

    python3 -m unittest discover -s tools/ReleaseCheck -v

Each test checks a text whose answer is known.
"""

from __future__ import annotations

import sys
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

import release_notes as notes  # noqa: E402
from test_release_check import HARD_NOTES  # noqa: E402


class SyllableTests(unittest.TestCase):
    def test_known_words(self):
        expected = {"the": 1, "cat": 1, "plain": 1, "makes": 1, "changed": 1, "review": 2, "table": 2,
                    "screening": 2, "English": 2, "citation": 3, "readability": 5}
        for word, count in expected.items():
            with self.subTest(word=word):
                self.assertEqual(notes.count_syllables(word), count)

    def test_parts_joined_by_a_hyphen_are_added(self):
        self.assertEqual(notes.count_syllables("full-text"), 2)

    def test_numbers_count_as_one(self):
        self.assertEqual(notes.count_syllables("2026"), 1)


class EmojiTests(unittest.TestCase):
    def test_plain_text_has_none(self):
        self.assertEqual(notes.find_emoji("Plain words, a tick \u2713, a star \u2605 and a (c) sign."), [])

    def test_each_emoji_is_found(self):
        self.assertEqual(notes.find_emoji("Ready \U0001F389 set \u2728 go \u2705"), ["\U0001F389", "\u2728", "\u2705"])

    def test_a_sequence_that_shows_as_one_picture_counts_once(self):
        family = "\U0001F468\u200d\U0001F469\u200d\U0001F467"
        thumbs = "\U0001F44D\U0001F3FD"
        flag = "\U0001F1E9\U0001F1F0"
        keycap = "1\ufe0f\u20e3"
        heart = "\u2764\ufe0f"
        for emoji in [family, thumbs, flag, keycap, heart]:
            with self.subTest(emoji=emoji):
                self.assertEqual(notes.find_emoji(f"a {emoji} b"), [emoji])


class ReadingEaseTests(unittest.TestCase):
    def test_short_plain_sentences_score_high(self):
        score = notes.reading_ease("The cat sat on the mat. It was a good day.")
        self.assertGreater(score, 100)

    def test_long_formal_sentences_score_low(self):
        self.assertLess(notes.reading_ease(HARD_NOTES), 0)

    def test_score_follows_the_formula(self):
        # 2 sentences, 6 words, 7 syllables: 206.835 - 1.015 x 3 - 84.6 x 7/6.
        self.assertAlmostEqual(notes.reading_ease("Screening is fast. The cat sat."), 206.835 - 3.045 - 84.6 * 7 / 6)

    def test_headings_links_and_code_marks_are_not_read(self):
        with_markup = "### Added\n\n- The [Metrics](https://example.org/metrics/page) page is `new`."
        self.assertAlmostEqual(notes.reading_ease(with_markup), notes.reading_ease("The Metrics page is new."))

    def test_a_list_item_without_a_full_stop_ends_a_sentence(self):
        self.assertAlmostEqual(notes.reading_ease("- The cat sat\n- The dog ran"),
                               notes.reading_ease("The cat sat. The dog ran."))

    def test_version_numbers_do_not_end_a_sentence(self):
        self.assertAlmostEqual(notes.reading_ease("It is v0.1.0 now."), notes.reading_ease("It is v010 now."))

    def test_blockquote_marks_and_bold_marks_are_not_read(self):
        quoted = "> **Dev note:** The cat sat.\n> The dog ran.\n> \u2014 The team"
        plain = "Dev note: The cat sat. The dog ran. \u2014 The team"
        self.assertAlmostEqual(notes.reading_ease(quoted), notes.reading_ease(plain))

    def test_a_stat_line_is_one_sentence_and_the_arrow_is_not_a_word(self):
        self.assertAlmostEqual(notes.reading_ease("- **Limit**: none \u21d2 at most 3"),
                               notes.reading_ease("Limit: none at most 3"))

    def test_no_words_gives_none(self):
        self.assertIsNone(notes.reading_ease("### Added\n\n\U0001F389"))


if __name__ == "__main__":
    unittest.main()
