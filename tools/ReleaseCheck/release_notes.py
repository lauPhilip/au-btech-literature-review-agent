"""How release_check.py reads release notes: the emoji in them and their Flesch Reading Ease score.

Kept apart from release_check.py so each file stays short; release_check imports these names back.
Standard library only.
"""

from __future__ import annotations

import re


# Release notes must be plain English: a Flesch Reading Ease of 60 or more (60 to 70 is "plain English").
MIN_READING_EASE = 60.0

# Characters that show as an emoji on their own (Emoji_Presentation), outside the main emoji blocks.
_EMOJI_BMP = set(
    "\u231a\u231b\u23e9\u23ea\u23eb\u23ec\u23f0\u23f3\u25fd\u25fe\u2614\u2615\u267f\u2693\u26a1"
    "\u26aa\u26ab\u26bd\u26be\u26c4\u26c5\u26ce\u26d4\u26ea\u26f2\u26f3\u26f5\u26fa\u26fd\u2705"
    "\u270a\u270b\u2728\u274c\u274e\u2753\u2754\u2755\u2757\u2795\u2796\u2797\u27b0\u27bf\u2b1b"
    "\u2b1c\u2b50\u2b55"
) | {chr(c) for c in range(0x2648, 0x2654)}
_VS16, _ZWJ, _KEYCAP = "\ufe0f", "\u200d", "\u20e3"


def _is_regional(char: str) -> bool:
    return 0x1F1E6 <= ord(char) <= 0x1F1FF


def _is_skin_tone(char: str) -> bool:
    return 0x1F3FB <= ord(char) <= 0x1F3FF


def _is_pictograph(text: str, i: int) -> bool:
    """True when the character at i starts an emoji: from the emoji blocks, or a symbol shown as one."""
    char = text[i]
    code = ord(char)
    if 0x1F000 <= code <= 0x1FAFF and not _is_skin_tone(char):
        return True
    if char in _EMOJI_BMP:
        return True
    # A text symbol (such as a heart or a warning sign) becomes an emoji when variation selector 16 follows it.
    followed_by_vs16 = i + 1 < len(text) and text[i + 1] == _VS16
    return followed_by_vs16 and (0x2000 <= code <= 0x2BFF or char in "\u00a9\u00ae\u3030\u303d\u3297\u3299")


def find_emoji(text: str) -> list[str]:
    """Every emoji in the text, in order. A sequence that shows as one picture counts once: a family joined with
    zero-width joiners, a flag made of two regional letters, a skin tone, or a keycap such as 1 with its box."""
    found: list[str] = []
    i = 0
    while i < len(text):
        char = text[i]
        if char in "0123456789#*" and text[i + 1:i + 3] in (_VS16 + _KEYCAP, _KEYCAP):
            length = 3 if text[i + 1] == _VS16 else 2
            found.append(text[i:i + length])
            i += length
            continue
        if _is_regional(char):
            length = 2 if i + 1 < len(text) and _is_regional(text[i + 1]) else 1
            found.append(text[i:i + length])
            i += length
            continue
        if not _is_pictograph(text, i):
            i += 1
            continue
        start = i
        i += 1
        while i < len(text):
            if text[i] == _VS16 or _is_skin_tone(text[i]):
                i += 1
            elif text[i] == _ZWJ and i + 1 < len(text) and _is_pictograph(text, i + 1):
                i += 2
            else:
                break
        found.append(text[start:i])
    return found


def _plain_text(markdown: str) -> list[str]:
    """The notes as sentences of plain text. Headings are left out; a list item or a paragraph without a full stop
    still ends a sentence; links keep their words and lose their address; code marks and emphasis go."""
    blocks: list[str] = []
    current: list[str] = []
    for line in markdown.splitlines():
        stripped = line.strip()
        starts_item = re.match(r"^([-*+]|\d+[.)])\s+", stripped)
        if not stripped or stripped.startswith("#") or starts_item:
            if current:
                blocks.append(" ".join(current))
            current = []
            if starts_item:
                current.append(stripped[starts_item.end():])
            continue
        current.append(stripped)
    if current:
        blocks.append(" ".join(current))

    sentences: list[str] = []
    for block in blocks:
        text = re.sub(r"!?\[([^\]]*)\]\([^)]*\)", r"\1", block)
        text = re.sub(r"https?://\S+", " ", text)
        text = re.sub(r"[`*_]", "", text)
        for emoji in find_emoji(text):
            text = text.replace(emoji, " ")
        # A full stop, question or exclamation mark followed by a space ends a sentence; "v0.1.0" does not.
        for sentence in re.split(r"(?<=[.!?])\s+", text):
            if _words(sentence):
                sentences.append(sentence)
    return sentences


def _words(text: str) -> list[str]:
    return re.findall(r"[A-Za-z0-9]+(?:['\u2019.-][A-Za-z0-9]+)*", text)


def count_syllables(word: str) -> int:
    """An estimate of the syllables in an English word: groups of vowels, less a silent final e, at least one.
    A number, or a version such as v0.1.0, counts as one. Parts joined by a hyphen are counted one by one."""
    parts = [part for part in word.split("-") if part]
    if len(parts) > 1:
        return sum(count_syllables(part) for part in parts)
    letters = re.sub(r"[^a-z]", "", word.lower())
    if len(letters) <= 3:
        return 1
    letters = re.sub(r"(?:[^laeiouy]es|[^laeiouy]ed|[^laeiouy]e)$", "", letters)
    letters = re.sub(r"^y", "", letters)
    return max(1, len(re.findall(r"[aeiouy]{1,2}", letters)))


def reading_ease(markdown: str) -> float | None:
    """The Flesch Reading Ease score of the notes, or None when they have no words.
    206.835 - 1.015 x (words per sentence) - 84.6 x (syllables per word). Higher is easier; 60 is plain English."""
    sentences = _plain_text(markdown)
    words = [word for sentence in sentences for word in _words(sentence)]
    if not words:
        return None
    syllables = sum(count_syllables(word) for word in words)
    return 206.835 - 1.015 * (len(words) / len(sentences)) - 84.6 * (syllables / len(words))
